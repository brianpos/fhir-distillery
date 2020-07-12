using Hl7.Fhir.Model;
using Hl7.Fhir.Specification.Source;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace fhir_distillery.Processors
{
    [System.Diagnostics.DebuggerDisplay(@"\{{DebuggerDisplay,nq}}")] // http://blogs.msdn.com/b/jaredpar/archive/2011/03/18/debuggerdisplay-attribute-best-practices.aspx
    public class ElementDefinitionCollection
    {
        IResourceResolver _resolver;
        public ElementDefinitionCollection(IResourceResolver resolver, List<ElementDefinition> elements)
        {
            // This is the "root" of the StructureDefinition, so we need to grab the first element, then populate the children
            _resolver = resolver;
            MyElementDefinition = elements.First();

            // Scan through the immediate children
            var immediateChildren = FilterCollectionForImmediateChildren(elements, MyElementDefinition.Path).ToArray();
            for (int n = 0; n < immediateChildren.Length; n++)
            {
                int startIndex = elements.IndexOf(immediateChildren[n]);
                List<ElementDefinition> childsElements;
                if (n != immediateChildren.Length - 1)
                {
                    int endIndex = elements.IndexOf(immediateChildren[n + 1]) - 1;
                    childsElements = elements.Skip(startIndex).Take(endIndex - startIndex + 1).ToList();
                }
                else
                {
                    childsElements = elements.Skip(startIndex).ToList();
                }
                Children.Add(new ElementDefinitionCollection(_resolver, childsElements));
            }
        }

        List<ElementDefinition> FilterCollectionForImmediateChildren(List<ElementDefinition> elements, string basePath)
        {
            string checkPath = $"{basePath}.";
            return elements.Where(e => e.Path.StartsWith(checkPath) && !e.Path.Substring(checkPath.Length + 1).Contains(".")).ToList();
        }

        public IEnumerable<ElementDefinition> Elements
        {
            get
            {
                List<ElementDefinition> results = new List<ElementDefinition>();
                results.Add(MyElementDefinition);
                foreach (var child in Children)
                    results.AddRange(child.Elements);
                return results;
            }
        }

        private List<ElementDefinitionCollection> Children = new List<ElementDefinitionCollection>();
        private ElementDefinition MyElementDefinition;

        /// <summary>
        /// Walk through the base definition or datatypes and include this path
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        public bool IncludeElementFromBaseOrDatatype(string path)
        {
            if (path.StartsWith(MyElementDefinition.Path + "."))
            {
                string relativePath = path.Substring(MyElementDefinition.Path.Length + 1);
                string immediateChildPath = $"{MyElementDefinition.Path}.{(relativePath.Contains(".") ? relativePath.Substring(0, relativePath.IndexOf(".")):relativePath)}";
                // this comes from my part of the tree
                foreach (var child in Children)
                {
                    if (child.MyElementDefinition.Path == immediateChildPath)
                    {
                        return child.IncludeElementFromBaseOrDatatype(path);
                    }
                }
                // Path was not found
                if (!MyElementDefinition.Path.Contains("."))
                {
                    // Reach into the base Definition to find it
                    // var sd = this._resolver.ResolveByCanonicalUri(MyElementDefinition.Type);
                    return false;
                }
                foreach (var t in MyElementDefinition.Type)
                {
                    if (t.Code == "BackboneElement")
                        Debugger.Break();
                    var sd = this._resolver.ResolveByCanonicalUri($"http://hl7.org/fhir/StructureDefinition/{t.Code}") as StructureDefinition;
                    if (sd != null)
                    {
                        var dataTypeElements = (sd.DeepCopy() as StructureDefinition).Differential.Element.Skip(1);
                        System.Diagnostics.Trace.WriteLine($"    Expanding elements for {t.Code}: {dataTypeElements.Count()} child properties {string.Join(",", dataTypeElements.Select(d => d.Path))}");
                        foreach (var dte in dataTypeElements)
                        {
                            ScanResources.MinimizeElementCardinality(sd, dte);
                            dte.ElementId = MyElementDefinition.ElementId + "." + dte.Path.Substring(dte.ElementId.IndexOf(".") + 1);
                            dte.Path = MyElementDefinition.Path + "." + dte.Path.Substring(dte.Path.IndexOf(".")+1);
                            List<ElementDefinition> ed = new List<ElementDefinition>();
                            ed.Add(dte);
                            Children.Add(new ElementDefinitionCollection(_resolver, ed));
                        }
                        return true;
                    }
                    return false;
                }
                return false;
            }
            return false;
        }

        [DebuggerBrowsable(DebuggerBrowsableState.Never)]
        private string DebuggerDisplay
        {
            get
            {
                string result = $"Path: \"{this.MyElementDefinition.Path}\"";

                if (this.Children.Count > 0)
                    result += $" Children.Count: {this.Children.Count}";

                return result;
            }
        }
    }
}
