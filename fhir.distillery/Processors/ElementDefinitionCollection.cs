using Hl7.Fhir.Model;
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
        public ElementDefinitionCollection(List<ElementDefinition> elements)
        {
            // This is the "root" of the StructureDefinition, so we need to grab the first element, then populate the children
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
                Children.Add(new ElementDefinitionCollection(childsElements));
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
        public void ExpandChildren()
        {

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
