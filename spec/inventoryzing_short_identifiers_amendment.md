# inventoryzing Specification Amendment
## Site-Local Short Identifiers and Micro Labels

**Status:** Proposed amendment  
**Scope:** MVP identifier and labeling subsystem  
**Purpose:** Define compact site-local identifiers for use on very small physical labels while preserving globally unique inventory identities.

---

## 1. Purpose

inventoryzing **MUST** support compact identifiers suitable for very small physical labels where encoding a full UUID, canonical URL, or federation identity would make the barcode impractically dense.

Each entity **MAY** therefore have a **site-local short identifier** in addition to its globally stable UUID.

Example:

```text
Global identity:
    0199af28-7c31-7d12-a5c8-...

Site:
    OpenLab

Local short ID:
    428731
```

The short identifier is an alias. It is **not** the canonical identity of the entity.

---

## 2. Identifier Semantics

A local identifier **MUST** be interpreted as the tuple:

```text
(site_id, local_id)
```

and **MUST NOT** be interpreted as merely:

```text
local_id
```

Therefore:

```text
OpenLab / 428731
```

and:

```text
IPI / 428731
```

may legitimately identify completely different entities.

The global UUID remains unique independently of site.

Conceptually:

```text
Entity UUID
    ├── OpenLab local ID: 428731
    ├── IPI local ID:     721904
    └── global/full identifier
```

An object transferred between sites **MAY** acquire a new local identifier at the receiving site without changing its UUID.

---

## 3. Short-ID Format

The initial implementation **SHOULD** support numeric identifiers of approximately 6–8 digits.

For example:

```text
00482173
```

Eight decimal digits provide 100 million values per site, which is sufficient for ordinary deployments while remaining highly barcode-friendly.

The database **SHOULD NOT** depend on a fixed textual length. The namespace should permit future formats such as:

```text
482173
A82K41
```

if a deployment later requires a larger identifier space.

### 3.1 Identifier Reuse

Site-local identifiers **MUST NOT** be recycled.

Once:

```text
428731 → Object UUID A
```

has been assigned, `428731` **SHOULD** remain permanently associated with Object A, even if the object is later archived or deleted.

This prevents an old physical label from unexpectedly resolving to an unrelated future object.

Archived or tombstoned entities should therefore continue to resolve to their historical records:

```text
Object 428731
ARCHIVED
```

rather than silently resolving incorrectly.

---

## 4. Data Representation

Site-local short identifiers should use the general identifier subsystem rather than a special-purpose column whose semantics leak throughout the application.

Conceptually:

```text
Identifier
----------
entity_id
namespace
issuer_site_id
value
```

Example:

```text
namespace      = inventoryzing.local
issuer_site_id = openlab-site-uuid
value          = "428731"
entity_id      = object-global-uuid
```

The database **MUST** enforce uniqueness equivalent to:

```text
UNIQUE(namespace, issuer_site_id, value)
```

The site's local-ID allocator **MUST** allocate identifiers atomically.

A PostgreSQL sequence is acceptable for this purpose because the sequence is used only to allocate unique aliases. Gaps and transaction commit ordering are irrelevant here.

---

## 5. Micro-Label Payload

A micro label **SHOULD** contain the smallest practical unambiguous local representation.

For example:

```text
I428731
```

The exact prefix is deliberately short but useful because bare numbers may collide with:

- UIN/iCard numbers
- UPC/EAN codes
- manufacturer asset IDs
- quantities
- arbitrary numeric labels

The initial protocol **SHOULD** reserve a compact inventoryzing-local syntax.

For example:

```text
I428731
```

means:

```text
inventoryzing local entity ID 428731
```

The scanner resolver interprets that value against the **current site's namespace**.

This is substantially smaller than:

```text
https://inventory.openlab.edu/o/0199af28-...
```

and materially reduces barcode density on micro labels.

