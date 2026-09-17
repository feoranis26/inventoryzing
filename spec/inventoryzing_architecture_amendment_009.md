# inventoryzing Architecture Amendment 009

**Status:** Draft architectural amendment  
**Scope:** Single-parent object-type inheritance, abstract types, inherited classification, typed property applicability, defaults, and object overrides  
**Applies to:** Original architecture handoff + Amendments 001–008  
**Intent:** Support reusable product-family definitions and editable instance properties without copying ancestor tags or defaults

---

## 1. Purpose and Precedence

Object types MUST support a single-parent hierarchy. This hierarchy belongs in the first editable-property implementation, so a shared abstract type can define classification and available fields, product-family types can extend it, and concrete product types can supply defaults.

Where this amendment conflicts with earlier wording, this amendment takes precedence. Precedence follows amendment order; earlier documents do not require individual superseded markers.

This amendment extends the dynamic-default rules in Amendment 001 and the metadata rules in Amendments 004–007. It does not require programming-language classes, executable methods, multiple type inheritance, distributed reconciliation, or automatic recovery work before the MVP.

## 2. Distinct Concepts

| Concept | Meaning | Example |
|---|---|---|
| Object | An individual inventory record representing an instance | Tote A17 |
| Object type | A reusable definition of shared fields, defaults, and classification | Medium SuperStack Tote |
| Abstract object type | A type used to organize and supply inherited definitions, unavailable for direct object assignment | Storage container |
| Classification tag | Membership in a category | Stackable |
| Property definition | A stable, namespaced identity with a declared value type and validation schema | `storage.volume`, a quantity with volume units |
| Property applicability | Declaration that a type and its instances expose a property | Containers have a Volume field |
| Property default or override | A value, or an explicit instruction that the effective value is unset | Volume = 20 US gal |

Type inheritance, physical containment, and the classification-tag graph are separate relationships. A type parent MUST NOT become an object's physical parent or implicitly create a classification tag.

Measurements such as capacity MUST be represented as typed properties, rather than tags such as `6qt`, `6 quart`, or `6`.

## 3. Type Hierarchy and Abstract Types

Each type MAY have one parent type. Roots have no parent. Self-parenting and cycles MUST be rejected, including cycles caused by concurrent hierarchy edits.

An object continues to have at most one directly assigned type. Its type ancestors are derived from that assignment; they are not additional direct assignments.

Abstract and concrete types MAY both have descendants. Abstractness is an explicit flag on each type and is not inherited automatically. A concrete child of an abstract parent is assignable to objects.

An abstract type MAY declare properties, defaults, and tags, but MUST NOT be directly assigned to an object. Type selectors used for object creation or reassignment MUST exclude abstract types from selectable choices while permitting their use as grouping headings.

Changing an assigned concrete type to abstract MUST be rejected while objects remain directly assigned to it. Objects assigned to concrete descendants do not prevent an ancestor from being abstract. The operation MUST NOT silently reassign existing objects.

Existing types migrate as concrete roots, preserving their identities, directly assigned tags, defaults, and object assignments.

### 3.1 Type Filtering

Filtering inventory by a type MUST include objects assigned directly to that type or any descendant type. Abstract types MUST be selectable as filters even though they cannot be assigned directly to objects. Selecting Storage container therefore finds objects assigned to Medium SuperStack Tote through its ancestor chain.

The API and UI MUST use the same descendant-inclusive semantics. If an exact-assignment filter is provided, it MUST be explicitly distinguished from the ordinary type filter. Changing a type's parent MUST affect subsequent filter results consistently with the current hierarchy.

This behavior is part of the initial hierarchy implementation. Broader search syntax, ranking, and query composition remain a separate design discussion.

## 4. Inherited Classification

A type's effective tags are the union of its own direct tag assignments and the direct tag assignments of all ancestor types, expanded through the existing classification-tag inheritance rules.

