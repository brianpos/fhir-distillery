# Generating FHIR Logical Models (StructureDefinitions) from C# Classes

> Status: implementation guide (design agreed)
> Audience: maintainers of `fml-processor`

## 1. Overview

This is a **code‑first** tool that turns C# classes into FHIR **logical models**
(`StructureDefinition` resources with `kind = logical`), so that the classes become the
single source of truth for both documentation and FML mappings.

The workflow:

1. A developer writes plain C# classes (POCOs) that describe some data shape
   (e.g. an internal DTO, a legacy record layout, an API contract), annotated where
   useful with the Firely SDK's existing attributes and standard `///` XML doc comments.
2. The tool inspects those classes and emits a FHIR logical model — a
   `StructureDefinition` with one `ElementDefinition` per property, with cardinalities,
   types, bindings, and descriptions pulled from the code.
3. The generated `StructureDefinition`s are then:
   - published as human‑readable documentation (via the IG publisher / Firely tooling), and
   - used as the `source`/`target` structures in FML `StructureMap`s, which this
     repository already parses, validates and serialises.

The deliverable is *not* a new FHIR resource type — it is a **logical model** (the FHIR
mechanism for describing arbitrary, non‑resource data structures). The tool's job is to
project C# type metadata into the `StructureDefinition` element model.

### The approach (agreed)

The tool is a **reflection‑based generator**: it loads the compiled assembly and walks
the types with `System.Reflection`. It combines three sources of metadata:

1. **Assembly reflection** — the type/member structure, CLR types, collections, nullability.
2. **Firely SDK attributes** — `[FhirType]`, `[FhirElement]`, `[Cardinality]`,
   `[AllowedTypes]`, `[Binding]`, `[References]`, `[NotMapped]`, etc. for the machine‑facing
   schema (see §3). These are read via the SDK's own `ModelInspector`/`ClassMapping`.
3. **XML doc comments** — `<summary>`/`<remarks>` for the human‑facing documentation
   prose (see §4).

Reasons this shape was chosen (over a Roslyn source generator, an instance‑inference
tool, or a bespoke attribute set) are recorded in the **Appendix: Alternates considered**.

### Why this fits the repository

`fml-processor` already depends on the **Firely .NET SDK** (`Hl7.Fhir.R5` +
`Hl7.Fhir.Conformance`, see `fml-processor/fml-processor.csproj`), which supplies the
building blocks the generator needs:

- **`StructureDefinition`, `ElementDefinition`, `ElementDefinition.TypeRefComponent`**
  POCOs plus a JSON serializer, so the tool never hand‑writes JSON.
- **`ModelInspector`, `ClassMapping`, `PropertyMapping`** — the SDK's own reflection
  layer that already reads the FHIR attributes off .NET types (§3).
- The existing FML validator consumes `StructureDefinition`s through
  `Hl7.Fhir.Specification.Navigation` / `Source` (see `fml-processor/FmlAnnotations.cs`),
  so anything generated can be fed straight back into the validator and mapping tooling
  that already exists.

Generating a model is therefore essentially "build `StructureDefinition` objects and
call the serializer" — the data model, serialization, and downstream consumption already
live in the codebase.

## 2. Generation pipeline

For each selected class:

1. Create a `StructureDefinition` with `kind = logical`, `derivation = specialization`,
   `abstract = false`, `baseDefinition` = the parent model (or
   `http://hl7.org/fhir/StructureDefinition/Base` at the root of a chain), `url`/`type`
   from the driver artifact (§7), and a root `ElementDefinition`.
2. Enumerate the class's **own declared** properties (`BindingFlags.DeclaredOnly` — see
   §6 on derivation chains) and, for each, emit a child `ElementDefinition`:
   - **Path** = `RootName.propertyName` (or `[FhirElement(Name=…)]` if supplied).
   - **Type** — mapped from the CLR type / attributes (§5).
   - **Cardinality** — from `[Cardinality]` if present, else inferred from nullability
     and collection‑ness (§5).
   - **Binding** — from `[Binding]` / enum types (§5, terminology in scope for v1).
   - **Documentation** — `short`/`definition`/`comment` from XML doc comments (§4).
