using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Hl7.Fhir.Introspection;
using Hl7.Fhir.Model;
using FirelyValidation = Hl7.Fhir.Validation;

namespace fhir_distillery
{
    /// <summary>
    /// Reflection based generator that projects plain C# classes (POCOs) into FHIR
    /// logical models (<see cref="StructureDefinition"/> resources with <c>kind = logical</c>).
    /// </summary>
    /// <remarks>
    /// The generator combines three sources of metadata (see docs/csharp-to-logical-model.md):
    /// <list type="number">
    ///   <item>assembly reflection (type/member structure, CLR types, collections, nullability),</item>
    ///   <item>the Firely SDK's existing attributes (<c>[FhirType]</c>, <c>[FhirElement]</c>,
    ///         <c>[Cardinality]</c>, <c>[AllowedTypes]</c>, <c>[References]</c>, <c>[DeclaredType]</c>), and</item>
    ///   <item>XML <c>///</c> doc comments for the human facing documentation prose.</item>
    /// </list>
    /// </remarks>
    public class LogicalModelGenerator
    {
        /// <summary>The canonical base URL to use for the generated logical models (e.g. http://fhir.example.org/).</summary>
        private readonly string _baseUrl;

        /// <summary>The publisher value stamped onto the generated logical models.</summary>
        private readonly string _publisher;

        /// <summary>The publication status stamped onto the generated logical models.</summary>
        private readonly PublicationStatus _status;

        /// <summary>Doc-comment members keyed by their documentation ID (e.g. <c>T:Namespace.MyClass</c>).</summary>
        private readonly Dictionary<string, XElement> _docMembers = new Dictionary<string, XElement>();

        /// <summary>The set of assemblies whose sibling XML doc files have already been loaded.</summary>
        private readonly HashSet<string> _loadedDocAssemblies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <param name="baseUrl">The canonical base URL to use for the generated logical models.</param>
        /// <param name="publisher">The publisher value to stamp onto the generated logical models.</param>
        /// <param name="status">The publication status to stamp onto the generated logical models.</param>
        public LogicalModelGenerator(string baseUrl, string publisher, PublicationStatus status = PublicationStatus.Draft)
        {
            _baseUrl = baseUrl;
            _publisher = publisher;
            _status = status;
        }

        /// <summary>
        /// Load the XML documentation file that sits alongside the given assembly (if present),
        /// so that <c>///</c> comments can be projected into the generated element descriptions.
        /// </summary>
        /// <param name="assembly">The assembly whose sibling <c>*.xml</c> doc file should be loaded.</param>
        public void LoadDocumentationForAssembly(Assembly assembly)
        {
            if (assembly == null || string.IsNullOrEmpty(assembly.Location))
                return;
            if (!_loadedDocAssemblies.Add(assembly.Location))
                return;
            string xmlPath = Path.ChangeExtension(assembly.Location, ".xml");
            LoadDocumentation(xmlPath);
        }

        /// <summary>
        /// Load an XML documentation file (as produced by <c>GenerateDocumentationFile</c>) so that
        /// <c>///</c> comments can be projected into the generated element descriptions.
        /// Missing files are ignored so generation degrades gracefully.
        /// </summary>
        /// <param name="xmlPath">The path to the compiler emitted <c>*.xml</c> doc file.</param>
        public void LoadDocumentation(string xmlPath)
        {
            if (string.IsNullOrEmpty(xmlPath) || !File.Exists(xmlPath))
                return;
            try
            {
                var doc = XDocument.Load(xmlPath);
                foreach (var member in doc.Descendants("member"))
                {
                    var name = member.Attribute("name")?.Value;
                    if (!string.IsNullOrEmpty(name))
                        _docMembers[name] = member;
                }
            }
            catch
            {
                // A malformed doc file should never fail the generation run
            }
        }

