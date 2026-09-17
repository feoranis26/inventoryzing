# inventoryzing Architecture Amendment 005

**Status:** Accepted architectural amendment  
**Scope:** Metadata schema evolution, set override semantics, entity versioning, exact decimal wire format, quantity normalization, and presentation security  
**Applies to:** Original architecture handoff + Amendments 001–004  
**Intent:** Resolve the first adversarial review of the typed metadata subsystem without redesigning the subsystem

---

# 1. Purpose

This amendment resolves the remaining findings from the adversarial review of Amendment 004.

It clarifies:

1. metadata definition identity and schema evolution;
2. how inherited set-valued metadata is overridden;
3. how metadata mutations interact with entity versioning;
4. exact decimal serialization and quantity normalization;
5. security boundaries for raw metadata and module-owned presentation.

Where this amendment conflicts with earlier wording, **this amendment takes precedence**.

No new major subsystem is introduced.

---

# 2. Schema History Does Not Grandfather Invalid Data

Metadata schema revisions MAY be retained for audit, migration, and historical interpretation.

However:

> **A value that was valid under an older schema is not automatically considered valid under the current active schema.**

If the active schema becomes more restrictive and an existing value no longer satisfies it, that value MUST be treated as requiring repair.

Example:

```text
v1 schema:
    manufacturing.nozzle_temperature
    range: 0 .. 1000 °C

stored value:
    750 °C
```

The schema is later corrected:

```text
v2 schema:
    range: 150 .. 350 °C
```

The existing value:

```text
750 °C
```

MUST NOT remain silently valid merely because it was accepted under v1.

It MUST instead be surfaced as something equivalent to:

```text
INVALID_UNDER_CURRENT_SCHEMA
NEEDS_REPAIR
```

The system SHOULD preserve the value until repaired unless policy explicitly requires destructive migration.

---

# 3. Property Definition Identity

Every metadata property definition MUST have stable identity independent of its current schema revision.

Conceptually:

```text
property_definition_id
canonical_key
owner_module
current_revision
```

Example:

```text
property_definition_id = UUID P
canonical_key          = manufacturing.material_used
```

The property remains the same logical property across compatible or corrective schema revisions.

---

# 4. Schema Revisions

Property definitions SHOULD maintain revision history sufficient for:

- audit;
- migration;
- debugging;
- determining which schema was active when a value was written;
- explaining why a previously accepted value is now invalid.

A conceptual revision record may include:

```text
property_definition_id
revision_number
schema
created_at
created_by/module
supersedes_revision
```

Historical revisions SHOULD be immutable once superseded.

However, **metadata values do not derive permanent validity from the revision under which they were created**.

Current validity is determined against the current active schema unless a specific migration or compatibility rule says otherwise.

---

# 5. Value Revision Provenance

A metadata value SHOULD record enough provenance to determine which property-definition revision was active when it was created or last modified.

Example:

```text
entity_id
property_definition_id
value
written_against_revision = 4
```

This field is historical provenance.

It MUST NOT be interpreted as:

```text
"continue validating this value forever against revision 4"
```

Instead, ordinary reads and mutation validation use the current active schema.

---

# 6. Current-Schema Validation

Whenever a metadata value is:

- created;
- modified;
- explicitly revalidated;
- migrated;
- loaded into a workflow that requires validity;

the current active property schema MUST be used.

Existing stored values MAY become invalid after a schema revision.

The system MUST be able to represent that condition without deleting the value.

Recommended conceptual state:

```text
VALID
INVALID_CURRENT_SCHEMA
UNKNOWN_SCHEMA
MIGRATION_REQUIRED
```

Exact names are implementation-defined.

---

# 7. Schema Tightening and Repair

When a new property-definition revision makes stored values invalid, the owning module SHOULD provide one or more of:

- a migration;
- a repair workflow;
- a validation report;
- an administrator repair action.

The system SHOULD make affected entities discoverable.

Example:

```text
metadata-invalid:manufacturing.nozzle_temperature
```

or an equivalent indexed validation status.

A module MUST NOT silently coerce an out-of-range value into range unless that coercion is explicitly part of a migration policy.

---

# 8. Schema Revision Authorization

Property schema revision is a privileged operation.

Only the owning module or an explicitly authorized schema-management capability MAY publish a new active revision.

Ordinary metadata writers MUST NOT alter:

- value type;
- cardinality;
- applies-to rules;
- validation ranges;
- unit dimension;
- write policy;
- namespace ownership.

Schema changes MUST be audited.

---

# 9. Definition Removal

A property definition that has persisted values MUST NOT simply disappear.

