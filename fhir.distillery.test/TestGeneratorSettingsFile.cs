using System.IO;
using System.Linq;
using Hl7.Fhir.Model;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace fhir_distillery
{
    /// <summary>
    /// Exercises the JSON settings file <c>defaults</c>/<c>overrides</c> sections and their application
    /// to a generated <see cref="StructureDefinition"/>.
    /// </summary>
    [TestClass]
    public class TestGeneratorSettingsFile
    {
        const string SampleJson = @"{
  ""assemblyPaths"": [""./bin/MyModels.dll""],
  ""defaults"": {
    ""status"": ""active"",
    ""experimental"": true,
    ""publisher"": ""My Organization"",
    ""version"": ""0.1.0"",
    ""jurisdiction"": [""urn:iso:std:iso:3166#AU""],
    ""copyright"": ""(c) My Organization""
  },
  ""overrides"": {
    ""Settings"": {
      ""url"": ""http://fhir.example.org/StructureDefinition/coverage"",
      ""title"": ""Internal Coverage record"",
      ""short"": ""A payer coverage line"",
      ""status"": ""draft"",
      ""elements"": {
        ""sourcePath"": {
          ""short"": ""Payer-assigned member id"",
          ""definition"": ""The identifier the payer uses for this member."",
          ""min"": 1,
          ""max"": ""1"",
          ""mustSupport"": true,
          ""binding"": { ""strength"": ""required"", ""valueSet"": ""http://fhir.example.org/ValueSet/member-id-type"" }
        },
        ""outputPath"": { ""max"": ""0"" }
      }
    }
  }
}";

        static StructureDefinition GenerateSettingsModel()
            => new LogicalModelGenerator("http://fhir.example.org/", "My Organization")
                .GenerateLogicalModel(typeof(Settings));

        static GeneratorSettingsFile LoadSample()
        {
            var path = Path.Combine(Path.GetTempPath(), $"distillery-settings-{System.Guid.NewGuid():N}.json");
            File.WriteAllText(path, SampleJson);
            try
            {
                return GeneratorSettingsFile.Load(path);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [TestMethod]
        public void MissingSettingsFileYieldsEmptyInstance()
        {
            var settings = GeneratorSettingsFile.Load("./does-not-exist.json");
            Assert.IsNotNull(settings);
            Assert.IsNull(settings.Defaults);
            Assert.IsNull(settings.Overrides);
        }

        [TestMethod]
        public void AppliesDefaultsToGeneratedModel()
        {
            var sd = GenerateSettingsModel();
            // Clear the generator-provided publisher so the default fills it in
            sd.Publisher = null;

            LoadSample().Apply(sd);

            Assert.AreEqual(PublicationStatus.Draft, sd.Status, "override status wins over default");
            Assert.AreEqual("0.1.0", sd.Version);
            Assert.AreEqual("My Organization", sd.Publisher);
            Assert.AreEqual(true, sd.Experimental);
            Assert.AreEqual("(c) My Organization", sd.Copyright);
            Assert.AreEqual("urn:iso:std:iso:3166", sd.Jurisdiction.Single().Coding.Single().System);
            Assert.AreEqual("AU", sd.Jurisdiction.Single().Coding.Single().Code);
        }

        [TestMethod]
        public void AppliesTypeLevelOverrides()
        {
            var sd = GenerateSettingsModel();

            LoadSample().Apply(sd);

            Assert.AreEqual("http://fhir.example.org/StructureDefinition/coverage", sd.Url);
            Assert.AreEqual(sd.Url, sd.Type);
            Assert.AreEqual("Internal Coverage record", sd.Title);
            Assert.AreEqual("A payer coverage line", sd.Differential.Element.First().Short);
        }

        [TestMethod]
        public void AppliesElementLevelOverrides()
        {
            var sd = GenerateSettingsModel();

            LoadSample().Apply(sd);

            var sourcePath = sd.Differential.Element.Single(e => e.Path == "Settings.sourcePath");
            Assert.AreEqual("Payer-assigned member id", sourcePath.Short);
            Assert.AreEqual("The identifier the payer uses for this member.", sourcePath.Definition);
            Assert.AreEqual(1, sourcePath.Min);
            Assert.AreEqual("1", sourcePath.Max);
            Assert.AreEqual(true, sourcePath.MustSupport);
            Assert.AreEqual(BindingStrength.Required, sourcePath.Binding.Strength);
            Assert.AreEqual("http://fhir.example.org/ValueSet/member-id-type", sourcePath.Binding.ValueSet);

            var outputPath = sd.Differential.Element.Single(e => e.Path == "Settings.outputPath");
            Assert.AreEqual("0", outputPath.Max);
        }
    }
}
