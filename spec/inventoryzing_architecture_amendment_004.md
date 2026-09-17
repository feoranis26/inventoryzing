# inventoryzing Architecture Amendment 004

**Status:** Accepted architectural amendment  
**Scope:** Classification tags, typed metadata properties, module-owned presentation, and metadata schema contracts  
**Applies to:** Original architecture handoff + Amendments 001–003  
**Intent:** Remove terminology ambiguity and define the typed metadata/presentation boundary before implementation

---

# 1. Purpose

This amendment resolves an ambiguity that existed throughout the earlier architecture discussion and specifications.

The word **tag** had been used for two substantially different concepts:

1. human-facing classification labels such as `Hardware`, `M5 Hardware`, and `Slot Nut`;
2. machine-facing key/value data attached to inventoryzing entities for use by core modules, workflows, plugins, and integrations.

These concepts MUST be separated.

This amendment also defines the typed metadata schema in more detail and establishes a strict presentation boundary:

> **Machine-facing metadata is not directly rendered to normal users. Modules interpret metadata and contribute user-facing presentation elements.**

Where this amendment conflicts with earlier wording, **this amendment takes precedence**.

---

# 2. Terminology

The following terminology is normative.

## 2.1 Classification Tag

A **Classification Tag** is a human-facing classification node used to organize, search, and categorize entities.

Examples:

```text
Hardware
M5 Hardware
Slot Nut
Measurement Equipment
Needs Repair
```

Classification tags MAY participate in a directed acyclic inheritance graph.

They are fundamentally membership/classification data rather than arbitrary key/value storage.

The word **tag** SHOULD refer only to this classification concept in future specifications and code.

---

## 2.2 Metadata Property

A **Metadata Property** is a typed, machine-facing key/value value associated with an inventoryzing entity.

Examples:

```text
manufacturing.printer
manufacturing.material_used
manufacturing.completed_at
openlab.return_state
workflow.last_operator
plugin.example.internal_state
```

Metadata properties are normally consumed by:

- core modules;
- optional modules;
- workflows;
- automation;
- plugins;
- integrations;
- search/indexing logic.

Metadata properties are **not normal user-facing fields**.

The normal UI MUST NOT generically dump metadata keys and raw values onto an entity page.

---

## 2.3 Module Presentation

**Module Presentation** is the human-facing interpretation of core state and metadata.

Examples:

```text
Printer: Prusa MK4 #2

This object was printed in: Prusa MK4 #2

Material used: 83.4 g

Design file: motor_mount_rev7.gcode [Download]

Time of manufacture: 08 Nov 2032
```

The presentation layer is owned by the module that understands the semantics of the underlying data.

The metadata storage layer MUST remain presentation-agnostic.

---

## 2.4 First-Class Core State

**First-Class Core State** is state represented using dedicated core structures because inventoryzing itself must enforce universal semantics or invariants.

Examples include:

```text
checkout
ownership
custody
physical hierarchy
quantity holdings
authority
transfer
identity
```

First-class core state MUST NOT be demoted into arbitrary metadata merely because the metadata subsystem is flexible.

---

# 3. Architectural Rule

The architecture MUST preserve the following separation:

```text
Classification Tags
    human-facing classification graph

Metadata Properties
    typed machine-facing extensible data

First-Class Core State
    universal state with core invariants

Module Presentation
    human-facing interpretation of state
```

These are related systems but MUST NOT be treated as interchangeable.

---

# 4. Classification Tags

Classification tags remain a dedicated subsystem.

A classification tag SHOULD have at least:

```text
tag_id
canonical_name
description
parent relationships
created_at
archived_at
```

A tag MAY inherit from multiple parent tags.

Cycles MUST be prevented using the structural graph rules defined by earlier amendments.

Example:

```text
M5 Slot Nut
├── Slot Nut
│   └── Hardware
└── M5 Hardware
    └── Hardware
```

