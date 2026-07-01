using System.Collections.Generic;

namespace fhir_distillery
{
    /// <summary>
    /// The set of configuration options that control how the FHIR Distillery
    /// scans example resources and produces StructureDefinitions.
    /// </summary>
    /// <remarks>
    /// These values are provided via command line parameters (see <see cref="Program.GetRootCommand"/>)
    /// rather than an appsettings.json file.
    /// </remarks>
    public class Settings
    {
        /// <summary>
        /// The path containing any existing StructureDefinitions to use while scanning
        /// (in addition to the generated output folder and the core specification)
        /// </summary>
        public string SourcePath { get; set; }

        /// <summary>
        /// The folder where the generated/updated StructureDefinitions are written
        /// </summary>
        public string OutputPath { get; set; } = "OutputResources";

        /// <summary>
        /// The canonical base URL to use for the generated StructureDefinitions
        /// (e.g. http://fhir.example.org/)
        /// </summary>
        public string BaseUrl { get; set; }

        /// <summary>
        /// The publisher value to stamp onto the generated StructureDefinitions
        /// </summary>
        public string Publisher { get; set; }

        /// <summary>
        /// A local folder of example resources (xml/json) to scan for extensions and property usage
        /// </summary>
        public string ScanFolder { get; set; }

        /// <summary>
        /// The base URL of a FHIR Server to scan for extensions and property usage
        /// </summary>
        public string ServerUrl { get; set; }

        /// <summary>
        /// The set of queries to execute against the FHIR Server when scanning
        /// (e.g. Questionnaire?_count=10)
        /// </summary>
        public List<string> Queries { get; set; }

        /// <summary>
        /// Provide verbose diagnostic output while processing
        /// </summary>
        public bool Verbose { get; set; }
    }
}
