# FHIR-Distillery
A simple tool for extracting HL7� FHIR� StructureDefinitions based on examples provided.

Creating a FHIR Implementation Guide can seem like hard work, and often development and release timelines 
get in the way of good practice, so this tool comes in handy to help pad out the extensions that you forgot
to define during the modeling phase, and also helps demonstrate which ones you've never used too...

Specifically it will:

* Deduce extensions _(creates the SD, updates the usage context and datatypes in existing SDs)_
* Highlight property usage _(sets max=0 when properties are not seen in any examples, except mandatory fields of course)_

The current project is a **dotnet tool** (similar to [UploadFIG](https://github.com/brianpos/uploadfig))
that scans a folder of example resources, or a live FHIR server, and produces the StructureDefinitions.
All of its settings are provided as command line parameters (rather than an `appsettings.json` file).
In the future will probably adapt it to work on NDJSON files and live servers.

## Installation
```
dotnet tool install --global fhir-distillery
```

## Usage
```
fhir-distillery --baseUrl http://fhir.example.org/ --publisher "My Organization" --scanFolder ./examples --outputPath ./OutputResources
```

| Parameter | Alias | Description |
| --- | --- | --- |
| `--sourcePath` | `-s` | The path containing any existing StructureDefinitions to use while scanning |
| `--outputPath` | `-o` | The folder where the generated/updated StructureDefinitions are written (default `OutputResources`) |
| `--baseUrl` | `-b` | The canonical base URL to use for the generated StructureDefinitions |
| `--publisher` | `-p` | The publisher value to stamp onto the generated StructureDefinitions |
| `--scanFolder` | `-sf` | A local folder of example resources (xml/json) to scan |
| `--serverUrl` | `-su` | The base URL of a FHIR Server to scan |
| `--queries` | `-q` | The queries to execute against the FHIR Server when scanning (repeatable) |
| `--verbose` | | Provide verbose diagnostic output while processing |

One of `--scanFolder` or `--serverUrl` must be provided to indicate what to scan.

## Licensing
HL7�, FHIR� and the FHIR Mark� are trademarks owned by Health Level Seven International, 
registered with the United States Patent and Trademark Office.
