using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hl7.Fhir.Model;
using Hl7.Fhir.Utility;

namespace fhir_distillery
{
    /// <summary>
    /// The optional JSON settings file for the <c>gen-logical</c> command. Its top-level keys mirror the
    /// command-line options (so the file and the flags are interchangeable) and it can additionally carry
    /// <see cref="Defaults"/> and per-type <see cref="Overrides"/> sections that a flat command line cannot
    /// express (see docs/csharp-to-logical-model.md §7.2).
    /// </summary>
    public class GeneratorSettingsFile
    {
        /// <summary>Values applied to every generated StructureDefinition unless a code attribute or a more specific override supplies one.</summary>
        [JsonPropertyName("defaults")]
        public StructureDefinitionDefaults Defaults { get; set; }

        /// <summary>Per-type augmentations (keyed by the resolved type/model name), sitting at the top of the precedence chain.</summary>
        [JsonPropertyName("overrides")]
        public Dictionary<string, StructureDefinitionOverride> Overrides { get; set; }

        private static readonly JsonSerializerOptions _options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        /// <summary>
        /// Load the <c>defaults</c>/<c>overrides</c> sections from the given JSON settings file.
        /// A missing file yields an empty (no-op) instance so generation degrades gracefully.
        /// </summary>
        public static GeneratorSettingsFile Load(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                return new GeneratorSettingsFile();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<GeneratorSettingsFile>(json, _options) ?? new GeneratorSettingsFile();
        }

        /// <summary>
        /// Apply the <c>defaults</c> (only where the generated resource has not already set a value) and any
        /// matching per-type <c>overrides</c> (which always win) to the given StructureDefinition.
        /// </summary>
        /// <param name="sd">The generated StructureDefinition to augment.</param>
        /// <param name="verbose">When true, unknown override keys are surfaced as warnings.</param>
        public void Apply(StructureDefinition sd, bool verbose = false)
        {
            if (sd == null)
                return;

            Defaults?.ApplyTo(sd);

            if (Overrides == null)
                return;
            // Match on the model name (id/name) so it is stable regardless of the CLR namespace.
            var over = Overrides.FirstOrDefault(kvp =>
                string.Equals(kvp.Key, sd.Name, StringComparison.Ordinal) ||
                string.Equals(kvp.Key, sd.Id, StringComparison.Ordinal)).Value;
            over?.ApplyTo(sd, verbose);
        }
    }

    /// <summary>The <c>defaults</c> section — StructureDefinition-level metadata applied to every generated model.</summary>
    public class StructureDefinitionDefaults
    {
        public string Status { get; set; }
        public string Version { get; set; }
        public string Publisher { get; set; }
        public bool? Experimental { get; set; }
        public string Copyright { get; set; }
        public List<string> Jurisdiction { get; set; }

        /// <summary>Apply the defaults, only filling in fields the generator has left empty.</summary>
        public void ApplyTo(StructureDefinition sd)
        {
            if (!string.IsNullOrEmpty(Status) && SettingsFieldMapper.TryParseStatus(Status, out var status))
                sd.Status = status;
            if (!string.IsNullOrEmpty(Version) && string.IsNullOrEmpty(sd.Version))
                sd.Version = Version;
            if (!string.IsNullOrEmpty(Publisher) && string.IsNullOrEmpty(sd.Publisher))
                sd.Publisher = Publisher;
            if (Experimental.HasValue && !sd.Experimental.HasValue)
                sd.Experimental = Experimental;
            if (!string.IsNullOrEmpty(Copyright) && string.IsNullOrEmpty(sd.Copyright))
                sd.Copyright = Copyright;
            if (Jurisdiction?.Any() == true && !sd.Jurisdiction.Any())
                sd.Jurisdiction = Jurisdiction.Select(j => new CodeableConcept { Coding = { SettingsFieldMapper.ParseCoding(j) } }).ToList();
        }
    }

    /// <summary>A per-type entry in the <c>overrides</c> section — model-level metadata plus a per-element map.</summary>
    public class StructureDefinitionOverride
    {
        public string Url { get; set; }
        public string Title { get; set; }
        public string Short { get; set; }
        public string Definition { get; set; }
        public string Status { get; set; }
        public string Version { get; set; }
        public string Publisher { get; set; }
        public string Copyright { get; set; }

        /// <summary>Per-element augmentations, keyed by the element name (relative to the model root) or the full element path.</summary>
        public Dictionary<string, ElementOverride> Elements { get; set; }

