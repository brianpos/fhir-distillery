using System;
using System.IO;
using System.Linq;
using fhir.distillery.test;
using Hl7.Fhir.ElementModel;
using Hl7.Fhir.Introspection;
using Hl7.Fhir.Model;
using Hl7.Fhir.Rest;
using Hl7.Fhir.Serialization;
using Hl7.Fhir.Specification.Source;
using Hl7.Fhir.Utility;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace fhir_distillery
{
    [TestClass]
    public class TestScanResources
    {
        static IConfiguration Configuration;
        [ClassInitialize]
        public static void TestInitialize(TestContext context)
        {
            var builder = new ConfigurationBuilder()
               .SetBasePath(new FileInfo(typeof(TestScanResources).Assembly.Location).DirectoryName)
               .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
            Configuration = builder.Build();
        }
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
        public void DiscoverExtensionsInFolderXml()
        {
            string sourcePath = Configuration.GetValue<string>("sourcePath");
            string outputPath = Configuration.GetValue<string>("outputPath");
            string canonicalBase = Configuration.GetValue<string>("defaults:baseurl");
            string publisher = Configuration.GetValue<string>("defaults:publisher");
            ScanResources processor = new ScanResources(sourcePath, outputPath,
                                                canonicalBase, publisher);

            string exampleResourcesPath = Configuration.GetValue<string>("scanExamplesInPath");
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
                                processor.ScanForExtensions(null, entry.ToTypedElement());
                            }
                        }
                    }
                    else
                    {
                        processor.ScanForExtensions(null, resource.ToTypedElement());
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

        [TestMethod]
        public void DiscoverExtensionsOnFhirServer()
        {
            string sourcePath = Configuration.GetValue<string>("sourcePath");
            string outputPath = Configuration.GetValue<string>("outputPath");
            string canonicalBase = Configuration.GetValue<string>("defaults:baseurl");
            string publisher = Configuration.GetValue<string>("defaults:publisher");
            ScanResources processor = new ScanResources(sourcePath, outputPath,
                                                canonicalBase, publisher);

            var settings = Configuration.GetSection("scanserver").Get<ScanServerSettings>();
            var server = new FhirClient(settings.baseurl, false);

            int createdResources = 0;
            foreach (var query in settings.queries)
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
                                System.Diagnostics.Trace.WriteLine($"  -->{entry.ResourceType}/{entry.Id}");
                                processor.ScanForExtensions(null, entry.ToTypedElement());
                                createdResources ++;
                            }
                        }
                        if (batch.NextLink == null)
                            break;
                        batch = server.Continue(batch);
                    }
                    while (true);
                }
                catch (FhirOperationException ex)
                {
                    DebugDumpOutputXml(ex.Outcome);
                }
            }
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