Assigning `M5 Slot Nut` MAY therefore make the object effectively searchable under all inherited parent classifications.

---

# 5. Classification Assignment

Classification membership SHOULD be represented structurally rather than as arbitrary string metadata.

For example:

```text
Entity A
    --classified_as-->
Tag UUID: M5 Slot Nut
```

or through an equivalent dedicated assignment table.

The implementation MUST NOT use metadata such as:

```text
category = "M5 Slot Nut"
```

as the canonical classification representation.

This preserves:

- DAG inheritance;
- stable tag identity;
- rename safety;
- search consistency;
- deduplication;
- permissions;
- efficient closure/indexing.

---

# 6. Typed Metadata System

The metadata subsystem SHOULD use schema-backed property definitions.

A metadata value MUST be interpreted according to its registered property definition rather than relying on ad-hoc self-describing JSON values.

Recommended conceptual structures:

```text
metadata_property_definitions
metadata_property_values
```

A property definition SHOULD include:

```text
property_key
namespace
value_schema
cardinality
applies_to
owner_module
write_policy
index_policy
default_policy
description
created_at
archived_at
```

---

# 7. Property Keys and Namespaces

Metadata property keys MUST be namespaced.

Examples:

```text
core.foo
manufacturing.printer
manufacturing.material_used
openlab.return_state
workflow.openlab.last_scan
plugin.com.example.myplugin.state
```

A namespace SHOULD have an owning module or subsystem.

Recommended ownership examples:

```text
core.*                  -> inventoryzing core
manufacturing.*         -> manufacturing module
openlab.*               -> OpenLab workflow/module
plugin.com.example.*    -> third-party plugin
```

Namespace ownership MUST NOT imply unrestricted database access.

Instead, it defines which module is normally authorized to mutate or interpret that metadata.

---

# 8. Metadata Is Not Presentation

Property definitions MUST NOT be required to contain normal user-facing display strings such as:

```text
display_label = "Printer"
display_format = "This object was printed at {}"
```

Normal presentation is the responsibility of the owning module.

The same underlying metadata MAY be rendered differently in different UI contexts.

Example metadata:

```text
manufacturing.printer = <entity UUID>
```

Possible presentations include:

```text
Object page:
Printed on Prusa MK4 #2
```

```text
History table:
Printer | Prusa MK4 #2
```

```text
Mobile card:
Prusa MK4 #2
```

The storage format MUST NOT force one of these presentation styles.

---

# 9. Metadata Visibility

Normal users SHOULD NOT see raw metadata keys or raw machine values.

A normal object page MUST NOT contain generic output such as:

```text
manufacturing.printer = 0199af28...
manufacturing.completed_at = 2032-11-08T14:02:11Z
openlab.return_state = desk_pending
```

Instead, modules contribute human-facing display elements.

Raw metadata MAY be exposed through an advanced administrative/debugging interface.

Such a view SHOULD be clearly separated from the normal entity page.

---

# 10. Raw Metadata Editing

Raw machine metadata SHOULD be read-only by default, including for administrators.

Administrative users MAY be granted a dangerous maintenance capability for direct metadata mutation, but:

- it SHOULD require explicit elevated permission;
- the UI SHOULD warn that invariants may be bypassed;
- such edits MUST be audited;
- modules SHOULD prefer semantic commands/actions over direct raw edits.

A workflow or module SHOULD normally mutate metadata through the owning module's semantic API.

Example:

```text
request_return(item)
```

is preferred over an administrator manually setting:

```text
openlab.return_state = "return_pending"
```

even if both ultimately modify the same metadata.

---

# 11. Metadata Value Types

The MVP typed metadata system SHOULD support at least the following value types:

```text
string
integer
decimal
boolean
date
datetime
enum
entity_reference
quantity
url
blob_reference
json
```

Additional types MAY be added later.

Binary floating-point values SHOULD NOT be used for decimal-safe inventory or engineering quantities.