The exact encoding syntax **SHOULD** be a protocol constant rather than something every site defines independently.

---

## 6. Resolution

When the OpenLab coordinator receives:

```text
I428731
```

it resolves:

```text
namespace = inventoryzing.local
site      = OpenLab
value     = 428731
```

to the object's global UUID.

Once resolved, every subsequent operation **SHOULD** use the UUID internally.

```text
SCAN
I428731
   ↓
local identifier lookup
   ↓
0199af28-...
   ↓
normal inventoryzing operation
```

The short ID should disappear from core business logic after resolution.

---

## 7. Portability Classification

Label templates **SHOULD** explicitly declare their portability.

Example:

```text
Template: Micro Local QR
portability: site_local
```

versus:

```text
Template: Standard Object QR
portability: global
```

The UI may then display:

```text
Label
Micro QR #428731

⚠ Site-local identifier
Usable only with OpenLab inventoryzing
```

This allows workflows to reason about label portability explicitly rather than guessing from physical dimensions or barcode contents.

---

## 8. Cross-Site Transfer Behavior

A cross-site transfer **MUST NOT** assume that the source site's local identifier is sufficient to identify the asset at the destination.

When an object whose only physical inventoryzing label is site-local is prepared for transfer, inventoryzing **SHOULD** warn:

> **This object's current label is local to OpenLab and cannot independently identify the object at another inventoryzing installation.**

The workflow should offer an action such as:

```text
[ Print transfer/full identifier label ]
```

A full-transfer label may contain:

- the global inventoryzing UUID
- the home authority or source-site identity
- an optional resolvable URL or authority URI

Once the destination accepts the asset:

```text
Global UUID:
    unchanged

Source local ID:
    OpenLab / 428731

Destination local ID:
    XRI / 107284
```

The destination may immediately print a new local micro label if desired.

---

## 9. Historical Local Identifiers

A source site's old local identifier does not necessarily become invalid when the object leaves that site.

The original alias **SHOULD** remain associated with the object's historical identity at that site.

Example:

```text
OpenLab / 428731
    ↓
transferred to XRI
    ↓
returns six months later
```

OpenLab can still scan the original micro label and recognize the object immediately.

The local alias therefore belongs to the site's historical identity mapping, not merely its current custody.

---

## 10. Trusted Transfer Optimization

A globally portable physical label does not necessarily have to be mandatory for every controlled transfer.

Suppose OpenLab explicitly authorizes:

```text
Object UUID A
OpenLab local ID 428731
→ XRI
```

and XRI already has the pending transfer in its synchronized transfer manifest.

XRI could scan:

```text
I428731
```

and reason:

```text
This is not one of XRI's local IDs.

There is exactly one incoming transfer from OpenLab
with source-local ID 428731.

→ Resolve to Object UUID A
```

This is a valid convenience mechanism.

However, a receiving installation **MUST NOT** resolve an otherwise contextless foreign local identifier by guessing.

If XRI receives:

```text
I428731
```

without:

- source-site context
- a matching transfer manifest
- a globally identifying companion tag
- or another trusted disambiguating mechanism

the correct result is:

```text
UNKNOWN LOCAL IDENTIFIER
```

The system should not probe arbitrary peers until something happens to match.

---

## 11. Transfer Containers and Batch Movement

For batch transfers, reprinting a globally portable label for every micro-tagged item may be unnecessary.

A transfer may instead define a manifest:

```text
Transfer Shipment #ABC
├── OpenLab/428731 → UUID A
├── OpenLab/428732 → UUID B
├── OpenLab/428739 → UUID C
...
```

The shipment or transport container itself may carry a globally resolvable transfer label.

At the destination:

```text
scan transfer container
→ establish source/transfer context

scan I428731
→ resolve using transfer manifest

scan I428732
→ resolve using transfer manifest
```

The destination can then assign its own short IDs and print new local labels where appropriate.