An object's effective tags additionally include its own direct assignments. Duplicate effective tags MUST be removed by tag identity, while retaining enough provenance to explain all contributing sources.

Inherited tags MUST NOT be copied into descendant assignment rows. Adding or removing a tag on an ancestor changes the effective classification of descendants that derive that tag from it.

Removing a direct assignment removes only that contribution. A tag remains effective if another direct or inherited source supplies it. The UI MUST distinguish removing a direct assignment from inspecting an inherited one.

This version does not introduce negative tag assignments or descendant exclusions. A descendant that should not be classified as a storage container should not inherit from a type that supplies that classification.

## 5. Property Definitions and Applicability

Property definitions retain stable identity, canonical namespaced keys, schema revisions, and ownership as defined in Amendments 004 and 005. An administrator MAY create definitions in an authorized site-managed namespace; installed modules MAY contribute definitions in their own namespaces.

A type's property declaration references an existing property definition. It does not create a separate definition with the same name for each type.

Effective property applicability is the union of declarations on the type and its ancestors. An applicable property MUST be visible as a field even when no ancestor supplies a value. Applicability does not imply that a value is required.

The definition's entity-kind restrictions and write policies continue to apply. A type declaration MUST NOT expand a module property's permitted attachment targets or grant permission to edit it.

A descendant MAY add properties and supply defaults for inherited properties. It MUST NOT locally redefine an inherited property's data type, cardinality, dimension, namespace, or validation schema. For example, it cannot reinterpret the inherited Volume property as free text. Authorized schema evolution remains a separate property-definition operation.

Defaults and object overrides for these type-declared fields MUST reference applicable properties. Other module-owned metadata attachments remain governed by their existing contracts; the type hierarchy is not a new requirement for every metadata value in the system.

The initial implementation does not require descendant removal of inherited applicability. Explicitly unsetting a value leaves the field applicable and visible.

## 6. Value Resolution and Explicit Unset

For each applicable property, resolve the first explicit entry in this order:

```text
object entry
    -> directly assigned type entry
    -> parent type entry
    -> successive ancestor entries, nearest first
    -> unset if no entry exists
```

Every level MUST distinguish these three states:

| Local state | Resolution behavior |
|---|---|
| Inherit | No local entry; continue to the next ancestor/default source |
| Value | Use the locally supplied typed value; stop traversal |
| Explicitly unset | Effective value is unset; stop traversal |

Explicitly unset is a persisted override state, not an empty string or a magic property value. The storage and API representation MUST distinguish it from absence of an entry and from any schema-permitted null value.

Deleting a local value or unset entry restores inheritance. The UI MUST provide separate actions for **Use inherited value** and **Leave unset** where inheritance is available.

Zero, false, an allowed empty string, and an empty set are actual values, not absence. Set-valued properties continue to use replacement semantics from Amendment 005: a local set replaces the entire inherited set, including when the local set is empty. Sets MUST NOT be unioned merely because classification tags are additive.

An invalid explicit value MUST NOT silently fall back to an ancestor. It remains the selected value with its validation status exposed according to the existing repair and authorization rules.

## 7. Typed Quantities and Units

A quantity definition declares its dimension, canonical unit, and compatible allowed units. The quantity is one typed value containing an amount and a unit; it MUST NOT be modeled as unrelated amount and unit fields.

The editor MUST offer units compatible with the property's dimension. A Volume field accepts supported volume units and rejects mass or length units. It MAY have no default amount while still declaring its dimension and unit choices.

Amounts and conversion follow Amendment 005's exact decimal, canonicalization, and wire-format rules. Display-unit preferences do not change the underlying quantity or establish a new property identity.

Unit identities and labels MUST distinguish US gallons from imperial gallons. An ambiguous label such as `gallon` MUST NOT silently select one. The example in this amendment uses **20 US gal**.

## 8. Live Inheritance, Editing, and Provenance

Inherited defaults are resolved dynamically. They MUST NOT be copied into newly created objects or descendant types as implicit overrides.