---

# 12. String Schema

A string metadata schema MAY define:

```text
minimum_length
maximum_length
regex/pattern
normalization
```

Example:

```text
property_key: manufacturing.machine_serial
type: string
maximum_length: 128
```

---

# 13. Integer and Decimal Schema

Numeric metadata schemas MAY define:

```text
minimum
maximum
exclusive_minimum
exclusive_maximum
granularity
```

Decimal values requiring exact representation MUST use decimal-safe storage.

API representations SHOULD preserve exact decimal values.

---

# 14. Boolean Schema

Boolean values MUST represent logical true/false state.

Boolean properties SHOULD NOT be replaced by arbitrary strings such as:

```text
"yes"
"no"
"pending"
```

if the semantic value is genuinely boolean.

---

# 15. Date and Datetime Schema

Date values SHOULD represent calendar dates without time-of-day semantics.

Datetime values SHOULD represent absolute instants using a timezone-aware representation.

The database SHOULD store canonical timestamps and allow UI modules to render them in user-local time.

---

# 16. Enum Schema

Enum properties MUST define stable machine values.

Example:

```text
property_key: openlab.return_state

type: enum

allowed_values:
    active
    return_pending
    desk_pending
```

Display wording MAY differ from machine values and is controlled by the owning module.

The enum value:

```text
return_pending
```

does not require the UI to display:

```text
return_pending
```

The module may display:

```text
Return requested
```

or any other contextually appropriate phrase.

---

# 17. Entity Reference Schema

Entity references MUST store stable entity identity rather than a user-facing name.

Example:

```text
manufacturing.printer = <UUID of Printer #2>
```

A property definition MAY restrict the allowed target kinds.

Example:

```text
type: entity_reference

allowed_targets:
    Asset
```

or:

```text
allowed_targets:
    Principal
```

The presentation module resolves the reference to the current human-readable name and appropriate link.

---

# 18. Quantity Schema

A quantity MUST be represented as one typed value containing:

```text
decimal amount
unit/dimension
```

It MUST NOT be represented as two unrelated metadata keys such as:

```text
amount = 15
unit = "cm"
```

A quantity property definition SHOULD define:

```text
dimension
canonical_unit
allowed_units
optional minimum
optional maximum
optional granularity
```

Example:

```text
property_key: manufacturing.material_used

type: quantity
dimension: mass
canonical_unit: g
minimum: 0
```

The UI may display:

```text
83.4 g
```

or:

```text
0.0834 kg
```

without changing canonical storage.

---

# 19. URL Schema

URL metadata MAY store validated external links.

The property definition MAY restrict schemes or host patterns if needed.

The UI MUST NOT blindly render unsafe protocols as executable links.

---

# 20. Blob Reference Schema

A blob reference MUST identify an attachment/blob object managed by inventoryzing storage.

Example:

```text
manufacturing.design_file = <blob UUID>
```

The metadata layer stores identity only.

The module presentation may render:

```text
Design file:
final_prototype_revision_final_real_final.gcode
[Download]
```

The metadata layer does not need to understand filenames, download buttons, or user-facing formatting.

---

# 21. JSON Escape Hatch

A JSON metadata type MAY exist for module-private structured state.

It SHOULD be treated as an escape hatch, not the default representation.

JSON metadata:

- MAY be excluded from generic indexing;
- MAY only be writable by its owning module;
- SHOULD have a versioned internal schema if persisted long-term;
- MUST NOT replace first-class data structures where the core requires invariants.

A plugin MUST NOT store important cross-module universal state in opaque JSON merely to avoid defining a proper schema.

---

# 22. Cardinality

Metadata property definitions SHOULD declare cardinality.

The MVP SHOULD support at least:

```text
single
set
```

## 22.1 Single

At most one active value exists for the entity/property pair.

Example:

```text
manufacturing.printer
```

## 22.2 Set

