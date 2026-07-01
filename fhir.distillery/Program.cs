using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Hl7.Fhir.ElementModel;
using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using Hl7.Fhir.Serialization;
using Microsoft.Extensions.Configuration;

namespace fhir_distillery
{
    public class Program
    {
        /// <summary>Main entry-point for this application.</summary>
        /// <param name="args">An array of command-line argument strings.</param>
        public static async Task<int> Main(string[] args)
        {
            Console.WriteLine("HL7 FHIR Distillery");
            Console.WriteLine("--------------------------------------");

            RootCommand rootCommand = GetRootCommand(args);

            rootCommand.Handler = CommandHandler.Create((Settings context) =>
            {
                try
                {
                    return RunDistillery(context);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                    return -1;
                }
            });
            return await rootCommand.InvokeAsync(args);
        }

        /// <summary>
        /// Build the root command with all of the command line options, seeding the
        /// defaults from any environment variables that may be present.
        /// </summary>
        public static RootCommand GetRootCommand(string[] args)
        {
            // Seed the option defaults from the environment (command line values override these)
            IConfiguration configuration = new ConfigurationBuilder()
                    .AddEnvironmentVariables()
                    .Build();
            var settings = configuration.Get<Settings>() ?? new Settings();

            var sourcePathOption = new Option<string>(["-s", "--sourcePath"], () => settings.SourcePath, "The path containing any existing StructureDefinitions to use while scanning");

            var rootCommand = new RootCommand("HL7 FHIR Distillery - derives StructureDefinitions from example resources")
            {
                sourcePathOption,
                new Option<string>(["-o", "--outputPath"], () => settings.OutputPath ?? "OutputResources", "The folder where the generated/updated StructureDefinitions are written"),
                new Option<string>(["-b", "--baseUrl"], () => settings.BaseUrl, "The canonical base URL to use for the generated StructureDefinitions (e.g. http://fhir.example.org/)"),
                new Option<string>(["-p", "--publisher"], () => settings.Publisher, "The publisher value to stamp onto the generated StructureDefinitions"),
                new Option<string>(["-sf", "--scanFolder"], () => settings.ScanFolder, "A local folder of example resources (xml/json) to scan for extensions and property usage"),
                new Option<string>(["-su", "--serverUrl"], () => settings.ServerUrl, "The base URL of a FHIR Server to scan for extensions and property usage"),
                new Option<List<string>>(["-q", "--queries"], () => settings.Queries, "The queries to execute against the FHIR Server when scanning (e.g. Questionnaire?_count=10)"),
                new Option<bool>(["--verbose"], () => settings.Verbose, "Provide verbose diagnostic output while processing"),
            };

            // Check that there is at least a source of examples to scan (folder or server)
            rootCommand.AddValidator((result) =>
            {
                // The gen-logical subcommand has its own inputs and does not scan examples
                if (args.Contains("gen-logical"))
                    return;
                List<string> scanFolderAliases = ["-sf", "--scanFolder"];
                List<string> serverAliases = ["-su", "--serverUrl"];
                if (!args.Any(a => scanFolderAliases.Contains(a)) && !args.Any(a => serverAliases.Contains(a)))
                    result.ErrorMessage = "The scanFolder and serverUrl are both missing, please provide one or the other to indicate what to scan for extensions and property usage";
            });

            rootCommand.AddCommand(GetGenerateLogicalCommand(settings));
            return rootCommand;
        }

        /// <summary>
        /// Build the <c>gen-logical</c> subcommand that reflects over one or more assemblies and emits
        /// FHIR logical models (plus any CodeSystem/ValueSet resources derived from enum properties).
        /// </summary>
        public static Command GetGenerateLogicalCommand(Settings settings)
        {
            var command = new Command("gen-logical", "Generate FHIR logical models (and enum CodeSystems/ValueSets) from C# classes")
            {
                new Option<List<string>>(["--assemblyPaths", "-a", "--assembly"], "One or more compiled assemblies (.dll) to reflect over") { IsRequired = true },
                new Option<List<string>>(["--typeNames", "-t", "--type"], "Type name patterns to output (wildcards supported); empty = all [FhirType] types"),
                new Option<string>(["-o", "--outputPath"], () => settings.OutputPath ?? "OutputResources", "The folder where the generated resources are written"),
                new Option<string>(["-b", "--baseUrl"], () => settings.BaseUrl, "The canonical base URL to use for the generated resources (e.g. http://fhir.example.org/)"),
                new Option<string>(["-p", "--publisher"], () => settings.Publisher, "The publisher value to stamp onto the generated resources"),
                new Option<bool>(["--verbose"], () => settings.Verbose, "Provide verbose diagnostic output while processing"),
            };

            command.Handler = CommandHandler.Create((Settings context) =>
            {
                try
                {
                    return RunLogicalModelGeneration(context);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                    return -1;
                }
            });
            return command;
        }