        /// <summary>Apply the (always-winning) overrides to the StructureDefinition and its elements.</summary>
        public void ApplyTo(StructureDefinition sd, bool verbose = false)
        {
            if (!string.IsNullOrEmpty(Url))
            {
                sd.Url = Url;
                sd.Type = Url;
            }
            if (!string.IsNullOrEmpty(Title))
                sd.Title = Title;
            if (!string.IsNullOrEmpty(Status) && SettingsFieldMapper.TryParseStatus(Status, out var status))
                sd.Status = status;
            if (!string.IsNullOrEmpty(Version))
                sd.Version = Version;
            if (!string.IsNullOrEmpty(Publisher))
                sd.Publisher = Publisher;
            if (!string.IsNullOrEmpty(Copyright))
                sd.Copyright = Copyright;

            var root = sd.Differential?.Element?.FirstOrDefault();
            if (root != null)
            {
                if (!string.IsNullOrEmpty(Short))
                    root.Short = Short;
                if (!string.IsNullOrEmpty(Definition))
                    root.Definition = Definition;
            }

            if (Elements == null || sd.Differential?.Element == null)
                return;
            string rootName = sd.Name ?? sd.Id;
            foreach (var entry in Elements)
            {
                // Accept either the leaf element name ("memberId") or the full path ("Coverage.memberId").
                string path = entry.Key.Contains('.') ? entry.Key : $"{rootName}.{entry.Key}";
                var element = sd.Differential.Element.FirstOrDefault(e => e.Path == path);
                if (element == null)
                {
                    if (verbose)
                        Console.WriteLine($"  ==> Override for unknown element '{entry.Key}' on {rootName} was ignored");
                    continue;
                }
                entry.Value?.ApplyTo(element);
            }
        }
    }

    /// <summary>A per-element augmentation inside a type override.</summary>
    public class ElementOverride
    {
        public string Short { get; set; }
        public string Definition { get; set; }
        public string Comment { get; set; }
        public int? Min { get; set; }
        public string Max { get; set; }
        public bool? MustSupport { get; set; }
        public BindingOverride Binding { get; set; }

        /// <summary>Apply the element-level override values (all always win over the generated content).</summary>
        public void ApplyTo(ElementDefinition element)
        {
            if (!string.IsNullOrEmpty(Short))
                element.Short = Short;
            if (!string.IsNullOrEmpty(Definition))
                element.Definition = Definition;
            if (!string.IsNullOrEmpty(Comment))
                element.Comment = Comment;
            if (Min.HasValue)
                element.Min = Min;
            if (!string.IsNullOrEmpty(Max))
                element.Max = Max;
            if (MustSupport.HasValue)
                element.MustSupport = MustSupport;
            if (Binding != null)
            {
                element.Binding ??= new ElementDefinition.ElementDefinitionBindingComponent();
                if (!string.IsNullOrEmpty(Binding.Strength) && SettingsFieldMapper.TryParseStrength(Binding.Strength, out var strength))
                    element.Binding.Strength = strength;
                if (!string.IsNullOrEmpty(Binding.ValueSet))
                    element.Binding.ValueSet = Binding.ValueSet;
                if (!string.IsNullOrEmpty(Binding.Description))
                    element.Binding.Description = Binding.Description;
            }
        }
    }

    /// <summary>A binding override (strength + value set) inside an element override.</summary>
    public class BindingOverride
    {
        public string Strength { get; set; }
        public string ValueSet { get; set; }
        public string Description { get; set; }
    }

    /// <summary>Small helpers to coerce settings-file string values into the Firely model types.</summary>
    internal static class SettingsFieldMapper
    {
        public static bool TryParseStatus(string value, out PublicationStatus status)
        {
            var parsed = EnumUtility.ParseLiteral<PublicationStatus>(value)
                         ?? EnumUtility.ParseLiteral<PublicationStatus>(value?.ToLowerInvariant());
            if (parsed.HasValue)
            {
                status = parsed.Value;
                return true;
            }
            status = default;
            return false;
        }

        public static bool TryParseStrength(string value, out BindingStrength strength)
        {
            var parsed = EnumUtility.ParseLiteral<BindingStrength>(value)
                         ?? EnumUtility.ParseLiteral<BindingStrength>(value?.ToLowerInvariant());
            if (parsed.HasValue)
            {
                strength = parsed.Value;
                return true;
            }
            strength = default;
            return false;
        }

        /// <summary>Parse a <c>system#code</c> (or bare <c>system</c>) jurisdiction token into a Coding.</summary>
        public static Coding ParseCoding(string value)
        {
            if (string.IsNullOrEmpty(value))
                return new Coding();
            int hash = value.IndexOf('#');
            return hash < 0
                ? new Coding { System = value }
                : new Coding { System = value.Substring(0, hash), Code = value.Substring(hash + 1) };
        }
    }
}