Multiple unordered distinct values may exist.

Example:

```text
manufacturing.related_design_files
```

Ordered-list semantics MAY be added later if a concrete use case requires them.

Implementations SHOULD NOT simulate list/set values using numbered property names such as:

```text
file_1
file_2
file_3
```

---

# 23. Metadata Attachment Targets

Metadata SHOULD be attachable to more than physical objects.

A property definition MUST declare which entity kinds it may target.

Possible targets include:

```text
Object
ObjectType
Principal
Relationship
Checkout
Transfer
ManufacturingJob
Site
WorkflowRun
```

Example:

```text
installation.torque
```

may belong on an `installed_in` relationship rather than on either object.

Example:

```text
openlab.return_state
```

may belong on a checkout record rather than on the physical asset.

The owning module SHOULD place state on the entity whose lifecycle most accurately matches the data.

---

# 24. Applies-To Validation

The metadata subsystem MUST reject a value attached to an unsupported entity kind.

Example definition:

```text
property_key: manufacturing.printer
applies_to:
    ManufacturingJob
```

Attempting to attach it directly to an unrelated `Principal` MUST fail validation.

---

# 25. Type-Level Defaults and Object Overrides

Metadata MAY support type-level defaults where semantically useful.

Effective resolution SHOULD be:

```text
entity-specific override
    else
type-level default
    else
unset
```

Type-level defaults MUST NOT be copied into each new object merely for convenience.

Example:

```text
ObjectType: 12 AWG Automotive Wire

electrical.voltage_rating = 60 V
```

Instances inherit this effective value unless explicitly overridden.

If historical instances require different semantics after a type definition changes, the implementation SHOULD use a new type/revision or explicit overrides rather than silently rewriting history.

---

# 26. Metadata Write Policy

Each metadata property definition SHOULD define a write policy.

Recommended MVP categories:

```text
owner_only
privileged
immutable
```

## 26.1 owner_only

Only the owning module/workflow capability may mutate the property.

Example:

```text
openlab.return_state
```

## 26.2 privileged

Users or scripts with the appropriate explicit permission may mutate it.

## 26.3 immutable

Once written, ordinary application operations may not modify it.

Example:

```text
manufacturing.original_gcode_sha256
```

A privileged repair/migration mechanism MAY exist separately.

---

# 27. Metadata Read Policy

Normal metadata access and raw metadata inspection MAY have separate permissions.

A module MAY read metadata in namespaces it requires.

Generic raw metadata APIs SHOULD require explicit permission.

The normal presentation API SHOULD not require clients to understand raw metadata keys.

---

# 28. Metadata Index Policy

A property definition SHOULD specify whether and how values participate in search.

Recommended categories:

```text
not_indexed
exact
range
full_text
reference
```

Examples:

```text
openlab.return_state
    exact

electrical.voltage_rating
    range

manufacturing.notes
    full_text

manufacturing.printer
    reference
```

The implementation MUST NOT assume every metadata value is suitable for generic indexing.

Opaque JSON metadata SHOULD default to `not_indexed`.

---

# 29. Metadata Schema Evolution

Property definitions MAY evolve.

Schema changes MUST preserve data compatibility or use explicit migration.

A module SHOULD version its metadata schema when incompatible changes are introduced.

For example:

```text
manufacturing metadata schema v1
manufacturing metadata schema v2
```

A module upgrade MUST NOT silently reinterpret existing persisted values under incompatible semantics.

---

# 30. Module-Owned Presentation

Modules MAY contribute presentation elements to entity pages and other UI surfaces.

A module presentation provider MAY inspect:

- first-class core state;
- metadata it owns;
- metadata it is authorized to read;
- relationships;
- referenced entities;
- attachments.

It then produces user-facing display components.

Example:

```text
Manufacturing
-------------
This object was printed in: Prusa MK4 #2
Filament: Steel
Material used: 402 kg
Design file: final_prototype_revision_final_real_final.gcode [Download]
Time of manufacture: 08 Nov 1932
```