Editing a parent default affects descendants that still inherit it. A closer explicit value or unset entry blocks that change. Editing a display name or description preserves the type or property identity.

Type management MUST expose parent selection, abstractness, name, description, direct tags, applicable properties, and local defaults. Object property editing MUST distinguish local values from inherited values and support restoring inheritance or explicitly leaving a field unset.

For each effective property, APIs and UI MUST expose the field's definition identity, effective state, typed value when available, and source. The source distinguishes the object, a named type ancestor, explicit unset, and no supplied value. The source of applicability may differ from the source of the value and SHOULD also be available.

Inherited tags MUST show their originating assignments. Editing an inherited field MUST make it clear whether the user is creating a local override or navigating to its source to change a shared default.

The administrator property catalog MUST include definition keys, human-readable labels, data types, quantity dimensions and units where relevant, and ownership. Type inspection MUST show applicable fields even when unset.

## 9. Structural Changes and Consistency

Parent changes MUST be authorized semantic operations with concurrency protection and cycle checks. A successful change takes effect atomically for subsequent hierarchy resolution.

Reparenting changes inherited tags, applicable properties, and default sources. Type reassignment similarly changes an object's inherited state. The UI SHOULD show the affected inheritance before applying such an edit; neither operation may silently discard stored local values or unset entries.

For the initial implementation, a structural or applicability edit that would leave stored local entries outside the effective applicable-property set MUST be rejected with an actionable explanation. The caller can explicitly clear the affected entries or retain the property through an authorized declaration before retrying. This avoids requiring an automatic property-migration workflow for the MVP.

For a type reparenting or property-applicability edit, these checks MUST cover the changed type, every affected descendant type, and all objects assigned to those types. They MUST inspect both local values and explicit-unset entries against the proposed effective applicability. Checking only the directly edited type is insufficient. Object type reassignment MUST perform the equivalent check for that object.

Validation and the mutation MUST share concurrency protection so a concurrent property write, type assignment, or hierarchy edit cannot invalidate the check before commit. A rejected operation MUST leave the hierarchy and stored entries unchanged and identify the blocking properties and affected records within the caller's read permissions.

Changing a type's local data increments that type's version and records the applicable history. It MUST NOT increment every descendant's version merely because derived tags or values changed. Object override changes increment the object's version.

Derived caches and validation MUST depend on the relevant ancestor values, property schema, and hierarchy, or recompute on read. An object's own version alone is insufficient to establish freshness. A parent change can alter resolution even when the old value source itself has not changed.

These operations use the existing authorization, command, transaction, receipt, history, and outbox rules. The MVP may resolve directly on read; a new materialization or reconciliation subsystem is not required.

## 10. Presentation Providers and Labels

The effective-property resolver belongs to the hardware-independent inventory/metadata layer. Presentation providers consume that resolved state and provide structured human-readable fields with separate label and formatted value, while preserving typed values and authorization boundaries.

A provider's formatted field is a read projection, not a second independently writable copy of a property. Computed provider fields need not have stored defaults or be editable. Existing provider fields and editable metadata definitions MUST NOT be conflated solely because both appear in a property list.

A built-in typed-property editor and presentation provider MAY expose administrator-defined fields using their human-readable labels, formatted values, units, and provenance. A custom module UI is not required for each such field.

The earlier prohibition on generic raw-metadata presentation is relaxed for explicitly authorized administration and debugging interfaces. These interfaces MAY show namespaced keys, raw typed values, schemas, validation results, and resolution provenance, and MAY offer generic editing where the caller has the relevant write permission. Opening a debug view does not itself grant additional access.

Property read/write policies and the existing raw-metadata permissions remain authoritative, including for history and export. Values MUST be rendered as inert data in generic inspection tools; displaying raw metadata does not authorize executing embedded HTML, JavaScript, or templates. Ordinary inventory and label presentation continues to use structured, human-readable fields.