        /// <summary>Run the distillery over the configured examples folder and/or FHIR server.</summary>
        public static int RunDistillery(Settings settings)
        {
            // Ensure the output folder exists (the resolver requires the directory to be present)
            if (!string.IsNullOrEmpty(settings.OutputPath) && !Directory.Exists(settings.OutputPath))
                Directory.CreateDirectory(settings.OutputPath);

            // If no separate source of existing StructureDefinitions was provided, use the output folder
            string sourcePath = string.IsNullOrEmpty(settings.SourcePath) ? settings.OutputPath : settings.SourcePath;

            ScanResources processor = new ScanResources(sourcePath, settings.OutputPath,
                                                settings.BaseUrl, settings.Publisher);

            if (!string.IsNullOrEmpty(settings.ScanFolder))
                ScanFolder(processor, settings.ScanFolder, settings.Verbose);

            if (!string.IsNullOrEmpty(settings.ServerUrl))
                ScanServer(processor, settings);

            return 0;
        }

        /// <summary>
        /// Reflect over the configured assemblies and emit FHIR logical models for the selected types,
        /// together with any CodeSystem/ValueSet resources derived from their enum-typed properties.
        /// </summary>
        public static int RunLogicalModelGeneration(Settings settings)
        {
            if (settings.AssemblyPaths == null || !settings.AssemblyPaths.Any())
            {
                Console.WriteLine("No assemblies were provided; use --assembly to point at one or more .dll files");
                return -1;
            }

            // Ensure the output folder exists
            if (!string.IsNullOrEmpty(settings.OutputPath) && !Directory.Exists(settings.OutputPath))
                Directory.CreateDirectory(settings.OutputPath);

            var generator = new LogicalModelGenerator(settings.BaseUrl, settings.Publisher);

            var types = DiscoverTypes(settings).ToList();
            if (!types.Any())
            {
                Console.WriteLine("No matching types were found to generate logical models from");
                return 0;
            }

            var serializer = new FhirXmlSerializer(new SerializerSettings() { AppendNewLine = true, Pretty = true });
            int modelCount = 0, terminologyCount = 0;
            foreach (var type in types)
            {
                var result = generator.GenerateModel(type);
                if (settings.Verbose)
                    Console.WriteLine($"Generated logical model {result.StructureDefinition.Url} ({type.FullName})");
                foreach (var resource in result.AllResources())
                {
                    SaveResource(settings.OutputPath, resource, serializer);
                    if (resource is StructureDefinition)
                        modelCount++;
                    else
                        terminologyCount++;
                }
            }
            Console.WriteLine($"Generated {modelCount} logical model(s) and {terminologyCount} terminology resource(s) into {settings.OutputPath}");
            return 0;
        }

        /// <summary>Load the configured assemblies and select the types to generate logical models for.</summary>
        static IEnumerable<Type> DiscoverTypes(Settings settings)
        {
            var patterns = settings.TypeNames ?? new List<string>();
            var results = new List<Type>();
            foreach (var assemblyPath in settings.AssemblyPaths)
            {
                Assembly assembly;
                try
                {
                    assembly = Assembly.LoadFrom(Path.GetFullPath(assemblyPath));
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  ==> Unable to load assembly {assemblyPath}: {ex.Message}");
                    continue;
                }

                Type[] assemblyTypes;
                try
                {
                    assemblyTypes = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    assemblyTypes = ex.Types.Where(t => t != null).ToArray();
                }

                foreach (var type in assemblyTypes.Where(t => t.IsClass && !t.IsAbstract && (t.IsPublic || t.IsNestedPublic)))
                {
                    bool selected = patterns.Any()
                        ? patterns.Any(p => MatchesTypePattern(type, p))
                        : type.GetCustomAttribute<Hl7.Fhir.Introspection.FhirTypeAttribute>() != null;
                    if (selected)
                        results.Add(type);
                }
            }
            return results.Distinct();
        }