The generic object-page renderer does not need to understand manufacturing semantics.

---

# 31. Presentation Provider Triggers

A module MAY decide whether to contribute a section based on entity state.

Example:

```text
if entity has manufacturing provenance:
    contribute Manufacturing section
else:
    contribute nothing
```

A module SHOULD NOT create empty UI sections merely because the module is installed.

---

# 32. Standard Presentation Component Vocabulary

Server-side modules SHOULD NOT inject arbitrary HTML or JavaScript into the official UI.

Instead, modules SHOULD contribute typed presentation components from a standard vocabulary.

Recommended initial component types:

```text
Text
KeyValue
EntityLink
Quantity
Date
DateTime
Badge
FileDownload
Image
Table
List
ActionButton
Warning
Status
```

The UI client controls final rendering.

This keeps:

- the web UI safe;
- the Android client compatible;
- third-party frontends able to render module content;
- presentation consistent across modules.

---

# 33. Example Presentation Model

A manufacturing module might return conceptually:

```json
{
  "section_id": "manufacturing",
  "title": "Manufacturing",
  "components": [
    {
      "type": "EntityLink",
      "label": "Printer",
      "entity_id": "..."
    },
    {
      "type": "Quantity",
      "label": "Material used",
      "value": "402",
      "unit": "kg"
    },
    {
      "type": "FileDownload",
      "label": "Design file",
      "blob_id": "..."
    },
    {
      "type": "DateTime",
      "label": "Time of manufacture",
      "value": "2032-11-08T14:02:11Z"
    }
  ]
}
```

This is a presentation model, not the persisted metadata representation.

---

# 34. Prose Presentation

Modules MAY also contribute localized message templates rather than only key/value rows.

For example, the manufacturing module may conceptually emit:

```text
message_id:
    manufacturing.object_printed_at

arguments:
    printer = <entity UUID>
```

The UI/localization layer may render:

```text
This object was printed at Prusa MK4 #2.
```

The persisted metadata MUST NOT store the English sentence as canonical state.

---

# 35. Module Actions

Modules MAY contribute user-facing actions in addition to display elements.

Examples:

```text
Return Item
Download G-code
Reprint Label
Mark Calibration Complete
Open Manufacturing Job
```

Actions MUST use semantic command/workflow APIs and remain subject to authorization rules.

Presentation does not grant mutation authority.

---

# 36. Core Classification Presentation

The core classification module is responsible for turning classification assignments into user-facing tag/chip displays.

Example stored assignment:

```text
entity -> Tag UUID: M5 Slot Nut
```

The classification module resolves inheritance and may display:

```text
[M5 Slot Nut] [Slot Nut] [M5 Hardware] [Hardware]
```

The generic metadata renderer is not responsible for classification display.

---

# 37. Checkout Boundary

The earlier design decision regarding checkout remains unchanged.

`checked_out_to` MUST NOT be stored as arbitrary metadata when the checkout subsystem is active.

The canonical representation remains a first-class checkout record.

Example:

```text
Checkout
--------
asset
borrower_principal
opened_at
opened_by
closed_at
closed_by
```

A UI MAY expose:

```text
Checked out to: Alp
```

as a derived presentation.

OpenLab-specific lifecycle state MAY remain metadata.

Example:

```text
openlab.return_state = "desk_pending"
```

The OpenLab module interprets this metadata and renders an appropriate status/action.

---

# 38. Metadata Must Not Become a Backdoor Around Core Invariants

Modules MUST NOT recreate first-class core state in private metadata to bypass core semantics.

For example, a plugin MUST NOT implement:

```text
plugin.foo.checked_out_to = <principal>
```

and treat it as equivalent to the canonical checkout system.

Likewise, plugins MUST NOT replace:

- ownership;
- custody;
- quantity holdings;
- physical parent;
- transfer authority;