        /// <summary>
        /// Generate a FHIR logical model <see cref="StructureDefinition"/> for the given C# type.
        /// </summary>
        /// <param name="type">The C# type (POCO) to project into a logical model.</param>
        /// <returns>The generated logical model <see cref="StructureDefinition"/>.</returns>
        public StructureDefinition GenerateLogicalModel(Type type)
            => GenerateModel(type).StructureDefinition;

        /// <summary>
        /// Generate a FHIR logical model for the given C# type, along with any <see cref="CodeSystem"/>
        /// and <see cref="ValueSet"/> resources derived from the enum-typed properties encountered.
        /// </summary>
        /// <param name="type">The C# type (POCO) to project into a logical model.</param>
        /// <returns>The generated logical model plus any associated terminology resources.</returns>
        public LogicalModelResult GenerateModel(Type type)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));

            var result = new LogicalModelResult();
            result.StructureDefinition = BuildStructureDefinition(type, result);
            return result;
        }

        /// <summary>
        /// Build the <see cref="StructureDefinition"/> for a type into a (possibly shared) result. The model is
        /// registered on the result up-front so that referenced complex types (which each become their own model)
        /// can resolve back-references and cycles without regenerating.
        /// </summary>
        private StructureDefinition BuildStructureDefinition(Type type, LogicalModelResult result)
        {
            // Make the type's own documentation available (best-effort)
            LoadDocumentationForAssembly(type.Assembly);

            var fhirType = type.GetCustomAttribute<FhirTypeAttribute>();
            string name = fhirType?.Name ?? type.Name;
            string url = fhirType?.Canonical ?? $"{TrimBaseUrl(_baseUrl)}/StructureDefinition/{name}";

            var sd = new StructureDefinition
            {
                Id = name,
                Url = url,
                Name = name,
                Title = PascalCaseWithSpaces(name),
                Status = _status,
                Publisher = _publisher,
                FhirVersion = FHIRVersion.N4_0_1,
                Kind = StructureDefinition.StructureDefinitionKind.Logical,
                Abstract = type.IsAbstract,
                Type = url,
                BaseDefinition = ResolveBaseDefinition(type),
                Derivation = StructureDefinition.TypeDerivationRule.Specialization,
                Differential = new StructureDefinition.DifferentialComponent()
            };

            // Register the model before projecting its members so that referenced complex types
            // (each of which becomes its own model) can resolve back-references without regenerating.
            result.StructureDefinitions.Add(sd);

            // Description comes from the class' <summary>
            var (classShort, classComment) = ReadDocumentation(DocId(type));
            sd.Description = classComment ?? classShort;

            // Root element - the root of a logical model carries no cardinality (min/max)
            var rootElement = new ElementDefinition
            {
                ElementId = name,
                Path = name,
                Short = classShort,
                Definition = classShort
            };
            sd.Differential.Element.Add(rootElement);

            // Only the type's own declared properties are projected (specialization, not flattening)
            var nullabilityContext = new NullabilityInfoContext();
            var nestedStack = new HashSet<Type>();
            foreach (var property in GetProjectedProperties(type))
            {
                AddElement(sd, name, property, nullabilityContext, result, nestedStack);
            }

            return sd;
        }

        /// <summary>Gather the public instance properties of a type that should be projected as elements.</summary>
        private static IEnumerable<PropertyInfo> GetProjectedProperties(Type type)
        {
            var properties = type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                .Where(p => p.GetCustomAttribute<System.ComponentModel.DataAnnotations.Schema.NotMappedAttribute>() == null)
                // Skip properties that are ignored during serialization - they would not be present
                // in an instance serialized from the content, so they should not be modelled either.
                .Where(p => !IsSerializationIgnored(p));
            return OrderProperties(properties);
        }

        /// <summary>
        /// Build the <see cref="ElementDefinition"/> for a property and add it (and, for a nested complex type,
        /// its recursively projected child elements) to the model's differential.
        /// </summary>
        private void AddElement(StructureDefinition sd, string rootName, PropertyInfo property,
            NullabilityInfoContext nullabilityContext, LogicalModelResult result, HashSet<Type> nestedStack)
        {
            var element = BuildElement(rootName, property, nullabilityContext, result, out Type nestedComplexType);
            sd.Differential.Element.Add(element);

            // A nested complex type is projected inline as BackboneElement children (recurse into its members).
            // The stack guards against a nested type that (directly or transitively) references itself.
            if (nestedComplexType != null && nestedStack.Add(nestedComplexType))
            {
                foreach (var child in GetProjectedProperties(nestedComplexType))
                    AddElement(sd, element.Path, child, nullabilityContext, result, nestedStack);
                nestedStack.Remove(nestedComplexType);
            }
        }

        /// <summary>Order the properties by any <c>[FhirElement(Order=…)]</c>, then declaration order.</summary>
        private static IEnumerable<PropertyInfo> OrderProperties(IEnumerable<PropertyInfo> properties)
        {
            return properties
                .Select((p, index) => new
                {
                    Property = p,
                    Order = p.GetCustomAttribute<FhirElementAttribute>()?.Order ?? int.MaxValue,
                    Index = index
                })
                .OrderBy(x => x.Order)
                .ThenBy(x => x.Index)
                .Select(x => x.Property);
        }

        /// <summary>Build a single child <see cref="ElementDefinition"/> for the given property.</summary>
        /// <param name="nestedComplexType">
        /// Set to the CLR type whose members should be projected inline as <c>BackboneElement</c> children when the
        /// property is a nested complex type; otherwise <c>null</c>.
        /// </param>
        private ElementDefinition BuildElement(string rootName, PropertyInfo property, NullabilityInfoContext nullabilityContext, LogicalModelResult result, out Type nestedComplexType)
        {
            nestedComplexType = null;
            var fhirElement = property.GetCustomAttribute<FhirElementAttribute>();
            string elementName = fhirElement?.Name ?? CamelCase(property.Name);
            string path = $"{rootName}.{elementName}";

            var element = new ElementDefinition
            {
                ElementId = path,
                Path = path
            };

            // Documentation
            var (shortText, comment) = ReadDocumentation(DocId(property));
            element.Short = shortText;
            element.Definition = shortText;
            element.Comment = comment;

            // Determine whether this is a collection, and the underlying (element) type
            bool isCollection = TryGetEnumerableElementType(property.PropertyType, out Type elementClrType);
            if (!isCollection)
                elementClrType = property.PropertyType;

            // Cardinality: an explicit [Cardinality] attribute is authoritative
            var cardinality = property.GetCustomAttribute<FirelyValidation.CardinalityAttribute>();
            if (cardinality != null)
            {
                element.Min = cardinality.Min;
                element.Max = cardinality.Max == -1 ? "*" : cardinality.Max.ToString();
            }
            else if (isCollection)
            {
                element.Min = 0;
                element.Max = "*";
            }
            else
            {
                element.Min = IsNullable(property, nullabilityContext) ? 0 : 1;
                element.Max = "1";
            }

            // Types: [AllowedTypes] and [References] and [DeclaredType] override the CLR mapping
            var allowedTypes = property.GetCustomAttribute<FirelyValidation.AllowedTypesAttribute>();
            var references = property.GetCustomAttribute<ReferencesAttribute>();
            var declaredType = property.GetCustomAttribute<DeclaredTypeAttribute>();

            if (allowedTypes?.Types?.Any() == true)
            {
                foreach (var t in allowedTypes.Types)
                    element.Type.Add(new ElementDefinition.TypeRefComponent { Code = MapClrTypeToFhir(t) });
            }
            else if (declaredType?.Type != null)
            {
                element.Type.Add(new ElementDefinition.TypeRefComponent { Code = MapClrTypeToFhir(declaredType.Type) });
            }
            else
            {
                element.Type.Add(BuildComplexOrPrimitiveType(elementClrType, result, out nestedComplexType));
            }

            // Reference target profiles
            if (references?.Resources?.Any() == true)
            {
                var reference = element.Type.FirstOrDefault() ?? new ElementDefinition.TypeRefComponent();
                if (!element.Type.Any())
                    element.Type.Add(reference);
                reference.Code = "Reference";
                reference.TargetProfile = references.Resources
                    .Select(r => r.StartsWith("http") ? r : $"http://hl7.org/fhir/StructureDefinition/{r}");
            }

            // A binding comes from an explicit [Binding] attribute or an enum value set
            var binding = property.GetCustomAttribute<BindingAttribute>();
            var enumType = UnwrapEnumType(elementClrType);
            if (binding != null)
            {
                element.Binding = new ElementDefinition.ElementDefinitionBindingComponent
                {
                    Strength = BindingStrength.Required,
                    Description = binding.Name
                };
            }
            else if (enumType != null)
            {
                // Derive (or reference) a ValueSet/CodeSystem for the enum and bind to it
                string valueSetUri = GenerateEnumTerminology(enumType, result);
                element.Binding = new ElementDefinition.ElementDefinitionBindingComponent
                {
                    Strength = BindingStrength.Required,
                    ValueSet = valueSetUri
                };
            }

            if (fhirElement?.InSummary == true)
                element.IsSummary = true;
            if (fhirElement?.IsModifier == true)
                element.IsModifier = true;

            // Record any XML attribute/text serialization so a consumer knows how the element appears
            // in an XML instance. Elements with no special XML representation leave the property unset.
            var representation = DetermineXmlRepresentation(property, fhirElement);
            if (representation.HasValue)
                element.Representation = new[] { (ElementDefinition.PropertyRepresentation?)representation.Value };

            return element;
        }

        /// <summary>The full names of the attributes that mark a property as excluded from serialization.</summary>
        private static readonly string[] _ignoreAttributeNames =
        {
            "System.Text.Json.Serialization.JsonIgnoreAttribute",
            "Newtonsoft.Json.JsonIgnoreAttribute",
            "System.Xml.Serialization.XmlIgnoreAttribute",
        };

        /// <summary>
        /// Determine whether a property is excluded from serialization (JSON or XML) and therefore would not
        /// appear in an instance serialized from the content, so it should not be projected into the model.
        /// </summary>
        private static bool IsSerializationIgnored(PropertyInfo property)
        {
            return property.GetCustomAttributes(true)
                .Any(a => _ignoreAttributeNames.Contains(a.GetType().FullName));
        }

        /// <summary>
        /// Determine the FHIR <c>representation</c> (xmlAttr / xmlText) for a property from the Firely
        /// <c>[FhirElement(XmlSerialization=…)]</c> metadata or the standard <c>System.Xml.Serialization</c>
        /// attributes. Returns <c>null</c> when the property has no special XML representation.
        /// </summary>
        private static ElementDefinition.PropertyRepresentation? DetermineXmlRepresentation(PropertyInfo property, FhirElementAttribute fhirElement)
        {
            // The Firely [FhirElement] attribute records the XML serialization mode directly.
            string xmlSerialization = fhirElement?.XmlSerialization.ToString();
            if (string.Equals(xmlSerialization, "XmlAttr", StringComparison.Ordinal))
                return ElementDefinition.PropertyRepresentation.XmlAttr;
            if (string.Equals(xmlSerialization, "XmlText", StringComparison.Ordinal))
                return ElementDefinition.PropertyRepresentation.XmlText;

            // Fall back to the standard System.Xml.Serialization attributes (matched by name to avoid a hard reference).
            var xmlAttributeNames = property.GetCustomAttributes(true).Select(a => a.GetType().FullName).ToList();
            if (xmlAttributeNames.Contains("System.Xml.Serialization.XmlAttributeAttribute"))
                return ElementDefinition.PropertyRepresentation.XmlAttr;
            if (xmlAttributeNames.Contains("System.Xml.Serialization.XmlTextAttribute"))
                return ElementDefinition.PropertyRepresentation.XmlText;

            return null;
        }

        private static Type UnwrapEnumType(Type type)
        {
            if (type == null)
                return null;
            type = Nullable.GetUnderlyingType(type) ?? type;
            return type.IsEnum ? type : null;
        }

        /// <summary>
        /// Derive (or reference) the terminology for a C# <c>enum</c> and return the canonical URL of the
        /// bound <see cref="ValueSet"/>.
        /// </summary>
        /// <remarks>
        /// If the enum carries the Firely <c>[FhirEnumeration]</c> attribute (as the SDK's own enums do), the
        /// existing FHIR value set it names is referenced and no new resources are generated. Otherwise a new
        /// <see cref="CodeSystem"/> and <see cref="ValueSet"/> are generated under the configured base URL, with
        /// each concept's <c>code</c>/<c>display</c>/<c>definition</c> taken from the Firely <c>[EnumLiteral]</c> and
        /// <c>[Description]</c> annotations, falling back to the member's XML doc comment and finally its name.
        /// </remarks>
        /// <param name="enumType">The CLR enum type to derive terminology for.</param>
        /// <param name="result">The result collector that generated terminology resources are added to.</param>
        /// <returns>The canonical URL of the value set the enum-typed element should bind to.</returns>
        public string GenerateEnumTerminology(Type enumType, LogicalModelResult result)
        {
            LoadDocumentationForAssembly(enumType.Assembly);

            // An existing Firely-annotated enum already names its canonical value set - just reference it.
            var fhirEnumeration = enumType.GetCustomAttributes()
                .FirstOrDefault(a => a.GetType().FullName == "Hl7.Fhir.Utility.FhirEnumerationAttribute");
            string existingValueSet = fhirEnumeration != null ? GetAttributeString(fhirEnumeration, "Valueset") : null;
            if (!string.IsNullOrEmpty(existingValueSet))
                return existingValueSet;

            string enumName = enumType.Name;
            string codeSystemUrl = $"{TrimBaseUrl(_baseUrl)}/CodeSystem/{enumName}";
            string valueSetUrl = $"{TrimBaseUrl(_baseUrl)}/ValueSet/{enumName}";

            // Already generated during this run - reuse it (avoids duplicate resources for shared enums).
            if (result != null && result.ValueSets.Any(vs => vs.Url == valueSetUrl))
                return valueSetUrl;

            var (enumShort, enumComment) = ReadDocumentation(DocId(enumType));

            var codeSystem = new CodeSystem
            {
                Id = enumName,
                Url = codeSystemUrl,
                Name = enumName,
                Title = PascalCaseWithSpaces(enumName),
                Status = _status,
                Publisher = _publisher,
                Content = CodeSystemContentMode.Complete,
                CaseSensitive = true,
                ValueSet = valueSetUrl,
                Description = enumComment ?? enumShort
            };

            foreach (var member in enumType.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                string code = GetEnumLiteral(member) ?? member.Name;
                string display = GetDescription(member)
                    ?? ReadDocumentation(DocId(member)).shortText
                    ?? member.Name;
                string definition = ReadDocumentation(DocId(member)).comment;
                codeSystem.Concept.Add(new CodeSystem.ConceptDefinitionComponent
                {
                    Code = code,
                    Display = display,
                    Definition = definition
                });
            }
            codeSystem.Count = codeSystem.Concept.Count;

            var valueSet = new ValueSet
            {
                Id = enumName,
                Url = valueSetUrl,
                Name = enumName,
                Title = PascalCaseWithSpaces(enumName),
                Status = _status,
                Publisher = _publisher,
                Description = enumComment ?? enumShort,
                Compose = new ValueSet.ComposeComponent()
            };
            valueSet.Compose.Include.Add(new ValueSet.ConceptSetComponent { System = codeSystemUrl });

            result?.CodeSystems.Add(codeSystem);
            result?.ValueSets.Add(valueSet);
            return valueSetUrl;
        }

        /// <summary>Read the Firely <c>[EnumLiteral].Literal</c> value (the wire code) for an enum member, if present.</summary>
        private static string GetEnumLiteral(FieldInfo member)
        {
            var literal = member.GetCustomAttributes()
                .FirstOrDefault(a => a.GetType().FullName == "Hl7.Fhir.Utility.EnumLiteralAttribute");
            return literal != null ? GetAttributeString(literal, "Literal") : null;
        }

        /// <summary>Read the Firely <c>[Description].Description</c> value (the human display) for an enum member, if present.</summary>
        private static string GetDescription(FieldInfo member)
        {
            var description = member.GetCustomAttributes()
                .FirstOrDefault(a => a.GetType().FullName == "Hl7.Fhir.Utility.DescriptionAttribute");
            return description != null ? GetAttributeString(description, "Description") : null;
        }

        /// <summary>Read a string property from an attribute instance via reflection (the Firely utility attributes are not referenced at compile time).</summary>
        private static string GetAttributeString(object attribute, string propertyName)
        {
            var value = attribute.GetType().GetProperty(propertyName)?.GetValue(attribute) as string;
            return string.IsNullOrEmpty(value) ? null : value;
        }


        private string ResolveBaseDefinition(Type type)
        {
            var baseType = type.BaseType;
            if (baseType != null && baseType != typeof(object) && baseType != typeof(ValueType)
                && baseType.GetCustomAttribute<FhirTypeAttribute>() != null)
            {
                var baseFhirType = baseType.GetCustomAttribute<FhirTypeAttribute>();
                string baseName = baseFhirType.Name ?? baseType.Name;
                return baseFhirType.Canonical ?? $"{TrimBaseUrl(_baseUrl)}/StructureDefinition/{baseName}";
            }
            // Root of a chain: the FHIR abstract Base type
            return "http://hl7.org/fhir/StructureDefinition/Base";
        }

        /// <summary>Map a CLR type to its FHIR primitive/complex type code (see §5 of the design doc).</summary>
        public static string MapClrTypeToFhir(Type type)
        {
            if (type == null)
                return "string";

            // Unwrap Nullable<T>
            type = Nullable.GetUnderlyingType(type) ?? type;

            if (type.IsEnum)
                return "code";

            if (type == typeof(string)) return "string";
            if (type == typeof(bool)) return "boolean";
            if (type == typeof(int) || type == typeof(short) || type == typeof(long)) return "integer";
            if (type == typeof(uint) || type == typeof(ushort) || type == typeof(ulong)) return "positiveInt";
            if (type == typeof(byte) || type == typeof(sbyte)) return "integer";
            if (type == typeof(decimal) || type == typeof(double) || type == typeof(float)) return "decimal";
            if (type == typeof(DateTime) || type == typeof(DateTimeOffset)) return "dateTime";
            if (type == typeof(DateOnly)) return "date";
            if (type == typeof(TimeOnly) || type == typeof(TimeSpan)) return "time";
            if (type == typeof(Guid)) return "uuid";
            if (type == typeof(Uri)) return "uri";
            if (type == typeof(byte[])) return "base64Binary";

            // A complex (non-primitive) type maps structurally; use BackboneElement as the code
            return "BackboneElement";
        }

        /// <summary>
        /// Build the <see cref="ElementDefinition.TypeRefComponent"/> for a property whose type was not overridden
        /// by an explicit attribute. Primitive types map to their FHIR code directly. A complex type is either
        /// projected inline (nested class → <c>BackboneElement</c> children, signalled via
        /// <paramref name="nestedComplexType"/>) or emitted as a separate logical model referenced by its
        /// canonical URL (non-nested class).
        /// </summary>
        private ElementDefinition.TypeRefComponent BuildComplexOrPrimitiveType(Type elementClrType, LogicalModelResult result, out Type nestedComplexType)
        {
            nestedComplexType = null;
            string code = MapClrTypeToFhir(elementClrType);
            if (code != "BackboneElement")
                return new ElementDefinition.TypeRefComponent { Code = code };

            // A complex type used only within its parent is projected inline as BackboneElement children.
            if (IsNestedType(elementClrType))
            {
                nestedComplexType = elementClrType;
                return new ElementDefinition.TypeRefComponent { Code = "BackboneElement" };
            }

            // A complex type that stands on its own becomes a separate logical model, referenced by canonical URL.
            var referenced = GenerateReferencedModel(elementClrType, result);
            return new ElementDefinition.TypeRefComponent { Code = referenced.Url };
        }

        /// <summary>
        /// Determine whether a complex type is "nested" (used only within its parent, projected inline as
        /// <c>BackboneElement</c> children) rather than a standalone type that becomes its own logical model.
        /// A CLR nested class, or a type marked <c>[FhirType(IsNestedType = true)]</c>, is treated as nested.
        /// </summary>
        private static bool IsNestedType(Type type)
        {
            var fhirType = type.GetCustomAttribute<FhirTypeAttribute>();
            if (fhirType != null && fhirType.IsNestedType)
                return true;
            return type.IsNested;
        }

        /// <summary>
        /// Emit (or reuse) a separate logical model for a non-nested complex type referenced from a property,
        /// adding it to the shared <paramref name="result"/>. Returns the referenced model so the caller can
        /// link to it by canonical URL.
        /// </summary>
        private StructureDefinition GenerateReferencedModel(Type type, LogicalModelResult result)
        {
            var fhirType = type.GetCustomAttribute<FhirTypeAttribute>();
            string name = fhirType?.Name ?? type.Name;
            string url = fhirType?.Canonical ?? $"{TrimBaseUrl(_baseUrl)}/StructureDefinition/{name}";

            var existing = result.StructureDefinitions.FirstOrDefault(s => s.Url == url);
            if (existing != null)
                return existing;

            return BuildStructureDefinition(type, result);
        }

        /// <summary>
        /// Determine whether the given type is an enumerable of a single element type (excluding
        /// <see cref="string"/> and <c>byte[]</c>, which are treated as scalar values).
        /// </summary>
        private static bool TryGetEnumerableElementType(Type type, out Type elementType)
        {
            elementType = null;
            if (type == typeof(string) || type == typeof(byte[]))
                return false;

            if (type.IsArray)
            {
                elementType = type.GetElementType();
                return true;
            }

            var enumerable = new[] { type }
                .Concat(type.GetInterfaces())
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>));
            if (enumerable != null)
            {
                elementType = enumerable.GetGenericArguments()[0];
                return true;
            }
            return false;
        }

        /// <summary>Determine whether the property may be absent (nullable reference type or <c>Nullable&lt;T&gt;</c>).</summary>
        private static bool IsNullable(PropertyInfo property, NullabilityInfoContext nullabilityContext)
        {
            if (Nullable.GetUnderlyingType(property.PropertyType) != null)
                return true;
            if (!property.PropertyType.IsValueType)
            {
                var info = nullabilityContext.Create(property);
                return info.ReadState != NullabilityState.NotNull;
            }
            return false;
        }

        /// <summary>Read the <c>short</c> (from <c>&lt;summary&gt;</c>) and <c>comment</c> (from <c>&lt;remarks&gt;</c>) prose for a doc ID.</summary>
        private (string shortText, string comment) ReadDocumentation(string docId)
        {
            if (docId == null || !_docMembers.TryGetValue(docId, out var member))
                return (null, null);
            string shortText = NormalizeDocText(member.Element("summary"));
            string comment = NormalizeDocText(member.Element("remarks"));
            return (shortText, comment);
        }

        /// <summary>Whitespace-normalize XML doc prose, stripping inline tags and decoding entities.</summary>
        private static string NormalizeDocText(XElement element)
        {
            if (element == null)
                return null;
            // Value already decodes entities and drops inline markup tags such as <c>/<see>
            var text = element.Value;
            if (string.IsNullOrWhiteSpace(text))
                return null;
            var collapsed = Regex.Replace(text, @"\s+", " ").Trim();
            return string.IsNullOrEmpty(collapsed) ? null : collapsed;
        }

        /// <summary>Build the XML documentation ID for a type (e.g. <c>T:Namespace.MyClass</c>).</summary>
        private static string DocId(Type type) => $"T:{DocTypeName(type)}";

        /// <summary>Build the XML documentation ID for a property (e.g. <c>P:Namespace.MyClass.MyProperty</c>).</summary>
        private static string DocId(PropertyInfo property) => $"P:{DocTypeName(property.DeclaringType)}.{property.Name}";

        /// <summary>Build the XML documentation ID for an enum member (e.g. <c>F:Namespace.MyEnum.Member</c>).</summary>
        private static string DocId(FieldInfo member) => $"F:{DocTypeName(member.DeclaringType)}.{member.Name}";

        /// <summary>Return a type's full name using the XML documentation nesting separator (<c>.</c> rather than <c>+</c>).</summary>
        private static string DocTypeName(Type type) => (type.FullName ?? type.Name).Replace('+', '.');

        /// <summary>Remove any trailing slash from the base URL so paths can be composed consistently.</summary>
        private static string TrimBaseUrl(string baseUrl) => string.IsNullOrEmpty(baseUrl) ? baseUrl : baseUrl.TrimEnd('/');

        /// <summary>Lower-case the first character of a PascalCase identifier to produce a camelCase element name.</summary>
        private static string CamelCase(string s)
        {
            if (string.IsNullOrEmpty(s))
                return s;
            return char.ToLowerInvariant(s[0]) + s.Substring(1);
        }

        /// <summary>Insert spaces between the words of a PascalCase identifier (for a human readable title).</summary>
        private static string PascalCaseWithSpaces(string s)
        {
            if (string.IsNullOrEmpty(s))
                return s;
            return Regex.Replace(s, "(?<=[a-z0-9])(?=[A-Z])", " ");
        }
    }

    /// <summary>
    /// The output of generating a logical model for a single C# type: the
    /// <see cref="StructureDefinition"/> itself plus any terminology resources
    /// (<see cref="CodeSystem"/>/<see cref="ValueSet"/>) derived from its enum-typed properties.
    /// </summary>
    public class LogicalModelResult
    {
        /// <summary>The generated logical model. When the type references other non-nested complex types,
        /// those become additional models available via <see cref="StructureDefinitions"/>.</summary>
        public StructureDefinition StructureDefinition { get; set; }

        /// <summary>Every generated logical model: the model for the requested type plus any additional models
        /// generated for non-nested complex types referenced (directly or transitively) by its properties.</summary>
        public List<StructureDefinition> StructureDefinitions { get; } = new List<StructureDefinition>();

        /// <summary>The code systems generated from the type's enum-typed properties.</summary>
        public List<CodeSystem> CodeSystems { get; } = new List<CodeSystem>();

        /// <summary>The value sets generated from the type's enum-typed properties.</summary>
        public List<ValueSet> ValueSets { get; } = new List<ValueSet>();

        /// <summary>Enumerate every generated resource (all models followed by their terminology).</summary>
        public IEnumerable<Resource> AllResources()
        {
            foreach (var sd in StructureDefinitions)
                yield return sd;
            foreach (var cs in CodeSystems)
                yield return cs;
            foreach (var vs in ValueSets)
                yield return vs;
        }
    }
}