Therefore, the UI warning should describe the actual limitation:

> **This label is not independently portable outside its issuing site.**

It should not claim that the label must always be replaced before transfer.

---

## 12. Search and UI Behavior

The site-local short identifier **SHOULD** be a normal searchable identifier.

Typing:

```text
428731
```

into the OpenLab UI should find the object if the query is unambiguous.

An object page might display:

```text
Fluke 87V #12

UUID
0199af28-...

Local ID
428731

Labels
✓ OpenLab Micro QR
✓ Full QR
```

The UUID remains the canonical identifier exposed by APIs unless an endpoint specifically requests aliases or identifier mappings.

---

## 13. Relationship to Other Identifier Types

inventoryzing should maintain a clear identifier hierarchy:

```text
UUID
    permanent global identity

Full/global label
    independently portable physical representation

Site-local short ID
    compact alias optimized for routine local scanning

External identifiers
    UPC, serial number, iCard/UIN, manufacturer identifiers, etc.
```

A site-local identifier is therefore one member of the general identifier system, with special semantics around site scope and physical-label portability.

---

## 14. MVP Requirements

Support for site-local short identifiers and micro labels is part of the MVP.

The MVP **MUST** include:

1. Global UUIDs as canonical entity identities.
2. Site-local short identifiers.
3. Atomic allocation of site-local IDs.
4. Permanent non-reuse of allocated short IDs.
5. Resolver support for local micro-tag payloads.
6. Search by local short ID.
7. Label-template portability metadata.
8. A warning when attempting to transfer an object whose only physical inventoryzing label is site-local.
9. Ability to print or otherwise obtain a globally portable identifier for transfer.
10. Preservation of historical local aliases after transfer.

Transfer-manifest-assisted foreign short-ID resolution **MAY** be implemented after the initial MVP, provided the data model does not prevent it.

---

## 15. Acceptance Tests

The following acceptance tests **MUST** be satisfied.

### 15.1 Local Uniqueness

Two different sites may allocate the same numeric local ID to different objects without conflict.

```text
OpenLab / 428731 → UUID A
IPI     / 428731 → UUID B
```

This is valid.

### 15.2 Site Uniqueness

One site **MUST NOT** allocate the same local ID to two entities.

### 15.3 Concurrent Allocation

Concurrent object creation **MUST NOT** produce duplicate site-local IDs.

### 15.4 Non-Reuse

Archived or deleted short IDs **MUST NOT** be reassigned to unrelated entities.

### 15.5 Local Resolution

Scanning a local micro tag at its issuing site **MUST** resolve to the correct UUID.

### 15.6 Foreign Resolution

Scanning the same site-local tag at another installation **MUST NOT** resolve without explicit source or transfer context.

### 15.7 Transfer Identity

A transferred object **MUST** retain its global UUID when assigned a destination-local ID.

### 15.8 Transfer Warning

A transfer workflow **MUST** detect when an object lacks an independently portable physical identifier and warn the operator.

### 15.9 Transfer Bootstrap

A full/global label or trusted transfer manifest **MUST** be sufficient to establish the object's global identity at another trusted installation.

### 15.10 Return to Prior Site

Returning an object to a previous site **SHOULD** permit its historical site-local identifier to resolve again.

---

## 16. Normative Summary

1. Every entity has a globally stable canonical UUID.
2. An entity may additionally have one or more site-issued local aliases.
3. A site-local identifier is meaningful only together with its issuing site.
4. Site-local identifiers are optimized for compact labels and routine local scanning.
5. Site-local identifiers are never recycled.
6. Internal business logic operates on UUIDs after identifier resolution.
7. Local labels are not independently portable across inventoryzing installations.
8. Cross-site transfer must preserve global identity.
9. A receiving site may assign its own new local alias without changing the global UUID.
10. Transfer manifests may provide contextual resolution for foreign local labels, but arbitrary cross-site guessing is forbidden.
