using System;
using System.IO;
using System.Linq;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace fhir_distillery
{
    [TestClass]
    public class ScanResources
    {
        [TestMethod]
        public void DiscoverResources()
        {
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
                                System.Diagnostics.Trace.WriteLine($"  -->{entry.ResourceType}/{entry.Id}");
                        }
                    }
                }
                catch(Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine($"{file}");
                    System.Diagnostics.Trace.WriteLine($"  ==> Exception {ex.Message}");
                }
            }
        }
    }
}
