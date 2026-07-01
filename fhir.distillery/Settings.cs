using System.Collections.Generic;

namespace fhir_distillery
{
    /// <summary>
    /// The serialization format for content read from or written to disk / a FHIR server.
    /// </summary>
    /// <remarks>
    /// Mirrors the <c>upload_format</c> enum used by
    /// <see href="https://github.com/brianpos/UploadFIG/blob/main/UploadFIG/Settings.cs">UploadFIG</see>.
    /// </remarks>
    public enum output_format
    {
        /// <summary>FHIR XML serialization.</summary>
        xml,
        /// <summary>FHIR JSON serialization.</summary>
        json
    }

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
        /// Headers to add to the request when connecting to the FHIR Server (e.g. an
        /// authentication header). Each entry is a single <c>Header: value</c> pair.
        /// </summary>
        /// <remarks>Uses the same convention as the UploadFIG <c>DestinationServerHeaders</c> setting.</remarks>
        public List<string> ServerHeaders { get; set; }


        /// <summary>
        /// The set of queries to execute against the FHIR Server when scanning
        /// (e.g. Questionnaire?_count=10)
        /// </summary>
        public List<string> Queries { get; set; }

        /// <summary>
        /// Provide verbose diagnostic output while processing
        /// </summary>
        public bool Verbose { get; set; }

        /// <summary>
        /// One or more compiled assemblies (.dll) to reflect over when generating logical models
        /// (used by the <c>gen-logical</c> command)
        /// </summary>
        public List<string> AssemblyPaths { get; set; }

        /// <summary>
        /// The set of type name patterns (wildcards supported) to output as logical models.
        /// When empty, all types carrying the Firely <c>[FhirType]</c> attribute are generated.
        /// </summary>
        public List<string> TypeNames { get; set; }

        /// <summary>
        /// The serialization format (xml or json) used when writing the generated resources,
        /// and when exchanging content with a FHIR Server. Defaults to <see cref="output_format.xml"/>.
        /// </summary>
        /// <remarks>Mirrors the UploadFIG <c>DestinationFormat</c> setting.</remarks>
        public output_format? OutputFormat { get; set; }

        /// <summary>
        /// The path to an optional JSON settings file carrying the generator options together with
        /// the <c>defaults</c>/<c>overrides</c> sections applied to the generated StructureDefinitions.
        /// </summary>
        /// <remarks>
        /// Command-line only: it names the settings file, so a <c>settingsFile</c> key inside the file
        /// itself is ignored (the tool never chains to a second settings file).
        /// </remarks>
        public string SettingsFile { get; set; }
    }
}