If a module is removed or a property is retired:

```text
property definition -> archived/retired
values              -> preserved
```

The current active module set may no longer interpret or present the property, but the retained definition identity and schema history must remain sufficient for:

- export;
- audit;
- later reinstall;
- migration;
- raw privileged inspection.

---

# 10. Set-Valued Metadata Inheritance

Set-valued metadata uses row existence to distinguish inheritance from override.

No separate override marker is required.

The normative rule is:

```text
no entity override row
    -> inherit type/default set

entity override row exists
    -> replace inherited set completely
```

This applies even if the override set is empty.

---

# 11. Empty Set Is an Explicit Override

Suppose the type defines:

```text
compatible_materials = {
    PETG,
    ABS,
    ASA
}
```

An object with no override row resolves to:

```text
{
    PETG,
    ABS,
    ASA
}
```

If an explicit object override row exists with:

```text
{}
```

the effective value is:

```text
{}
```

The empty set does not mean "missing".

It means:

> This entity explicitly overrides the inherited value with no members.

---

# 12. Restoring Set Inheritance

Inheritance is restored by deleting the entity-level override row.

Example:

```text
DELETE object override row
```

causes resolution to return to:

```text
type/default set
```

The system does not need a separate `inherit = true` marker.

The distinction is structural:

```text
row absent  -> inherit
row present -> override
```

---

# 13. Set Override Semantics Are Replacement Semantics

The MVP MUST use complete replacement semantics for inherited sets.

An override does not mean:

```text
inherited set UNION override set
```

unless a future property type explicitly defines additive semantics.

Therefore:

```text
type set:
    {A, B, C}

object override:
    {B, D}
```

resolves to:

```text
{B, D}
```

not:

```text
{A, B, C, D}
```

This rule keeps inheritance deterministic and easy to reason about.

---

# 14. Set Equality

Set values MUST use canonical typed-value equality.

For ordinary scalar members:

```text
"A" == "A"
```

subject to the property's normalization rules.

For entity references:

```text
same canonical entity UUID -> same set member
```

For quantities, equality MUST be evaluated after canonical unit normalization.

Example:

```text
1000 g
```

and:

```text
1 kg
```

represent the same quantity value when they have the same physical dimension.

A set MUST NOT contain both as distinct members merely because the input units differed.

---

# 15. Metadata Mutation and Entity Versions

Persisted metadata is part of entity state.

Therefore, a successful metadata value mutation MUST increment the version of the entity whose metadata changed.

Examples:

```text
Object A:
manufacturing.printer changed
    -> increment Object A version
```

```text
Checkout C:
openlab.return_state changed
    -> increment Checkout C version
```

This keeps optimistic concurrency, audit, replication, and cache behavior consistent with other entity mutations.

---

# 16. Type-Default Changes Do Not Rewrite Child Versions

Changing metadata on an `ObjectType` or other type/default provider MUST increment the version of the type/default entity.

It MUST NOT increment the version of every inheriting child merely because their effective resolved value changed.

Example:

```text
ObjectType T:
electrical.voltage_rating
60 V -> 80 V
```

causes:

```text
ObjectType T version += 1
```

It does not cause:

```text
every instance of T version += 1
```

---

# 17. Effective-Value Dependency

Because inherited effective values may change without child-version increments, caches and indexes that materialize effective metadata MUST track the relevant dependency.

Acceptable strategies include:

- type/default version dependency;
- explicit invalidation events;
- recomputation on read;
- search-index update jobs.

The implementation MUST NOT assume that an object's own version changing is the only way its effective inherited metadata can change.

---

# 18. Property Definition Changes Have Their Own Versioning

Changing a property definition or activating a new schema revision MUST update the property-definition revision/version.

It MUST NOT increment every entity containing that property.

Entities that become invalid under the new schema are discovered through validation/indexing/migration mechanisms rather than mass entity-version rewrites.

---

# 19. Exact Decimal Wire Format

Where a metadata or core quantity requires exact decimal semantics, JSON APIs MUST serialize the numeric magnitude as a decimal string.

Correct:

```json
{
  "quantity": "12.860",
  "unit": "m"
}
```

Incorrect for an exact quantity API:

```json
{
  "quantity": 12.86,
  "unit": "m"
}
```

The server MUST NOT rely on binary floating-point JSON numbers for exact persisted decimal quantities.

---

# 20. Decimal Parsing

Decimal strings MUST use a defined grammar.

The MVP SHOULD accept canonical base-10 forms such as:

```text
"0"
"12"
"12.860"
"-0.25"
```

Scientific notation MAY be supported, but if supported its grammar MUST be defined consistently.