with private metadata if they intend to alter canonical inventory behavior.

Metadata extends the model; it does not replace core invariants.

---

# 39. Recommended Module Contract

A module MAY define or contribute:

```text
metadata schemas
metadata validators
semantic commands
workflow actions
event consumers
search/index definitions
presentation providers
page sections
badges/statuses
action buttons
table columns
```

This allows a module to own both:

```text
machine-facing semantics
```

and:

```text
human-facing presentation
```

without forcing generic inventoryzing code to understand the domain.

---

# 40. Metadata Mutation Through Semantic APIs

Where practical, modules SHOULD expose semantic mutation operations.

Example:

```text
manufacturing.record_print(...)
openlab.request_return(...)
calibration.complete(...)
```

rather than requiring callers to directly write:

```text
manufacturing.*
openlab.*
calibration.*
```

metadata.

This provides a stable location for:

- validation;
- authorization;
- side effects;
- audit events;
- outbox events;
- invariants.

Direct metadata write APIs MAY still exist for internal/module use.

---

# 41. Metadata History

Metadata mutations that affect persisted state SHOULD participate in the ordinary history/audit mechanism.

A metadata change SHOULD record enough information to answer:

```text
what changed?
which entity?
which property?
old value?
new value?
who/what changed it?
when?
through which command/workflow/module?
```

Highly sensitive values MAY require redaction policies in future deployments.

---

# 42. Metadata and Replication

Persisted metadata that forms part of canonical site state SHOULD replicate through normal domain events/projections when replication is enabled.

Metadata replication SHOULD preserve:

```text
property key
typed value
schema/version identity where necessary
entity identity
mutation/version ordering
```

Presentation elements themselves SHOULD NOT be replicated as canonical state.

The destination UI/module regenerates presentation from replicated state.

---

# 43. Metadata and Federation

Federated sites MAY receive metadata associated with transferred or replicated entities.

A receiving system that does not have the owning module installed SHOULD still be able to preserve unknown metadata without interpreting it, subject to compatibility/security policy.

Unknown metadata MUST NOT be rendered generically to normal users merely because its key/value is present.

A later module installation MAY interpret preserved metadata if schema compatibility allows.

---

# 44. Unknown Metadata

The core SHOULD tolerate persisted metadata whose owning optional module is unavailable.

Such metadata SHOULD be:

```text
preserved
replicated where appropriate
visible in privileged raw inspection
not normally user-rendered
not mutated by unrelated modules
```

This prevents uninstalling or temporarily disabling a module from destroying its data.

---

# 45. Metadata Deletion and Module Removal

Removing a module MUST NOT automatically delete its persisted metadata unless an explicit destructive migration is requested.

Module removal MAY cause its user-facing presentation to disappear.

The underlying metadata remains available for:

- reinstall;
- audit;
- migration;
- export;
- later cleanup.

---

# 46. UI Composition Order

The standard object/entity page SHOULD be composed from:

```text
core identity
core classification
core location/containment
core custody/checkout
core attachments
module-contributed sections
module-contributed actions/status
```

The exact visual layout is a UI concern and MAY differ across:

- desktop web;
- phone web;
- kiosk;
- Android handheld;
- third-party clients.

Modules SHOULD provide semantic presentation components rather than assume one fixed layout.

---

# 47. Presentation Localization

Modules SHOULD use stable message/component identifiers where practical so that presentation can be localized.

The stored metadata MUST remain language-neutral.

For example:

```text
openlab.return_state = "desk_pending"
```

is stored.

The UI may render:

```text
Awaiting monitor verification
```

in English or an equivalent localized phrase.

---

# 48. Metadata API Boundary

Generic metadata APIs SHOULD operate on canonical property keys and typed values.

Example conceptual API:

```text
GET /entities/{uuid}/metadata
```

MAY exist for privileged/internal use.

Normal UI clients SHOULD preferentially consume:

```text
GET /entities/{uuid}/presentation
```

or equivalent composed resource output rather than reconstruct presentation from raw metadata.

The exact endpoint structure is implementation-defined.

---

# 49. Storage Model Guidance

A normalized implementation MAY use conceptual tables such as:

```text
metadata_property_definitions
metadata_property_values
```

where `metadata_property_values` contains at least:

```text
entity_id
property_definition_id
typed value representation
value/version metadata
```

The physical SQL encoding of typed values is implementation-defined.

Possible strategies include:

- per-type nullable columns;
- JSONB with schema validation;
- separate typed value tables;
- hybrid approaches.

The implementation SHOULD optimize for:

- type safety;
- validation;
- indexing;
- migration clarity;
- queryability.

It SHOULD NOT choose an opaque representation that makes common indexed metadata queries impractical.

---

# 50. Classification and Metadata Are Separate Search Inputs

Search MAY combine classification and metadata predicates.

Examples:

```text
tag:"Hardware"
```

```text
metadata electrical.voltage_rating >= 100 V
```

```text
tag:"Measurement Equipment"
AND metadata calibration.due_before < 2030-01-01
```

However, the underlying systems remain distinct.

The search engine SHOULD NOT reduce classification tags to arbitrary metadata strings.

---

# 51. Acceptance Tests

Before the metadata subsystem is considered complete, tests SHOULD verify at least:

1. classification tags and metadata are stored through distinct canonical mechanisms;
2. classification inheritance remains acyclic;
3. raw metadata is not dumped onto normal object pages;
4. module presentation can transform an entity reference into a human-readable link;
5. quantity metadata preserves decimal/unit semantics;
6. enum metadata rejects undeclared values;
7. `applies_to` rejects unsupported entity kinds;
8. owner-only metadata cannot be modified through ordinary user permissions;
9. immutable metadata rejects normal mutation;
10. unknown module metadata remains preserved when the module is disabled;
11. module removal does not silently delete metadata;
12. presentation disappears if its module is unavailable while raw metadata remains intact;
13. classification presentation includes inherited tags;
14. checkout remains first-class and is not duplicated into writable metadata;
15. module-specific lifecycle state may exist as metadata;
16. direct raw metadata editing is audited and privilege-gated;
17. metadata presentation is generated from module logic rather than persisted prose strings;
18. different clients may render the same presentation model differently without changing canonical state.

---

# 52. Final Terminology Freeze

Future specifications and implementation SHOULD use the following terms consistently.

| Term | Meaning |
|---|---|
| **Classification Tag** | Human-facing hierarchical classification such as `Hardware`, `M5`, `Needs Repair` |
| **Metadata Property** | Typed machine-facing extensible key/value state |
| **Metadata Schema / Property Definition** | Definition of type, validation, ownership, cardinality, applicability, and indexing for a metadata key |
| **Module Presentation** | Human-facing rendering of metadata/core state |
| **Relationship** | Typed edge between entities |
| **First-Class Core State** | Dedicated universal state with core-enforced semantics/invariants |

The generic term **attribute** SHOULD be avoided in new architecture documents unless referring to a specific API/library concept, because it previously blurred human-facing descriptive fields and machine-facing metadata.

---

# 53. Design Principle

The intended rule is:

> **Persist semantics, not presentation.**

The database stores:

```text
manufacturing.printer = <UUID>
manufacturing.material_used = 83.4 g
manufacturing.design_file = <blob UUID>
```

The manufacturing module decides to show:

```text
Manufacturing

This object was printed in: Prusa MK4 #2
Material used: 83.4 g
Design file: motor_mount_rev7.gcode [Download]
```

The core remains extensible because it does not need to understand every domain.

The UI remains usable because users do not need to understand internal metadata keys.

The module boundary remains strong because modules own both the semantics and the presentation of their metadata.

This separation is REQUIRED for future module development.
