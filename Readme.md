# FHIR-Distillery
A simple tool for extracting FHIR StructureDefinitions based on examples provided.

Creating a FHIR Implementation Guide can seem like hard work, and often development and release timelines 
get in the way of good practice, so this tool comes in handy to help pad out the extensions that you forgot
to define during the modeling phase, and also helps demonstrate which ones you've never used too...

Specifically it will:

* Deduce extensions _(creates the SD, updates the usage context and datatypes in existing SDs)_
* Highlight property usage _(sets max=0 when properties are not seen in any examples, except mandatory fields of course)_

The current project is a library that does the work, and a unit test that processes some local folders, or searching a live server.
In the future will probably adapt it to work on NDJSON files and live servers.