The web UI and labeling module SHOULD use the same property formatting, including units and date formatting. Labels may include a field's caption and value or only its value.

The labeling module resolves effective values when rendering. Inherited changes therefore appear on future renders without rewriting templates. Missing, inapplicable, explicitly unset, or unavailable properties MUST produce a defined empty/unavailable rendering result rather than crashing the coordinator. Read permissions remain enforced; unavailable data MUST NOT be exposed through labels as an alternative access path.

Render-context placeholders such as printing time remain owned by the labeling module. They are not persisted object properties or inherited type defaults. No inventory-core dependency on printing is introduced.

## 11. Worked Example

| Entity | Local declarations | Effective result |
|---|---|---|
| Storage container — abstract | Storage container tag; `storage.volume` applicable, quantity dimension Volume; no value | Establishes classification and an unset Volume field |
| ACME SuperStack — abstract | Parent: Storage container; Stackable tag | Inherits both tags and Volume applicability |
| Medium SuperStack Tote — concrete | Parent: ACME SuperStack; Volume default: 20 US gal | Both tags and a 20 US gal default |
| Tote A17 — object | Type: Medium SuperStack Tote; no local Volume entry | Volume: 20 US gal, sourced from Medium SuperStack Tote |

If Tote A17 explicitly sets Volume to 18 US gal, later changes to the type default do not affect that value. If it explicitly leaves Volume unset, no ancestor supplies a replacement. Choosing Use inherited value removes either local entry and resolves the current type default again.

## 12. Acceptance Criteria

The implementation MUST verify that:

1. The worked example can be authored without copying ancestor tags or field definitions.
2. Self-parenting, cycles, and concurrent edits that would jointly create a cycle are rejected.
3. Abstract types cannot be assigned directly, while concrete descendants can.
4. An assigned concrete type cannot become abstract without explicit reassignment of its objects.
5. An applicable property with no default appears as an unset editable field.
6. Resolution selects the nearest explicit value or unset entry, and restoring inheritance reveals the current ancestor value.
7. Zero, false, empty sets, and explicitly unset remain distinguishable from inheritance.
8. Set-valued overrides replace; inherited classification tags accumulate and retain provenance.
9. Quantity entry rejects incompatible units and distinguishes US and imperial gallons.
10. A child cannot redefine an inherited property schema through a local default edit.
11. Parent edits update inherited values and tags without copying data or rewriting descendant versions.
12. Reparenting and type reassignment do not silently discard or hide stored local entries.
13. Cached effective state becomes stale after relevant hierarchy, source-value, or schema changes.
14. Existing types and object assignments retain their meaning after migration to concrete root types.
15. UI and label consumers use the same resolved property formatting, and missing fields do not crash rendering.
16. Existing property permissions apply to inherited values, provenance, editing, and label output.
17. Filtering by an abstract or concrete ancestor includes objects assigned to descendant types, with matching API and UI semantics; an explicitly offered exact-assignment filter remains distinguishable.
18. Reparenting and applicability checks detect blocking local values and explicit-unset entries anywhere in the affected subtree, including its objects, and remain valid under concurrent writes and assignments.
19. Authorized administration/debug views can inspect raw metadata and edit it where permitted, while unauthorized access and automatic execution of stored content remain prohibited.

## 13. Implementation Sequence

1. Add type parentage and abstractness, migrate existing types as concrete roots, and implement cycle-safe hierarchy mutations and descendant-inclusive type filtering.
2. Add typed property definitions, declarations, local values and explicit-unset entries, quantity units, effective resolution, and the required subtree/concurrency checks. Property writes MUST NOT ship without those checks on existing hierarchy mutations.
3. Extend type and object editing with inherited-value provenance and explicit override controls; provide the administrator catalog and authorized administration/debug inspection.
4. Connect effective properties to the shared presentation providers and label renderer, and verify the worked example and acceptance criteria end to end.

These are implementation steps for one usable feature. They do not require completing the broader search design or the deferred recovery/reconciliation work.
