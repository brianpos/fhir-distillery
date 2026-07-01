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

            // Description comes from the class' <summary>
            var (classShort, classComment) = ReadDocumentation(DocId(type));
            sd.Description = classComment ?? classShort;

            // Root element
            var rootElement = new ElementDefinition
            {
                ElementId = name,
                Path = name,
                Short = classShort,
                Definition = classShort,
                Min = 0,
                Max = "*"
            };
            sd.Differential.Element.Add(rootElement);

            // Only the type's own declared properties are projected (specialization, not flattening)
            var properties = type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                .Where(p => p.GetCustomAttribute<System.ComponentModel.DataAnnotations.Schema.NotMappedAttribute>() == null);

            var nullabilityContext = new NullabilityInfoContext();
            foreach (var property in OrderProperties(properties))
            {
                sd.Differential.Element.Add(BuildElement(name, property, nullabilityContext, result));
            }

            result.StructureDefinition = sd;
            return result;
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
        private ElementDefinition BuildElement(string rootName, PropertyInfo property, NullabilityInfoContext nullabilityContext, LogicalModelResult result)
        {
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
                element.Type.Add(new ElementDefinition.TypeRefComponent { Code = MapClrTypeToFhir(elementClrType) });
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

            return element;
        }

        /// <summary>Return the underlying enum type of <paramref name="type"/> (unwrapping <c>Nullable&lt;T&gt;</c>), or null if it is not an enum.</summary>
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
        /// <summary>The generated logical model.</summary>
        public StructureDefinition StructureDefinition { get; set; }

        /// <summary>The code systems generated from the type's enum-typed properties.</summary>
        public List<CodeSystem> CodeSystems { get; } = new List<CodeSystem>();

        /// <summary>The value sets generated from the type's enum-typed properties.</summary>
        public List<ValueSet> ValueSets { get; } = new List<ValueSet>();

        /// <summary>Enumerate every generated resource (the model followed by its terminology).</summary>
        public IEnumerable<Resource> AllResources()
        {
            if (StructureDefinition != null)
                yield return StructureDefinition;
            foreach (var cs in CodeSystems)
                yield return cs;
            foreach (var vs in ValueSets)
                yield return vs;
        }
    }
}
