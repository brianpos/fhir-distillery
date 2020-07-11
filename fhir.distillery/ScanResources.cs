using System;
using System.IO;
using System.Linq;
using Hl7.Fhir.ElementModel;
using Hl7.Fhir.Introspection;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Hl7.Fhir.Specification.Source;
using Hl7.Fhir.Utility;

namespace fhir_distillery
{
    public class ScanResources
    {
        public ScanResources(string existingSDpath, string newSDpath, string outputProfileBaseUri, string publisher)
        {
            _publisher = publisher;
            _outputProfileBaseUri = outputProfileBaseUri;
            _outputProfilePath = newSDpath;
            ds = new DirectorySource(_outputProfilePath, new DirectorySourceSettings() { IncludeSubDirectories = true });
            sourceSD = new CachedResolver(
                    new MultiResolver(
                        ds,
                        new DirectorySource(existingSDpath, new DirectorySourceSettings() { IncludeSubDirectories = true }),
                        new ZipSource(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "specification.zip"))
                    )
                );
        }
        private string _outputProfilePath;
        DirectorySource ds;
        CachedResolver sourceSD;
        string _outputProfileBaseUri;
        string _publisher;

        public void DiscoverExtensions()
        {
            // Do ndjson too!
            string exampleResourcesPath = @"C:\git\MySL.FhirIG\examples";
            foreach (string file in Directory.EnumerateFiles(exampleResourcesPath, "*.xml", SearchOption.AllDirectories))
            {
                try
                {
                    var resource = new FhirXmlParser().Parse<Resource>(File.ReadAllText(file));
                    System.Diagnostics.Trace.WriteLine($"{file} {resource.ResourceType}/{resource.Id}");
                    if (resource is Bundle bundle)
                    {
                        foreach (var entry in bundle.Entry.Select(e => e.Resource))
                        {
                            if (entry != null)
                            {
                                System.Diagnostics.Trace.WriteLine($"  -->{entry.ResourceType}/{entry.Id}");
                                ScanForExtensions(null, entry.ToTypedElement());
                            }
                        }
                    }
                    else
                    {
                        ScanForExtensions(null, resource.ToTypedElement());
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"{file}");
                    System.Diagnostics.Trace.WriteLine($"  ==> Exception {ex.Message}");
                }
            }

            // Next pass was to update the type profile - including the generated extensions.
            // - while merging the profile
            // -- if it is sliced, but not slice for the value, suggest a new one?
            // -- with observations - based on a common profile, then train it on a folder to learn what they should look like
        }

        void ScanForTerminologyValues(Base item)
        {
            // create the valueset with all the USED values
        }

        void ScanForSliceUsage(Base item)
        {
            // Patient Identifier system usage etc?
        }


        public void ScanForExtensions(string basePath, ITypedElement item)
        {
            string context = string.IsNullOrEmpty(basePath) ? item.Name : basePath + "." + item.Name;

            // Check for extensions
            if (item.ToPoco() is IExtendable extendable)
            {
                if (extendable.Extension != null)
                {
                    foreach (var ext in extendable.Extension)
                    {
                        var instSD = sourceSD.ResolveByCanonicalUri(ext.Url) as StructureDefinition;
                        if (instSD == null)
                        {
                            Console.WriteLine($"  x==> Extension {ext.Url} not found");
                            System.Diagnostics.Trace.WriteLine($"  x==> Extension {ext.Url} not found");
                            DeriveExtensionFromUsage(ext.Url, ext.Value.TypeName, context);
                        }
                        else
                        {
                            System.Diagnostics.Trace.WriteLine($"  ---> Extension {ext.Url} exists");
                            // Check that this extension is defineed to support this
                            UpdateExtensionStructureDefinitionForUsage(instSD, ext.Value.TypeName, context);
                        }
                    }
                }
            }
            var children = item.Children();
            if (children != null)
            {
                foreach (var child in children)
                {
                    if (child.ToPoco() is Resource r)
                        ScanForExtensions(null, r.ToTypedElement());
                    else
                        ScanForExtensions(context, child);
                }
            }
        }

