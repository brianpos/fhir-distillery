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

        private void DebugDump(LogicalModelResult result)
        {
            foreach (var sd in result.StructureDefinitions)
            {
                TestScanResources.DebugDumpOutputXml(sd);
            }
            foreach (var r in result.ValueSets)
            {
                TestScanResources.DebugDumpOutputXml(r);
            }
            foreach (var r in result.CodeSystems)
            {
                TestScanResources.DebugDumpOutputXml(r);
            }
        }

        private void DebugDump(Resource resource)
        {
            TestScanResources.DebugDumpOutputXml(resource);
        }

        [TestMethod]
        public void GenerateLogicalModelFromSettingsClass()
        {
            var generator = CreateGenerator();
            var sd = generator.GenerateLogicalModel(typeof(Settings));
            DebugDump(sd);

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
            DebugDump(sd);

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
            DebugDump(sd);

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
            DebugDump(sd);

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
            DebugDump(sd);

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
            DebugDump(result);

            // One CodeSystem and one ValueSet for the SampleColour enum (deduplicated across the two properties)
            Assert.AreEqual(1, result.CodeSystems.Count);
            Assert.AreEqual(1, result.ValueSets.Count);
            DebugDump(result);

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
            DebugDump(result);
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
            
            // There will be nothing reported from this as when using that attribute, that's expected
            DebugDump(result);

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
            DebugDump(result);

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
            DebugDump(sd);

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

        [TestMethod]
        public void RootElementHasNoCardinality()
        {
            var generator = CreateGenerator();
            var sd = generator.GenerateLogicalModel(typeof(Settings));
            DebugDump(sd);

            // The root element of a logical model carries no cardinality (min/max)
            var root = sd.Differential.Element.First();
            Assert.AreEqual("Settings", root.Path);
            Assert.IsNull(root.Min);
            Assert.IsNull(root.Max);
        }

        [TestMethod]
        public void NestedComplexTypeProjectsBackboneElementChildren()
        {
            var generator = CreateGenerator();
            var result = generator.GenerateModel(typeof(OrderWithNestedAddress));
            DebugDump(result);

            // A nested class is projected inline, so only a single model is produced
            Assert.AreEqual(1, result.StructureDefinitions.Count);
            var sd = result.StructureDefinition;

            // The nested-typed property is a BackboneElement...
            var shipTo = sd.Differential.Element.Single(e => e.Path == "OrderWithNestedAddress.shipTo");
            Assert.AreEqual("BackboneElement", shipTo.Type.Single().Code);

            // ...and its members are projected inline as child elements of that backbone
            Assert.IsTrue(sd.Differential.Element.Any(e => e.Path == "OrderWithNestedAddress.shipTo.street"),
                "Expected the nested type's members to be projected as backbone-element children");
            var city = sd.Differential.Element.Single(e => e.Path == "OrderWithNestedAddress.shipTo.city");
            Assert.AreEqual("string", city.Type.Single().Code);
        }

        [TestMethod]
        public void NonNestedComplexTypeBecomesSeparateReferencedModel()
        {
            var generator = CreateGenerator();
            var result = generator.GenerateModel(typeof(InvoiceWithReferencedCustomer));
            DebugDump(result);

            // A non-nested (standalone) complex type becomes its own model referenced by canonical URL,
            // so generating one type produces two StructureDefinitions.
            Assert.AreEqual(2, result.StructureDefinitions.Count);

            var sd = result.StructureDefinition;
            Assert.AreEqual("InvoiceWithReferencedCustomer", sd.Name);

            string customerUrl = "http://fhir.example.org/StructureDefinition/ReferencedCustomer";

            // The referencing element links to the separate model by its canonical URL rather than inlining it
            var customer = sd.Differential.Element.Single(e => e.Path == "InvoiceWithReferencedCustomer.customer");
            Assert.AreEqual(customerUrl, customer.Type.Single().Code);

            // The referenced model is generated as a peer, with its own elements (not inlined into the parent)
            var referenced = result.StructureDefinitions.Single(s => s.Url == customerUrl);
            Assert.AreEqual("ReferencedCustomer", referenced.Name);
            Assert.IsTrue(referenced.Differential.Element.Any(e => e.Path == "ReferencedCustomer.name"));
            Assert.IsFalse(sd.Differential.Element.Any(e => e.Path.StartsWith("InvoiceWithReferencedCustomer.customer.")),
                "The referenced type should not be inlined into the parent model");
        }
    }

    /// <summary>A standalone (non-nested) complex type; when referenced it becomes its own logical model.</summary>
    public class ReferencedCustomer
    {
        /// <summary>The customer identifier</summary>
        public string Id { get; set; }

        /// <summary>The customer name</summary>
        public string Name { get; set; }
    }

    /// <summary>A POCO referencing a standalone complex type, which should yield a separate referenced model.</summary>
    public class InvoiceWithReferencedCustomer
    {
        /// <summary>The invoice number</summary>
        public string Number { get; set; }

        /// <summary>The billed customer (a standalone complex type)</summary>
        public ReferencedCustomer Customer { get; set; }
    }

    /// <summary>A POCO with a CLR-nested complex type, which should be projected inline as BackboneElements.</summary>
    public class OrderWithNestedAddress
    {
        /// <summary>The order reference</summary>
        public string Reference { get; set; }

        /// <summary>The delivery address (a nested complex type used only within this order)</summary>
        public NestedAddress ShipTo { get; set; }

        /// <summary>A nested complex type used only within its parent.</summary>
        public class NestedAddress
        {
            /// <summary>The street</summary>
            public string Street { get; set; }

            /// <summary>The city</summary>
            public string City { get; set; }
        }
    }
}