Locale-specific syntax such as:

```text
"12,86"
```

MUST NOT be accepted by the wire protocol unless an endpoint explicitly defines localized input.

UI localization is separate from API representation.

---

# 21. Quantity Canonicalization

Unit-bearing quantities MUST be normalized to one canonical internal unit for their physical dimension or property schema.

Where practical, canonical units SHOULD be SI units or a defined SI-compatible canonical unit.

Examples:

```text
length  -> m
time    -> s
mass    -> kg
voltage -> V
current -> A
```

Dimensionless/count quantities may use units such as:

```text
ea
```

Domain-specific non-SI quantities MAY be supported where necessary, but the property schema MUST define one canonical unit.

---

# 22. Exact Unit Conversion

Conversions between compatible units MUST preserve exact decimal semantics wherever the unit relationship permits exact conversion.

Examples:

```text
1000 g -> 1 kg
15 cm  -> 0.15 m
250 mV -> 0.250 V
```

Internally, equality, range checks, indexing, and set deduplication operate on canonical normalized values.

---

# 23. Display Units Are Presentation

Canonical storage units do not determine display units.

A module/UI MAY render the most sensible unit for the context.

Example canonical storage:

```text
0.00015 m
```

may display as:

```text
0.15 mm
```

Likewise a canonical mass may be displayed as:

```text
402 kg
```

rather than an inconveniently scaled base representation.

Presentation unit choice MUST NOT change the underlying physical value.

---

# 24. User Input Units

User-facing modules MAY accept convenient units.

Example:

```text
15 cm
```

The module parses and validates the unit, converts to the property's canonical dimension/unit, and submits the normalized typed value.

The metadata/core storage layer MUST reject incompatible dimensions.

Example:

```text
manufacturing.material_used
dimension: mass
```

MUST reject:

```text
12 V
```

---

# 25. Raw Metadata Visibility Is Permission-Gated

Raw metadata inspection is a security boundary.

The generic raw metadata API and advanced raw-metadata UI MUST require explicit authorization.

Normal users MUST NOT gain access to raw metadata merely because they can view the entity's normal object page.

The permission model SHOULD distinguish at least:

```text
entity.view
metadata.raw.read
metadata.raw.write
```

or equivalent capabilities.

---

# 26. Raw Metadata Editing Is Explicitly Privileged

Direct raw metadata mutation MUST require an explicit dangerous/maintenance permission.

It MUST NOT be implied by ordinary:

```text
item.edit
```

or similar general inventory permissions.

Raw metadata writes MUST:

- pass schema validation unless an explicit repair mode exists;
- be audited;
- increment the target entity version;
- emit normal history/outbox state;
- respect namespace/property write ownership unless an explicit maintenance override is authorized.

---

# 27. Arbitrary HTML and JavaScript Are Prohibited in Standard Presentation

Module-owned presentation MUST NOT inject arbitrary executable HTML or JavaScript into the standard official UI.

Module presentation MUST use registered typed presentation components.

Examples include:

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

Text values MUST be escaped according to the frontend rendering context.

This is a MUST-level security requirement.

---

# 28. Presentation Components Are Data, Not Code

A module presentation response is declarative data.

Example:

```json
{
  "type": "EntityLink",
  "label": "Printer",
  "entity_id": "..."
}
```

It is not:

```text
<script>...</script>
```

and not:

```text
raw_html = "<a onclick=...>"
```

Official clients decide how registered components are rendered.

---

# 29. Action Buttons Reference Registered Semantic Actions

An `ActionButton` presentation component MUST reference a registered semantic action or workflow identifier.

Example:

```text
action_id = openlab.request_return
```

It MUST NOT contain:

- arbitrary server-side script source;
- arbitrary JavaScript;
- arbitrary shell commands;
- an unrestricted URL to invoke;
- an arbitrary internal endpoint supplied by the module at render time.

The action registry defines:

- action identity;
- expected inputs;
- authorization;
- owning module;
- semantic handler/workflow.

The normal authorization engine still applies when the button is invoked.

---

# 30. Presentation Does Not Grant Authority

The presence of a presentation element MUST NOT itself authorize an operation.

Even if a module incorrectly renders:

```text
[Return Item]
```

to an unauthorized user, invoking the underlying registered action MUST independently perform authorization.

UI visibility is convenience, not security.

---

# 31. Registered External Navigation

If modules are allowed to contribute external links, links MUST use a dedicated typed component and MUST be validated against allowed URL schemes.

Official clients MUST reject dangerous executable schemes.

This is separate from semantic action invocation.

---

# 32. Module Trust Boundary

