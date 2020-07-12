using System;
using System.IO;
using System.Linq;
using fhir_distillery.Processors;
using Hl7.Fhir.ElementModel;
using Hl7.Fhir.Introspection;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Hl7.Fhir.Specification.Source;
using Hl7.Fhir.Utility;

namespace fhir_distillery
{
    public class ScanResources
    {
        public ScanResources(string existingSDpath, string newSDpath, string outputProfileBaseUri, string publisher)
        {
            _publisher = publisher;
            _outputProfileBaseUri = outputProfileBaseUri;
            _outputProfilePath = newSDpath;
            ds = new DirectorySource(_outputProfilePath, new DirectorySourceSettings() { IncludeSubDirectories = true });
            sourceSD = new CachedResolver(
                    new MultiResolver(
                        ds,
                        new DirectorySource(existingSDpath, new DirectorySourceSettings() { IncludeSubDirectories = true }),
                        new ZipSource(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "specification.zip"))
                    )
                );
        }
        private string _outputProfilePath;
        DirectorySource ds;
        public CachedResolver sourceSD;
        string _outputProfileBaseUri;
        string _publisher;

        public void DiscoverExtensions()
        {
            // Do ndjson too!
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
                            {
                                System.Diagnostics.Trace.WriteLine($"  -->{entry.ResourceType}/{entry.Id}");
                                ScanForExtensions(null, entry.ToTypedElement(), null);
                            }
                        }
                    }
                    else
                    {
                        ScanForExtensions(null, resource.ToTypedElement(), null);
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

        public StructureDefinition CreateProfileWithAllMinZero(string canonicalUri, string newBase)
        {
            var realSD = sourceSD.ResolveByCanonicalUri(canonicalUri) as StructureDefinition;
            var newSD = realSD.DeepCopy() as StructureDefinition;
            Uri t = new Uri(canonicalUri);
            newSD.Url = $"{newBase}StructureDefinition/{t.Segments.Last()}";
            newSD.Text = null;
            newSD.Version = null;
            newSD.Publisher = _publisher;
            newSD.Status = PublicationStatus.Draft;
            newSD.Contact = null;
            newSD.Mapping.Clear();
            newSD.Extension.Clear();
            newSD.Snapshot = null;
            newSD.BaseDefinition = canonicalUri;
            newSD.Derivation = StructureDefinition.TypeDerivationRule.Constraint;
            foreach (var e in newSD.Differential.Element)
            {
                MinimizeElementCardinality(newSD, e);
            }
            return newSD;
        }

        public static void MinimizeElementCardinality(StructureDefinition newSD, ElementDefinition e)
        {
            e.Requirements = null;
            e.IsSummary = null;
            e.Comment = null;
            e.IsModifier = null;
            e.IsModifierReason = null;
            e.Mapping.Clear();
            e.Example.Clear();
            if (e.Path != newSD.Type && (!e.Min.HasValue || e.Min == 0))
                e.Max = "0";
            e.MustSupport = null;
            e.Constraint.Clear();
            e.Binding = null; // might want to put this back and "expand" the used concepts
        }

        void ScanForTerminologyValues(Base item)
        {
            // create the valueset with all the USED values
        }

        void ScanForSliceUsage(Base item)
        {
            // Patient Identifier system usage etc?
        }


        public void ScanForExtensions(string basePath, ITypedElement item, StructureDefinition sd)
        {
            string context = string.IsNullOrEmpty(basePath) ? item.Name : basePath + "." + item.Name;
            bool customSD = false;

            // Check for extensions
            var iv = item as IFhirValueProvider;
            if (iv.FhirValue is Extension)
            {
                // this is a child that is the extension itself...
                // and not doing complex extensions just yet
                return;
            }
            if (iv.FhirValue is Resource r)
            {
                var canonicalUri = $"{_outputProfileBaseUri}StructureDefinition/{r.ResourceType.GetLiteral()}";
                sd = sourceSD.ResolveByCanonicalUri(canonicalUri) as StructureDefinition;
                if (sd == null)
                {
                    sd = CreateProfileWithAllMinZero($"http://hl7.org/fhir/StructureDefinition/{r.ResourceType.GetLiteral()}", _outputProfileBaseUri);
                    customSD = true;
                }
            }
            if (iv.FhirValue != null)
            {
                // Mark the property in the SD as in use, so not 0 anymore
                var ed = sd?.Differential?.Element.FirstOrDefault(e => e.Path == context || e.Path == context+"[x]");
                if (ed != null)
                {
                    ed.Max = null;
                    customSD = true;
                    int usage = ed.IncrementUsage();
                    System.Diagnostics.Trace.WriteLine($"      {context} updated {usage}");
                    if (usage == 1)
                    {
                        // Need to expand the datatype into the element table
                    }
                }
                else
                {
                    System.Diagnostics.Trace.WriteLine($"      {context} not found");
                    ElementDefinitionCollection edc = new ElementDefinitionCollection(this.sourceSD, sd.Differential.Element.ToList());
                    edc.IncludeElementFromBaseOrDatatype(context);
                    sd.Differential.Element = edc.Elements.ToList(); // replace the re-processed element tree

                    ed = sd?.Differential?.Element.FirstOrDefault(e => e.Path == context);
                    if (ed != null)
                    {
                        ed.Max = null;
                        customSD = true;
                        int usage = ed.IncrementUsage();
                        System.Diagnostics.Trace.WriteLine($"      {context} updated {usage}");
                        if (usage == 1)
                        {
                            // Need to expand the datatype into the element table
                        }
                    }
                }
            }
            if (iv.FhirValue is IExtendable extendable)
            {
                if (extendable.Extension != null)
                {
                    foreach (var ext in extendable.Extension)
                    {
                        var instSD = sourceSD.ResolveByCanonicalUri(ext.Url) as StructureDefinition;
                        if (instSD == null)
                        {
                            Console.WriteLine($"  x==> Extension {ext.Url} not found");
                            System.Diagnostics.Trace.WriteLine($"  x==> Extension {ext.Url} not found");
                            if (ext.Value != null)
                                DeriveExtensionFromUsage(ext.Url, ext.Value.TypeName, context);
                        }
                        else
                        {
                            System.Diagnostics.Trace.WriteLine($"  ---> Extension {ext.Url} exists");
                            // Check that this extension is defineed to support this
                            if (ext.Value != null)
                                UpdateExtensionStructureDefinitionForUsage(instSD, ext.Value.TypeName, context);
                        }
                        // Check that the extension is in the SD for this property
                        var ed = sd.Differential.Element.FirstOrDefault(e => e.Path == context);
                        if (ed != null)
                        {
                            // this is the position after which the extension needs to be added

                            // check if it is already in there
                            var edExts = sd.Differential.Element.Where(e => e.Path == context + ".extension");
                            if (!edExts.Any())
                            {
                                // this is the position after which the extension needs to be added
                                // check if it is already in there
                            }
                            Uri t = new Uri(ext.Url);
                            if (!edExts.Any(e => e.SliceName == t.Segments.Last()))
                            {
                                var edExt = new ElementDefinition()
                                {
                                    ElementId = $"{context}.extension:{t.Segments.Last()}",
                                    Path = context + ".extension",
                                    SliceName = t.Segments.Last()
                                };
                                edExt.Type.Add(new ElementDefinition.TypeRefComponent()
                                {
                                    Code = "Extension",
                                    Profile = new[] { ext.Url }
                                });
                                sd.Differential.Element.Insert(sd.Differential.Element.IndexOf(ed) + 1, edExt);
                            }
                        }
                    }
                }
            }
            var children = item.Children();
            if (children != null)
            {
                foreach (var child in children)
                {
                    var fv = child as IFhirValueProvider;
                    if (fv.FhirValue is Resource rc)
                        ScanForExtensions(null, rc.ToTypedElement(), null);
                    else
                        ScanForExtensions(context, child, sd);
                }
            }
            if (customSD)
                SaveStructureDefinition(sd);
        }

        private void UpdateExtensionStructureDefinitionForUsage(StructureDefinition sd, string typeName, string context)
        {
            bool updated = false;
            // Check the context (and add it)
            if (!sd.Context.Any(uc => uc.Type == StructureDefinition.ExtensionContextType.Extension && uc.Expression == context))
            {
                // Need to include this context too
                sd.Context.Add(new StructureDefinition.ContextComponent()
                {
                    Type = StructureDefinition.ExtensionContextType.Extension,
                    Expression = context
                });
                updated = true;
            }

            // Check the datatype
            if (!sd.Differential.Element.Any(e => e.Path == "Extension.value[x]" && e.Type.Any(t => t.Code == typeName)))
            {
                sd.Differential.Element.FirstOrDefault(e => e.Path == "Extension.value[x]").Type.Add(new ElementDefinition.TypeRefComponent()
                {
                    Code = typeName
                });
                updated = true;
            }

            // Store the StructureDefinition
            if (updated)
                SaveStructureDefinition(sd);
        }

        private void DeriveExtensionFromUsage(string url, string typeName, string context)
        {
            StructureDefinition sd = new StructureDefinition()
            {
                Url = url,
                Publisher = _publisher,
                Status = PublicationStatus.Draft,
                FhirVersion = FHIRVersion.N0_4_0,
                Kind = StructureDefinition.StructureDefinitionKind.ComplexType,
                Abstract = false,
                BaseDefinition = "http://hl7.org/fhir/StructureDefinition/Extension",
                Derivation = StructureDefinition.TypeDerivationRule.Constraint
            };
            sd.Context.Add(new StructureDefinition.ContextComponent()
            {
                Type = StructureDefinition.ExtensionContextType.Extension,
                Expression = context
            });

            // Derive a resource ID from the extension URL
            Uri t = new Uri(url);
            sd.Id = t.Segments.Last();


            // Add in the Extension element definitions
            sd.Differential = new StructureDefinition.DifferentialComponent();
            sd.Differential.Element.Add(new ElementDefinition()
            {
                ElementId = "Extension",
                Path = "Extension"
            });
            sd.Differential.Element.Add(new ElementDefinition()
            {
                ElementId = "Extension.url",
                Path = "Extension.url",
                Fixed = new FhirUrl(url) // This is really not required anymore
            });
            sd.Differential.Element.Add(new ElementDefinition()
            {
                ElementId = "Extension.value[x]",
                Path = "Extension.value[x]",
                Type = new System.Collections.Generic.List<ElementDefinition.TypeRefComponent>(new[]
                {
                    new ElementDefinition.TypeRefComponent() { Code = typeName }
                }),
                MustSupport = true
            });

            SaveStructureDefinition(sd);
        }

        public void SaveStructureDefinition(StructureDefinition sd)
        {
            // Ensure the folder exists
            Uri t = new Uri(sd.Url);
            string profileOutputDirectory = Path.Combine(_outputProfilePath, t.Host);
            if (!Directory.Exists(profileOutputDirectory))
                Directory.CreateDirectory(profileOutputDirectory);

            // Non extensions will just go in the root of the output
            if (sd.BaseDefinition != "http://hl7.org/fhir/StructureDefinition/Extension")
                profileOutputDirectory = _outputProfilePath;

            // Now output the file
            System.IO.File.WriteAllText($"{profileOutputDirectory}/StructureDefinition-{sd.Id}.xml", new FhirXmlSerializer(new SerializerSettings() { AppendNewLine = true, Pretty = true }).SerializeToString(sd));

            // And add it to our resolver
            sourceSD.InvalidateByCanonicalUri(sd.Url);
            ds.Refresh();

            // And check that it comes back...
            var instSD = sourceSD.ResolveByUri(sd.Url) as StructureDefinition;
            if (instSD == null)
            {
                Console.WriteLine($"Was not able to resolve the newly created extension");
            }
        }

        void ScanResourceForPropertyUsage(Resource resource)
        {
            var instSD = sourceSD.ResolveByUri("http://hl7.org/fhir/StructureDefinition/" + resource.TypeName) as StructureDefinition;
            if (instSD == null)
            {
                Console.WriteLine($"Unable to load the StructureDefinition for {resource.TypeName}");
                return;
            }
            var sd = instSD.DeepCopy() as StructureDefinition;
            sd.Url = this._outputProfileBaseUri + $"StructureDefinition-{resource.TypeName}";
            // Check for extensions
        }
    }

    public class PropertyUsage
    {
        public int UsageCount { get; set; } = 0;
        public int Increment() { UsageCount++; return UsageCount; }
    }

    public static class ED_ExtensionMethods
    {
        public static int IncrementUsage(this ElementDefinition ed)
        {
            if (ed == null)
                return 0;
            if (ed.HasAnnotation<PropertyUsage>())
            {
                return ed.Annotation<PropertyUsage>().Increment();
            }
            else
            {
                ed.SetAnnotation(new PropertyUsage() { UsageCount = 1 });
                return 1;
            }
        }
    }
}