3. Serialize with the Firely JSON serializer to `*.StructureDefinition.json`.

## 3. Firely SDK attributes used

The generator reuses the Firely SDK's existing attributes for the *structural* schema
rather than inventing a bespoke vocabulary. The following are verified against
`Hl7.Fhir.Base` 5.12.1 (the version referenced by this repo) and map directly onto
`StructureDefinition`/`ElementDefinition`:

| Firely attribute (namespace) | Target | Key members | Maps to |
| --- | --- | --- | --- |
| `[FhirType(name, canonical)]` (`Introspection`) | Class | `Name`, `Canonical`, `IsResource`, `IsNestedType` | `StructureDefinition.name` / `.url` / `.type`; root element |
| `[FhirElement(name)]` (`Introspection`) | Property | `Name`, `Order`, `Choice`, `XmlSerialization`, `InSummary`, `IsModifier`, `IsPrimitiveValue`, `FiveWs` | element `path`, `.order`, `representation` (xmlAttr), `.isModifier`, `.isSummary` |
| `[Cardinality(Min=…, Max=…)]` (`Validation`) | Property | `Min`, `Max` (`-1` = `*`) | `ElementDefinition.min` / `.max` — authoritative cardinality |
| `[AllowedTypes(types)]` (`Validation`) | Property | `Type[] Types` | `type[]` for choice (`value[x]`) elements |
| `[Binding(name)]` + `[Bindable(true)]` (`Introspection`) | Property/Class | `Name`, `IsBindable` | `ElementDefinition.binding` |
| `[References(resources)]` (`Introspection`) | Property | `string[] Resources` | `type.targetProfile` |
| `[DeclaredType(Type=…)]` (`Introspection`) | Property | `Type` | overrides the CLR→FHIR type mapping |
| `[BackboneType(definitionPath)]` (`Introspection`) | Class | `DefinitionPath` | nested `BackboneElement` typing |
| `[NotMapped]` (`Introspection`) | Any | — | the "ignore this member" marker |
| `[FhirModelAssembly(since)]` (`Introspection`) | Assembly | `FhirRelease Since` | marks an assembly as a FHIR model provider (discovery) |
| `[Versioned]` / `[VersionedValidation]` (`Introspection`/`Validation`) | Any | `FhirRelease Since` | version‑gating elements |
| `[UriPattern]` (`Validation`) | Property | — | uri‑format validation hint |

Two practical points:

- These attributes cover class identity/canonical (`[FhirType]`), member naming/order
  (`[FhirElement]`), cardinality (`[Cardinality]`), choice types (`[AllowedTypes]`),
  bindings (`[Binding]`), references (`[References]`) and exclusion (`[NotMapped]`).
- The SDK's **`ModelInspector` / `ClassMapping` / `PropertyMapping`** already read all of
  these, giving a pre‑parsed, FHIR‑shaped view of a type — the generator consumes that
  instead of hand‑rolling `GetCustomAttribute` calls.

**None of these attributes carry human documentation prose** — there is no Firely
attribute for `short`/`definition`/`comment` text. That gap is filled by XML doc comments
(§4). The division of labour:

- **Firely attributes → the machine‑facing schema** (cardinality, type, order, binding,
  canonical, isModifier).
- **XML `///` doc comments → the human‑facing documentation** (`short`, `definition`,
  `comment`).

## 4. Documentation from XML doc comments

Descriptions come from standard C# `///` XML doc comments.

**Plumbing.** Doc comments are **not** in the compiled DLL. The target project must set
`<GenerateDocumentationFile>true</GenerateDocumentationFile>`; the compiler then emits a
sibling `MyAssembly.xml`. The generator loads the DLL for structure and reads the `.xml`
for prose, keyed by *documentation IDs*:

- Type → `T:Namespace.MyClass`
- Property → `P:Namespace.MyClass.MyProperty` (generics use backtick arity, e.g. `` `1 ``)

**Tag → field mapping:**

| XML doc tag | Target |
| --- | --- |
| `<summary>` on a property | `ElementDefinition.short` (and/or `.definition`) |
| `<remarks>` on a property | `ElementDefinition.definition` or `.comment` (longer prose) |
| `<summary>` on the class | `StructureDefinition.description` + root element `.definition` |
| `<value>` | property `short` (alternative to `<summary>`) |
| `<see cref="…"/>` | resolve the cref doc‑ID → element path / link |
| `<example>` | `ElementDefinition.example` or an extension (phase 2) |

**Precedence chain** (for a given description field):

1. An override supplied in the driver artifact (§7) — highest priority, for augmenting
   or correcting generated content from an external source.
2. XML doc `<summary>` → `short`, `<remarks>` → `comment`/`definition` — the default,
   zero‑extra‑effort path.
3. Nothing → leave the description empty (do **not** invent text or emit raw tags).

**Gotchas to handle:** the `.xml` must be shipped/located (degrade gracefully if
missing); doc text is indentation‑polluted and must be whitespace‑normalized; inline
`<c>`/`<code>`/`<para>`/`<list>` tags should be stripped or converted to Markdown
(FHIR `definition`/`comment` are `markdown`); entities must be decoded; and
`<inheritdoc/>` / `<see cref>` are **not** expanded by the compiler — see §6.

## 5. FHIR primitive type mappings

Primitive CLR types map to FHIR primitives as follows. A `[DeclaredType]` attribute
overrides this mapping when present.

| C# / CLR type | FHIR type |
| --- | --- |
| `string` | `string` |
| `bool` | `boolean` |
| `int`, `short`, `long` | `integer` |
| `uint`, `ushort`, `ulong` | `positiveInt` / `unsignedInt` (by signedness) |
| `decimal`, `double`, `float` | `decimal` |
| `DateTime`, `DateTimeOffset` | `dateTime` |
| `DateOnly` | `date` |
| `TimeOnly` / `TimeSpan` | `time` |
| `Guid` | `uuid` (or `id`) |
| `Uri` | `uri` (or `url`) |
| `byte[]` | `base64Binary` |
| `enum` | `code` + `binding` to a generated `ValueSet` (terminology is in scope for v1) |

Non‑primitive property types are handled structurally (§6):

- A **nested class** (a complex type used only within its parent) → an inline
  `BackboneElement` on the parent model.
- A **non‑nested class** referenced from a property → a separate logical model, linked
  by canonical / `contentReference`.

## 6. Complex types, nesting, and derivation chains

### Nested vs. separate models (Firely‑consistent)

Complex (non‑primitive) property types are represented the way the Firely SDK itself
represents FHIR types:

- **Nested classes → inline `BackboneElement`s.** A complex type that exists only inside
  its parent is emitted as child elements (`Parent.child.grandchild …`) with
  `type.code = BackboneElement`, recursing into its members.
- **Non‑nested classes → separate logical models.** A complex type that stands on its own
  (referenced from multiple places, or explicitly selected for output) becomes its own
  `StructureDefinition`, referenced by canonical / `contentReference`.

This mirrors how Firely distinguishes backbone elements from standalone types.

### Derivation chains — specialization, not flattening

When `Derived : Base` and each class gets its own logical model, the tool uses
**specialization** (not flattening):

- `Derived`'s `StructureDefinition` sets `baseDefinition` = `Base`'s canonical URL,
  `derivation = specialization`.
- The differential contains **only the members declared on `Derived` itself** —
  inherited members are inherited from `Base`'s model, not re‑emitted.
- In reflection terms: enumerate members with **`BindingFlags.DeclaredOnly`** so only the
  type's own properties are projected.

This mirrors how FHIR expresses derived StructureDefinitions, avoids duplicating both
elements *and* documentation across every layer, and documents each member exactly once —
where it is declared.

**Impact on `<inheritdoc/>`.** The C# compiler does **not** expand `<inheritdoc/>`; the
emitted `.xml` contains the literal tag. Specialization largely sidesteps this: because
each layer only emits its own declared members, and those members' prose lives natively
on the declaring type, there is normally no `<inheritdoc/>` to resolve. A **minimal
resolver** is still worth implementing for the residual cases:

- A `Derived` member that `override`s or `new`‑hides a base member and uses
  `<inheritdoc/>` — walk the base‑type chain for the nearest real `<summary>`.
- `<inheritdoc cref="ISomeInterface.Member"/>` — resolve the cref target (interface docs
  are not in the base *class* chain).
- An `override` member with no comment — climb to the base declaration for text.

If an `<inheritdoc>` cannot be resolved, leave the description empty rather than emitting
the literal tag.

## 7. The driver artifact — selecting classes and augmenting output

The tool is driven by **either command‑line parameters or a JSON file** (which carries
the same parameters). The JSON form is preferred for anything non‑trivial because it can
additionally hold **per‑model / per‑element overrides** that augment the generated
content from an external source.

**Cross‑cutting settings** (command‑line flags or top‑level JSON keys):

- Target assembly path(s) and the list/selection of types to output.
- Canonical URL base (canonicals come from the driver artifact / command line; a per‑type
  `[FhirType(canonical)]` may override).
- FHIR release — **R5** (see §8).
- Publisher / status / version defaults, and the output directory.

**Augmentation overrides** (JSON only): an optional per‑type / per‑element section that
supplies documentation or metadata the code doesn't (or shouldn't) carry — e.g. a richer
`short`/`definition`, a fixed cardinality, or a binding. These overrides sit at the top
of the precedence chain (§4), giving an external place to refine generated output without
touching the source classes.

Selection of *which* classes to output can come from the driver artifact's type list,
and/or discovery of types carrying `[FhirType]` (optionally scoped by the
`[FhirModelAssembly]` marker).

## 8. Target FHIR release

Generated logical models target **FHIR R5**, matching the `Hl7.Fhir.R5` SDK reference in
this repo. R5 logical‑model JSON is also largely compatible with R4 consumers, so a
single R5 output serves both in practice.

## 9. Terminology / bindings (in scope for v1)

Terminology binding is **in scope for v1**:

- A C# `enum` property → `type.code = code` (or `Coding`/`CodeableConcept` where
  appropriate) with an `ElementDefinition.binding` to a **generated `ValueSet`** whose
  concepts come from the enum members.
- Enum member `///` comments feed the `ValueSet` concept `display`/`definition`.
- An explicit `[Binding(name)]` attribute binds to a named/existing value set instead of a
  generated one.

## 10. Mapping cheat‑sheet: C# → StructureDefinition

| C# construct | StructureDefinition / ElementDefinition |
| --- | --- |
| Class selected for output (`[FhirType]`) | `StructureDefinition` `kind=logical`, `derivation=specialization`, root element |
| Class name / `[FhirType(Name=…)]` | `name`, `id`, root element `path` |
| `[FhirType(canonical)]` or driver base + name | `url` (canonical), `type` |
| Base class (`Derived : Base`) | `baseDefinition` = `Base` canonical; only own members in differential (`DeclaredOnly`) |
| Declared public property | child `ElementDefinition` at `Root.prop` |
| `[FhirElement(Order=…)]` | element `.order` / ordering |
| Property CLR type (primitive) | `type.code` per §5 |
| Nested class (used only within parent) | inline `BackboneElement` (recurse) |
| Non‑nested class referenced by property | separate logical model via canonical / `contentReference` |
| `[Cardinality(Min,Max)]` (authoritative) | `min` / `max` (`-1`→`*`) |
| `List<T>` / `IEnumerable<T>` (fallback) | `max = *` |
| Nullable (`T?` / ref type, via `NullabilityInfoContext`) | `min = 0` |
| Non‑nullable value type / `required` | `min = 1` |
| `[AllowedTypes(...)]` | choice `type[]` (`value[x]`) |
| `enum` / `[Binding]` | `binding` (+ generated `ValueSet`) — v1 |
| `[References(...)]` | `type.targetProfile` |
| XML `<summary>` | `short` / `definition` |
| XML `<remarks>` | `comment` / `definition` |
| Driver‑artifact override | wins over generated `short`/`definition`/cardinality/binding |
| `[NotMapped]` | skipped |

## 11. Suggested shape in this repository

- A new command/mode on the existing CLI entry point (`fml-processor/Program.cs`), e.g.
  `fml-processor gen-logical --assembly … --config …` (or with the individual
  command‑line parameters from §7).
- A `LogicalModelGenerator` class encapsulating the "type → StructureDefinition"
  projection, so it can be unit tested directly.
- Emit `*.StructureDefinition.json` via the Firely serializer.
- Round‑trip test in `fml-tester`: define sample POCOs, generate, then feed the result
  into the **existing** FML validator to prove the generated models are usable as FML
  `source`/`target` structures.

## Appendix: Alternates considered

The following approaches were explored and set aside; each is recorded here with the
reason the chosen approach (reflection + Firely attributes + XML docs) was preferred.

### A1. Roslyn source generator / analyzer (compile‑time)

Read the C# syntax + semantic model at build time and emit the `StructureDefinition`
JSON as a build artifact.

- **Attractions:** sees everything the compiler sees (XML doc comments, nullable
  annotations, `init`/`required`, `partial` types), runs automatically on every build so
  models can't drift, and resolves `<inheritdoc/>` / `<see cref>` in‑memory via the
  semantic model with no separate `.xml` file.
- **Why not chosen:** considerably more machinery (a separate `netstandard2.0` analyzer
  project, Roslyn packages); analyzer dependency isolation makes it painful to reference
  the Firely SDK to build `StructureDefinition` POCOs, so we'd likely hand‑build JSON via
  a template and lose the SDK's serializer guarantees; harder to debug/unit‑test; and it
  can only see types in the compilation, not a prebuilt third‑party DLL. The reflection
  approach reuses the Firely POCO + serializer stack already in the repo and works against
  any assembly. The projection logic is kept in a standalone class so a source‑generator
  front‑end could still be layered on later without rewriting the mapping.

### A2. Instance inference from sample XML/JSON documents

Infer a logical model by traversing a representative data instance (as done by
fhirpath‑lab's `helpers/logical_model_generator.ts`), rather than from the type
definitions.

- **Attractions:** needs no C# at all; can bootstrap a model from an example payload; and
  value sniffing can guess refined types (e.g. `date`/`uuid`/`oid` from string contents)
  that the type system doesn't express.
- **Why not chosen:** it is *example‑first*, not *code‑first*. Cardinality is only a guess
  (everything defaults to `0..1`, `max` becomes `*` only if the sample happens to contain
  an array); optional members absent from the sample are invisible; and — most important
  for a "documentation from code" goal — a data instance carries **no descriptions**, so
  there is nothing to populate `short`/`definition`. It remains useful as a separate
  bootstrapping aid, and its string‑sniffing heuristics could optionally enrich reflected
  `string` members in a later phase.

### A3. A bespoke attribute vocabulary (`[LogicalModel]`, `[LogicalElement]`, `[LogicalIgnore]`)

Define new project‑specific attributes to mark classes, describe elements, and exclude
members.

- **Why not chosen:** the Firely SDK already provides equivalents — `[FhirType]` (class +
  canonical), `[FhirElement]` (naming/order), `[Cardinality]`, `[AllowedTypes]`,
  `[Binding]`, `[References]`, and `[NotMapped]` (exclusion) — all readable via
  `ModelInspector`/`ClassMapping` (§3). Reusing them avoids a parallel vocabulary and lets
  the same annotated classes work with other Firely tooling. Documentation prose, which no
  attribute carries, is taken from XML doc comments instead.

### A4. `[Description]` / `[Display]` attributes for documentation prose

Carry element descriptions in attributes rather than XML doc comments.

- **Why not chosen:** this duplicates text developers would naturally write as `///`
  comments and must be maintained separately. XML doc comments are the primary
  documentation channel (§4); external overrides for prose live in the driver artifact
  (§7) instead, so there's still a non‑source place to augment descriptions.

### A5. Flattening derived types into self‑contained models

Emit every inherited member on each derived type's model (a fully expanded snapshot per
class).

- **Why not chosen:** it duplicates both elements and documentation across every layer and
  *forces* full `<inheritdoc/>` resolution (inherited members otherwise come out blank).
  Specialization (§6) matches FHIR's own derivation model, documents each member once, and
  makes `<inheritdoc/>` a rare edge case.
