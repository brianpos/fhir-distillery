using System;
using System.Linq;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Hl7.Fhir.Utility;
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

        /// <summary>A sample enum whose members carry only <c>///</c> doc comments (no Firely annotations).</summary>
        public enum SampleColour
        {
            /// <summary>The colour red</summary>
            Red,
            /// <summary>The colour green</summary>
            Green,
            /// <summary>The colour blue</summary>
            Blue
        }

        /// <summary>A sample POCO with an enum-typed property, used to exercise terminology generation.</summary>
        public class SamplePoco
        {
            /// <summary>The chosen colour</summary>
            public SampleColour Colour { get; set; }

            /// <summary>An optional secondary colour</summary>
            public SampleColour? SecondaryColour { get; set; }
        }

        [TestMethod]
        public void GeneratesCodeSystemAndValueSetForEnumProperty()
        {
            var generator = CreateGenerator();
            var result = generator.GenerateModel(typeof(SamplePoco));

            // One CodeSystem and one ValueSet for the SampleColour enum (deduplicated across the two properties)
            Assert.AreEqual(1, result.CodeSystems.Count);
            Assert.AreEqual(1, result.ValueSets.Count);

            var codeSystem = result.CodeSystems.Single();
            Assert.AreEqual("http://fhir.example.org/CodeSystem/SampleColour", codeSystem.Url);
            Assert.AreEqual(CodeSystemContentMode.Complete, codeSystem.Content);
            Assert.AreEqual(3, codeSystem.Concept.Count);

            // Codes come from the member names, displays/definitions from the XML doc comments
            var red = codeSystem.Concept.Single(c => c.Code == "Red");
            Assert.AreEqual("The colour red", red.Display);

            var valueSet = result.ValueSets.Single();
            Assert.AreEqual("http://fhir.example.org/ValueSet/SampleColour", valueSet.Url);
            Assert.AreEqual(codeSystem.Url, valueSet.Compose.Include.Single().System);
        }

        [TestMethod]
        public void EnumElementIsBoundToGeneratedValueSet()
        {
            var generator = CreateGenerator();
            var result = generator.GenerateModel(typeof(SamplePoco));
            var sd = result.StructureDefinition;

            var colour = sd.Differential.Element.Single(e => e.Path == "SamplePoco.colour");
            Assert.AreEqual("code", colour.Type.Single().Code);
            Assert.IsNotNull(colour.Binding);
            Assert.AreEqual(BindingStrength.Required, colour.Binding.Strength);
            Assert.AreEqual("http://fhir.example.org/ValueSet/SampleColour", colour.Binding.ValueSet);
        }

        [TestMethod]
        public void FirelyAnnotatedEnumReferencesExistingValueSet()
        {
            var generator = CreateGenerator();
            var result = new LogicalModelResult();

            // PublicationStatus carries the Firely [FhirEnumeration] attribute naming a canonical value set,
            // so it should be referenced rather than regenerated under our base URL.
            var valueSetUri = generator.GenerateEnumTerminology(typeof(PublicationStatus), result);

            Assert.AreEqual("http://hl7.org/fhir/ValueSet/publication-status", valueSetUri);
            Assert.AreEqual(0, result.CodeSystems.Count);
            Assert.AreEqual(0, result.ValueSets.Count);
        }

        /// <summary>A sample enum annotated with the Firely SDK attributes (but no <c>[FhirEnumeration]</c>).</summary>
        public enum SampleStatus
        {
            [EnumLiteral("act")]
            [Hl7.Fhir.Utility.Description("Is active")]
            Active,
            [EnumLiteral("inact")]
            [Hl7.Fhir.Utility.Description("No longer active")]
            Inactive
        }

        public class SampleStatusPoco
        {
            public SampleStatus Status { get; set; }
        }

        [TestMethod]
        public void UsesFirelyEnumLiteralAndDescriptionForCodesAndDisplays()
        {
            var generator = CreateGenerator();
            var result = generator.GenerateModel(typeof(SampleStatusPoco));

            var codeSystem = result.CodeSystems.Single();
            // Codes come from [EnumLiteral], displays from [Description]
            var active = codeSystem.Concept.Single(c => c.Display == "Is active");
            Assert.AreEqual("act", active.Code);
            var inactive = codeSystem.Concept.Single(c => c.Display == "No longer active");
            Assert.AreEqual("inact", inactive.Code);
        }

        /// <summary>A sample POCO exercising serialization attributes: XML attribute/text and ignored properties.</summary>
        public class SerializationAttributesPoco
        {
            /// <summary>Rendered as an XML attribute</summary>
            [System.Xml.Serialization.XmlAttribute]
            public string Code { get; set; }

            /// <summary>Rendered as XML element text</summary>
            [System.Xml.Serialization.XmlText]
            public string Value { get; set; }

            /// <summary>A normal element (no special representation)</summary>
            public string Display { get; set; }

            /// <summary>Ignored by the JSON serializer, so it should not be modelled</summary>
            [System.Text.Json.Serialization.JsonIgnore]
            public string JsonOnlyIgnored { get; set; }

            /// <summary>Ignored by the XML serializer, so it should not be modelled</summary>
            [System.Xml.Serialization.XmlIgnore]
            public string XmlOnlyIgnored { get; set; }
        }

        [TestMethod]
        public void SetsXmlAttrRepresentationAndSkipsIgnoredProperties()
        {
            var generator = CreateGenerator();
            var sd = generator.GenerateLogicalModel(typeof(SerializationAttributesPoco));

            // The XmlAttribute-decorated property gets representation = xmlAttr
            var code = sd.Differential.Element.Single(e => e.Path == "SerializationAttributesPoco.code");
            Assert.AreEqual(ElementDefinition.PropertyRepresentation.XmlAttr, code.Representation.Single());

            // The XmlText-decorated property gets representation = xmlText
            var value = sd.Differential.Element.Single(e => e.Path == "SerializationAttributesPoco.value");
            Assert.AreEqual(ElementDefinition.PropertyRepresentation.XmlText, value.Representation.Single());

            // A plain property has no representation set
            var display = sd.Differential.Element.Single(e => e.Path == "SerializationAttributesPoco.display");
            Assert.IsFalse(display.Representation.Any());

            // Properties ignored during serialization are not modelled at all
            Assert.IsFalse(sd.Differential.Element.Any(e => e.Path == "SerializationAttributesPoco.jsonOnlyIgnored"));
            Assert.IsFalse(sd.Differential.Element.Any(e => e.Path == "SerializationAttributesPoco.xmlOnlyIgnored"));
        }
    }
}