Installing a server-side module may grant that module substantial application privileges according to deployment policy.

However, module-owned presentation MUST NOT automatically grant the module arbitrary code execution inside every user's browser.

Server-side module trust and browser-side code trust are separate boundaries.

A future plugin system MAY define signed client extensions or isolated UI bundles, but that is outside the MVP and MUST use a separate security model.

---

# 33. Updated Metadata Validation Example

Consider:

```text
property:
manufacturing.material_used

revision 1:
type = quantity
dimension = mass
minimum = 0
maximum = 1000 kg
```

Stored value:

```text
402 kg
```

Later revision:

```text
revision 2:
maximum = 100 kg
```

The retained value remains:

```text
402 kg
```

but its current validation status becomes:

```text
INVALID_CURRENT_SCHEMA
```

The manufacturing module may render:

```text
Material used: 402 kg
⚠ Value violates the current metadata schema and requires repair.
```

or suppress normal presentation and expose a repair action.

It MUST NOT silently claim the value is valid because it originated under revision 1.

---

# 34. Updated Set-Inheritance Example

Type-level default:

```text
manufacturing.compatible_materials = {
    PETG,
    ABS
}
```

### Object has no override row

Effective value:

```text
{
    PETG,
    ABS
}
```

### Object override row exists

```text
{
    ASA
}
```

Effective value:

```text
{
    ASA
}
```

### Object override row exists and contains empty set

```text
{}
```

Effective value:

```text
{}
```

### Override row is deleted

Effective value returns to:

```text
{
    PETG,
    ABS
}
```

No separate override marker is required.

---

# 35. Updated Quantity Equality Example

The following inputs:

```text
1000 g
1 kg
```

normalize to the same canonical mass value.

They therefore compare equal for:

- metadata set membership;
- exact equality filters;
- duplicate detection;
- validation.

Display may choose any sensible compatible unit.

---

# 36. Acceptance Tests

The metadata subsystem MUST add tests covering at least the following.

## Schema identity and revision

1. a property keeps stable identity across schema revisions;
2. historical schema revisions remain available after supersession;
3. stored values record revision provenance;
4. current validity is evaluated against the current active schema;
5. a value valid under an old schema becomes invalid when a tighter current schema excludes it;
6. invalid legacy values are preserved and surfaced for repair;
7. schema revision requires explicit authorization;
8. property retirement preserves definitions and persisted values.

## Set inheritance

9. absent override row inherits the type/default set;
10. non-empty override row completely replaces the inherited set;
11. empty override row produces an empty effective set;
12. deleting the override row restores inheritance;
13. quantity set members deduplicate after canonical unit normalization.

## Versioning

14. metadata value mutation increments the target entity version;
15. checkout metadata mutation increments the checkout entity version;
16. changing a type default increments the type/default entity version only;
17. type-default changes do not mass-increment child versions;
18. effective-value caches/indexes correctly react to type/default version changes;
19. property-definition revision changes do not mass-increment all affected entities.

## Decimal and quantity semantics

20. exact decimal API values are transmitted as strings;
21. binary JSON numbers are rejected where the API requires exact decimal representation;
22. compatible units normalize to the same canonical value;
23. incompatible physical dimensions are rejected;
24. display-unit choice does not affect stored equality;
25. range validation operates on canonical normalized values.

## Security

26. ordinary entity-view permission does not expose raw metadata;
27. raw metadata read requires explicit permission;
28. raw metadata write requires explicit elevated permission;
29. raw write is audited and versioned;
30. standard presentation rejects arbitrary HTML/JavaScript;
31. presentation text is escaped by clients;
32. action buttons may invoke only registered semantic actions;
33. action invocation independently checks authorization;
34. arbitrary endpoint/script execution cannot be embedded in standard presentation components.

---

# 37. Final Clarification

The intended metadata model is now:

```text
Property Definition
    stable logical identity
    current active schema
    immutable retained schema history

Property Value
    typed persisted value
    written-against revision provenance
    current-schema validity status
```

Historical schema provenance explains how data came to exist.

It does **not** grandfather that data into permanent validity.

For inherited metadata:

```text
override row absent
    -> inherit

override row present
    -> replace

override row present but empty
    -> explicitly empty

override row deleted
    -> inherit again
```

For quantities:

```text
input/display units
    -> canonical exact normalized quantity
    -> comparisons/indexing/storage
```

For presentation:

```text
metadata/core state
    -> owning module
    -> typed declarative presentation components
    -> client rendering
```

Raw machine metadata remains machine-facing.

Presentation remains module-owned.

Security boundaries are enforced by backend authorization rather than by UI convention.
