using System;
using System.Linq;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace fhir_distillery
{
    /// <summary>
    /// Exercises the <see cref="LogicalModelGenerator"/> using the project's own
    /// <see cref="Settings"/> class as the C# type to project into a FHIR logical model.
    /// </summary>
    [TestClass]
    public class TestLogicalModelGenerator
    {
        static LogicalModelGenerator CreateGenerator()
            => new LogicalModelGenerator("http://fhir.example.org/", "My Organization");

        [TestMethod]
        public void GenerateLogicalModelFromSettingsClass()
        {
            var generator = CreateGenerator();
            var sd = generator.GenerateLogicalModel(typeof(Settings));

            Assert.IsNotNull(sd);
            Assert.AreEqual("Settings", sd.Name);
            Assert.AreEqual("Settings", sd.Id);
            Assert.AreEqual("http://fhir.example.org/StructureDefinition/Settings", sd.Url);
            Assert.AreEqual(sd.Url, sd.Type);
            Assert.AreEqual(StructureDefinition.StructureDefinitionKind.Logical, sd.Kind);
            Assert.AreEqual(StructureDefinition.TypeDerivationRule.Specialization, sd.Derivation);
            Assert.AreEqual("http://hl7.org/fhir/StructureDefinition/Base", sd.BaseDefinition);
            Assert.AreEqual(PublicationStatus.Draft, sd.Status);
            Assert.AreEqual("My Organization", sd.Publisher);
            Assert.AreEqual(false, sd.Abstract);
        }

        [TestMethod]
        public void GeneratedModelHasOneElementPerProperty()
        {
            var generator = CreateGenerator();
            var sd = generator.GenerateLogicalModel(typeof(Settings));

            int propertyCount = typeof(Settings).GetProperties().Length;

            // Root element + one element per declared public property
            Assert.AreEqual(propertyCount + 1, sd.Differential.Element.Count);
            Assert.AreEqual("Settings", sd.Differential.Element.First().Path);

            // Every property should be represented (camelCased under the root path)
            foreach (var property in typeof(Settings).GetProperties())
            {
                string expectedPath = "Settings." + char.ToLowerInvariant(property.Name[0]) + property.Name.Substring(1);
                Assert.IsTrue(sd.Differential.Element.Any(e => e.Path == expectedPath),
                    $"Expected an element for {expectedPath}");
            }
        }

        [TestMethod]
        public void GeneratedModelMapsClrTypesAndCardinality()
        {
            var generator = CreateGenerator();
            var sd = generator.GenerateLogicalModel(typeof(Settings));

            // string property -> string, optional (nullable reference type) so min = 0, max = 1
            var sourcePath = sd.Differential.Element.Single(e => e.Path == "Settings.sourcePath");
            Assert.AreEqual("string", sourcePath.Type.Single().Code);
            Assert.AreEqual(0, sourcePath.Min);
            Assert.AreEqual("1", sourcePath.Max);

            // bool property -> boolean, non-nullable value type so min = 1
            var verbose = sd.Differential.Element.Single(e => e.Path == "Settings.verbose");
            Assert.AreEqual("boolean", verbose.Type.Single().Code);
            Assert.AreEqual(1, verbose.Min);
            Assert.AreEqual("1", verbose.Max);

            // List<string> property -> string with max = *
            var queries = sd.Differential.Element.Single(e => e.Path == "Settings.queries");
            Assert.AreEqual("string", queries.Type.Single().Code);
            Assert.AreEqual("*", queries.Max);
        }

        [TestMethod]
        public void GeneratedModelIncludesDocumentationFromXmlComments()
        {
            var generator = CreateGenerator();
            var sd = generator.GenerateLogicalModel(typeof(Settings));

            // The Settings.OutputPath property carries a /// <summary> comment; when the sibling
            // XML doc file is present it should be projected into the element description.
            var outputPath = sd.Differential.Element.Single(e => e.Path == "Settings.outputPath");
            Assert.IsFalse(string.IsNullOrWhiteSpace(outputPath.Short),
                "Expected the element short text to be populated from the XML doc comment");
            StringAssert.Contains(outputPath.Short, "generated");
        }

        [TestMethod]
        public void GeneratedModelSerializesToValidFhirJson()
        {
            var generator = CreateGenerator();
            var sd = generator.GenerateLogicalModel(typeof(Settings));

            // Round-trip through the Firely serializer to prove the model is well formed
            var json = new FhirJsonSerializer(new SerializerSettings { Pretty = true }).SerializeToString(sd);
            Assert.IsFalse(string.IsNullOrWhiteSpace(json));

            var parsed = new FhirJsonParser().Parse<StructureDefinition>(json);
            Assert.AreEqual(sd.Url, parsed.Url);
            Assert.AreEqual(sd.Differential.Element.Count, parsed.Differential.Element.Count);
        }
    }
}
