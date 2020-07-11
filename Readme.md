# FHIR-Distillery
A simple tool for extracting FHIR StructureDefinitions based on examples provided.

Creating a FHIR Implementation Guide can seem like hard work, and often development and release timelines 
get in the way of good practice, so this tool comes in handy to help pad out the extension that you forgot
to define during the modeling phase, and also helps demonstrate which ones you've never used too...

Specifically it can:

* Extensions (creates the SD, updates the usage context and datatypes in existing SDs)
* Property Usage (coming) max=0 when properties are not seen in any examples, max=1 if only ever saw 1

The current project is a library that does the work, and a unit test that processes some local folders.
In the future will probably adapt it to work on NDJSON files and live servers.