        private void UpdateExtensionStructureDefinitionForUsage(StructureDefinition sd, string typeName, string context)
        {
            bool updated = false;
            // Check the context (and add it)
            if (!sd.Context.Any(uc => uc.Type == StructureDefinition.ExtensionContextType.Extension && uc.Expression == context))
            {
                // Need to include this context too
                sd.Context.Add(new StructureDefinition.ContextComponent()
                {
                    Type = StructureDefinition.ExtensionContextType.Extension,
                    Expression = context
                });
                updated = true;
            }

            // Check the datatype
            if (!sd.Differential.Element.Any(e => e.Path == "Extension.value[x]" && e.Type.Any(t => t.Code == typeName)))
            {
                sd.Differential.Element.FirstOrDefault(e => e.Path == "Extension.value[x]").Type.Add(new ElementDefinition.TypeRefComponent() 
                {
                    Code = typeName
                });
                updated = true;
            }

            // Store the StructureDefinition
            if (updated)
                SaveStructureDefinition(sd);
        }

        private void DeriveExtensionFromUsage(string url, string typeName, string context)
        {
            StructureDefinition sd = new StructureDefinition()
            {
                Url = url,
                Publisher = _publisher,
                Status = PublicationStatus.Draft,
                FhirVersion = FHIRVersion.N0_4_0,
                Kind = StructureDefinition.StructureDefinitionKind.ComplexType,
                Abstract = false,
                BaseDefinition = "http://hl7.org/fhir/StructureDefinition/Extension",
                Derivation = StructureDefinition.TypeDerivationRule.Constraint
            };
            sd.Context.Add(new StructureDefinition.ContextComponent()
            {
                Type = StructureDefinition.ExtensionContextType.Extension,
                Expression = context
            });

            // Derive a resource ID from the extension URL
            Uri t = new Uri(url);
            sd.Id = t.Segments.Last();


            // Add in the Extension element definitions
            sd.Differential = new StructureDefinition.DifferentialComponent();
            sd.Differential.Element.Add(new ElementDefinition()
            {
                ElementId = "Extension",
                Path = "Extension"
            });
            sd.Differential.Element.Add(new ElementDefinition()
            {
                ElementId = "Extension.url",
                Path = "Extension.url",
                Fixed = new FhirUrl(url) // This is really not required anymore
            });
            sd.Differential.Element.Add(new ElementDefinition()
            {
                ElementId = "Extension.value[x]",
                Path = "Extension.value[x]",
                Type = new System.Collections.Generic.List<ElementDefinition.TypeRefComponent>(new[]
                {
                    new ElementDefinition.TypeRefComponent() { Code = typeName }
                }),
                MustSupport = true
            });

            SaveStructureDefinition(sd);
        }

        private void SaveStructureDefinition(StructureDefinition sd)
        {
            // Ensure the folder exists
            Uri t = new Uri(sd.Url);
            string profileOutputDirectory = Path.Combine(_outputProfilePath, t.Host);
            if (!Directory.Exists(profileOutputDirectory))
                Directory.CreateDirectory(profileOutputDirectory);
            // Now output the file
            System.IO.File.WriteAllText($"{profileOutputDirectory}/StructureDefinition-{sd.Id}.xml", new FhirXmlSerializer(new SerializerSettings() { AppendNewLine = true, Pretty = true }).SerializeToString(sd));

            // And add it to our resolver
            sourceSD.InvalidateByCanonicalUri(sd.Url);
            ds.Refresh();

            // And check that it comes back...
            var instSD = sourceSD.ResolveByUri(sd.Url) as StructureDefinition;
            if (instSD == null)
            {
                Console.WriteLine($"Was not able to resolve the newly created extension");
            }
        }

        void ScanResourceForPropertyUsage(Resource resource)
        {
            var instSD = sourceSD.ResolveByUri("http://hl7.org/fhir/StructureDefinition/" + resource.TypeName) as StructureDefinition;
            if (instSD == null)
            {
                Console.WriteLine($"Unable to load the StructureDefinition for {resource.TypeName}");
                return;
            }
            var sd = instSD.DeepCopy() as StructureDefinition;
            sd.Url = this._outputProfileBaseUri + $"StructureDefinition-{resource.TypeName}";
            // Check for extensions
        }

    }
    public class PropertyUsage
    {
        public int UsageCount { get; set; } = 0;
        public void Increment() { UsageCount++; }
    }

    public static class ED_ExtensionMethods
    {
        public static void IncrementUsage(this ElementDefinition ed)
        {
            if (ed == null)
                return;
            if (ed.HasAnnotation<PropertyUsage>())
            {
                ed.Annotation<PropertyUsage>().Increment();
            }
            else
            {
                ed.SetAnnotation(new PropertyUsage() { UsageCount = 1 });
            }
        }
    }
}