        /// <summary>Match a type's full name against a glob-style (or <c>regex:</c>-prefixed) pattern.</summary>
        static bool MatchesTypePattern(Type type, string pattern)
        {
            if (string.IsNullOrEmpty(pattern))
                return false;
            string fullName = type.FullName ?? type.Name;
            if (pattern.StartsWith("regex:", StringComparison.OrdinalIgnoreCase))
                return System.Text.RegularExpressions.Regex.IsMatch(fullName, pattern.Substring("regex:".Length));
            if (string.Equals(fullName, pattern, StringComparison.Ordinal) || string.Equals(type.Name, pattern, StringComparison.Ordinal))
                return true;
            string regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return System.Text.RegularExpressions.Regex.IsMatch(fullName, regex)
                || System.Text.RegularExpressions.Regex.IsMatch(type.Name, regex);
        }

        /// <summary>Serialize a generated resource to <c>{ResourceType}-{id}.xml</c> under the output folder.</summary>
        static void SaveResource(string outputPath, Resource resource, FhirXmlSerializer serializer)
        {
            string fileName = $"{resource.TypeName}-{resource.Id}.xml";
            File.WriteAllText(Path.Combine(outputPath ?? ".", fileName), serializer.SerializeToString(resource));
        }


        public static void ScanFolder(ScanResources processor, string scanFolder, bool verbose = false)
        {
            foreach (string file in Directory.EnumerateFiles(scanFolder, "*.xml", SearchOption.AllDirectories))
            {
                try
                {
                    var resource = new FhirXmlParser().Parse<Resource>(File.ReadAllText(file));
                    DiscoverInFile(processor, file, resource, verbose);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"{file}");
                    Console.WriteLine($"  ==> Exception {ex.Message}");
                }
            }
            foreach (string file in Directory.EnumerateFiles(scanFolder, "*.json", SearchOption.AllDirectories))
            {
                try
                {
                    var resource = new FhirJsonParser().Parse<Resource>(File.ReadAllText(file));
                    DiscoverInFile(processor, file, resource, verbose);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"{file}");
                    Console.WriteLine($"  ==> Exception {ex.Message}");
                }
            }
        }

        static void DiscoverInFile(ScanResources processor, string file, Resource resource, bool verbose)
        {
            if (verbose)
                Console.WriteLine($"{file} {resource.TypeName}/{resource.Id}");
            if (resource is Bundle bundle)
            {
                foreach (var entry in bundle.Entry.Select(e => e.Resource))
                {
                    if (entry != null)
                    {
                        if (verbose)
                            Console.WriteLine($"  -->{entry.TypeName}/{entry.Id}");
                        processor.ScanForExtensions(null, entry.ToTypedElement(), null);
                    }
                }
            }
            else
            {
                processor.ScanForExtensions(null, resource.ToTypedElement(), null);
            }
        }

        /// <summary>Scan a live FHIR server for extensions and property usage.</summary>
        public static void ScanServer(ScanResources processor, Settings settings)
        {
            var server = new FhirClient(settings.ServerUrl, new FhirClientSettings() { VerifyFhirVersion = false });
            foreach (var query in settings.Queries ?? Enumerable.Empty<string>())
            {
                try
                {
                    Bundle batch = server.Get(server.Endpoint + query) as Bundle;
                    do
                    {
                        foreach (var entry in batch.Entry.Select(e => e.Resource))
                        {
                            if (entry != null)
                            {
                                if (settings.Verbose)
                                    Console.WriteLine($"  -->{entry.TypeName}/{entry.Id}");
                                processor.ScanForExtensions(null, entry.ToTypedElement(), null);
                            }
                        }
                        break;
                    }
                    while (true);
                }
                catch (FhirOperationException ex)
                {
                    Console.WriteLine($"  ==> Exception {ex.Message}");
                }
            }
        }
    }
}
