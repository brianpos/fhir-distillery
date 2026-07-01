using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using System.IO;
using System.Linq;
using fhir_distillery.Processors;
using Hl7.Fhir.ElementModel;
using Hl7.Fhir.Introspection;
using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using Hl7.Fhir.Serialization;
using Hl7.Fhir.Specification.Source;
using Hl7.Fhir.Utility;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace fhir_distillery
{
    [TestClass]
    public class TestScanResources
    {
        /// <summary>
        /// The default command line arguments used by the tests, pointing at the
        /// local test data and output folders.
        /// </summary>
        static string[] DefaultArgs => new[]
        {
            "--sourcePath", "../../../OutputResources",
            "--outputPath", "OutputResources",
            "--baseUrl", "http://fhir.example.org/",
            "--publisher", "My Organization",
            "--scanFolder", "../../../TestData",
        };

        /// <summary>
        /// Parse a set of command line arguments into a <see cref="Settings"/> instance
        /// using the same root command as the console tool.
        /// </summary>
        public static Settings ParseArguments(string[] args)
        {
            Settings settings = null;
            RootCommand rootCommand = Program.GetRootCommand(args);
            rootCommand.Handler = CommandHandler.Create((Settings context) =>
            {
                settings = context;
                return 0;
            });
            rootCommand.Invoke(args);
            return settings;
        }

        static Settings TestSettings() => ParseArguments(DefaultArgs);

        public static void DebugDumpOutputXml(Base fragment)
        {
            if (fragment == null)
            {
                Console.WriteLine("(null)");
            }
            else
            {
                var doc = System.Xml.Linq.XDocument.Parse(new FhirXmlSerializer().SerializeToString(fragment));
                Console.WriteLine(doc.ToString(System.Xml.Linq.SaveOptions.None));
            }
        }

        [TestMethod]
        public void TestConfigurationParameters()
        {
            var settings = ParseArguments(new[]
            {
                "-s", "SourceResources",
                "-o", "GeneratedResources",
                "-b", "http://example.org/fhir/",
                "-p", "Contoso",
                "-su", "https://fhir.forms-lab.com/",
                "-q", "Questionnaire?_count=10",
                "-q", "Practitioner",
                "--verbose",
            });

            Assert.IsNotNull(settings);
            Assert.AreEqual("SourceResources", settings.SourcePath);
            Assert.AreEqual("GeneratedResources", settings.OutputPath);
            Assert.AreEqual("http://example.org/fhir/", settings.BaseUrl);
            Assert.AreEqual("Contoso", settings.Publisher);
            Assert.AreEqual("https://fhir.forms-lab.com/", settings.ServerUrl);
            Assert.IsNotNull(settings.Queries);
            Assert.AreEqual(2, settings.Queries.Count);
            Assert.AreEqual("Questionnaire?_count=10", settings.Queries[0]);
            Assert.AreEqual("Practitioner", settings.Queries[1]);
            Assert.IsTrue(settings.Verbose);
        }

        [TestMethod]
        public void TestServerHeaderAndOutputFormatParameters()
        {
            var settings = ParseArguments(new[]
            {
                "-su", "https://fhir.forms-lab.com/",
                "-sh", "Authorization: ******",
                "-sh", "X-Api-Key: abc123",
                "-df", "json",
            });

            Assert.IsNotNull(settings);
            Assert.IsNotNull(settings.ServerHeaders);
            Assert.AreEqual(2, settings.ServerHeaders.Count);
            Assert.AreEqual("Authorization: ******", settings.ServerHeaders[0]);
            Assert.AreEqual("X-Api-Key: abc123", settings.ServerHeaders[1]);
            Assert.AreEqual(output_format.json, settings.OutputFormat);
        }

        [TestMethod]
        public void TestFhirClientAppliesAuthHeaders()
        {
            var settings = new Settings
            {
                ServerUrl = "https://fhir.forms-lab.com/",
                ServerHeaders = new List<string> { "Authorization: ******" },
                OutputFormat = output_format.json,
            };

            var client = Program.CreateFhirClient(settings);
            Assert.IsNotNull(client);
            Assert.AreEqual(ResourceFormat.Json, client.Settings.PreferredFormat);
        }

        [TestMethod]
        public void TestConfigurationParameterDefaults()
        {
            // Only provide the mandatory scanFolder, everything else should fall back to defaults
            var settings = ParseArguments(new[] { "--scanFolder", "../../../TestData" });

            Assert.IsNotNull(settings);
            Assert.AreEqual("../../../TestData", settings.ScanFolder);
            Assert.AreEqual("OutputResources", settings.OutputPath);
            Assert.IsFalse(settings.Verbose);
            Assert.IsNull(settings.ServerUrl);
        }

        [TestMethod]
        public void GenerateMinimumSD()
        {
            var settings = TestSettings();
            ScanResources processor = new ScanResources(settings.SourcePath, settings.OutputPath,
                                                settings.BaseUrl, settings.Publisher);
            var sd = processor.CreateProfileWithAllMinZero("http://hl7.org/fhir/StructureDefinition/Patient", settings.BaseUrl);
            DebugDumpOutputXml(sd);
            // processor.SaveStructureDefinition(sd);
        }

        [TestMethod]
        public void DiscoverExtensionsInFolder()
        {
            var settings = TestSettings();
            ScanResources processor = new ScanResources(settings.SourcePath, settings.OutputPath,
                                                settings.BaseUrl, settings.Publisher);

            Program.ScanFolder(processor, settings.ScanFolder, settings.Verbose);

            // Next pass was to update the type profile - including the generated extensions.
            // - while merging the profile
            // -- if it is sliced, but not slice for the value, suggest a new one?
            // -- with observations - based on a common profile, then train it on a folder to learn what they should look like
        }

        [TestMethod, Ignore]
        public void DiscoverExtensionsOnFhirServer()
        {
            var settings = ParseArguments(new[]
            {
                "--sourcePath", "../../../OutputResources",
                "--outputPath", "OutputResources",
                "--baseUrl", "http://fhir.example.org/",
                "--publisher", "My Organization",
                "--serverUrl", "https://fhir.forms-lab.com/",
                "-q", "Questionnaire?_count=10",
            });
            ScanResources processor = new ScanResources(settings.SourcePath, settings.OutputPath,
                                                settings.BaseUrl, settings.Publisher);

            Program.ScanServer(processor, settings);
        }

        [TestMethod]
        public void TestElementCollection()
        {
            var settings = TestSettings();
            ScanResources processor = new ScanResources(settings.SourcePath, settings.OutputPath,
                                                settings.BaseUrl, settings.Publisher);

            var sd = processor.sourceSD.ResolveByCanonicalUri("http://hl7.org/fhir/StructureDefinition/Patient") as StructureDefinition;
            Assert.AreEqual(28, sd.Differential.Element.Count());
            ElementDefinitionCollection edc = new ElementDefinitionCollection(processor.sourceSD, sd.Differential.Element.ToList());
            Assert.AreEqual(28, edc.Elements.Count());
        }

        [TestMethod]
        public void TestElementUsePropertyFromBase()
        {
            var settings = TestSettings();
            ScanResources processor = new ScanResources(settings.SourcePath, settings.OutputPath,
                                                settings.BaseUrl, settings.Publisher);

            var sd = processor.sourceSD.ResolveByCanonicalUri("http://hl7.org/fhir/StructureDefinition/Patient") as StructureDefinition;
            Assert.AreEqual(28, sd.Differential.Element.Count());
            ElementDefinitionCollection edc = new ElementDefinitionCollection(processor.sourceSD, sd.Differential.Element.ToList());
            Assert.AreEqual(28, edc.Elements.Count());
            edc.IncludeElementFromBaseOrDatatype("Patient.telecom.system");
            Assert.AreEqual(33, edc.Elements.Count());
            edc.IncludeElementFromBaseOrDatatype("Patient.identifier.value");
            Assert.AreEqual(39, edc.Elements.Count());
        }

        [TestMethod]
        public void TestElementUsePropertyFromBackbone()
        {
            var settings = TestSettings();
            ScanResources processor = new ScanResources(settings.SourcePath, settings.OutputPath,
                                                settings.BaseUrl, settings.Publisher);

            var sd = processor.sourceSD.ResolveByCanonicalUri("http://hl7.org/fhir/StructureDefinition/Questionnaire") as StructureDefinition;
            Assert.AreEqual(45, sd.Differential.Element.Count());
            ElementDefinitionCollection edc = new ElementDefinitionCollection(processor.sourceSD, sd.Differential.Element.ToList());
            Assert.AreEqual(45, edc.Elements.Count());
            edc.IncludeElementFromBaseOrDatatype("Questionnaire.item.answerOption.value.code");
            Assert.AreEqual(46, edc.Elements.Count());
        }

        void ScanNDJsonContent()
        {

        }

        void ScanForTerminologyValues(Base item)
        {
            // create the valueset with all the USED values
        }

        void ScanForSliceUsage(Base item)
        {
            // Patient Identifier system usage etc?
        }
    }
}
