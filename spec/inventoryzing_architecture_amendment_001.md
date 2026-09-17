# inventoryzing Architecture Amendment 001

**Status:** Accepted architectural amendment  
**Scope:** Foundational correctness contracts + site-local short identifiers  
**Applies to:** `inventoryzing` architecture handoff specification  
**Intent:** Amend, not replace, the existing specification

---

## 1. Purpose

This amendment resolves the architectural gaps identified during review of the original `inventoryzing` handoff and adds one MVP requirement that was previously omitted: **compact site-local identifiers suitable for very small physical labels**.

The overall architecture remains unchanged:

- PostgreSQL-backed modular monolith at each site
- site-local authoritative operation
- hardware-independent scanner/printer interfaces
- semantic commands
- transactional history and outbox records
- application-layer replication
- regional/corporate projections
- pluggable workflows and UI surfaces
- offline-capable handheld clients
- federated inventoryzing installations

This amendment strengthens the contracts around:

1. structural graph concurrency,
2. cross-site write authority,
3. command idempotency,
4. replication cursors and rebuilds,
5. canonical sources of truth,
6. stock accounting,
7. identifier namespaces,
8. offline conflict handling,
9. rollback and recovery boundaries,
10. compact site-local IDs and micro labels.

Multinational/legal data-governance policy is intentionally deferred.

---

## 2. Normative Language

The key words **MUST**, **MUST NOT**, **REQUIRED**, **SHOULD**, **SHOULD NOT**, and **MAY** are to be interpreted as normative implementation requirements.

Where this amendment conflicts with earlier wording in the architecture handoff, **this amendment takes precedence**.

---

## 3. Core Rule: First-Class State vs. Generic Attributes

A fact SHOULD be represented as a first-class core structure when at least one of the following is true:

1. the core must enforce invariants around it;
2. multiple subsystems need to understand its semantics;
3. it has an independent lifecycle or history;
4. it participates in authorization, federation, or replication semantics;
5. representing it only as a generic tag/attribute would create competing sources of truth.

Examples of appropriate first-class state include:

- object identity;
- ownership;
- custody;
- current physical parent;
- quantity holdings;
- active checkout;
- authority site;
- transfer records;
- principals;
- users/roles/permissions.

Deployment-specific workflow state SHOULD remain generic unless the core itself needs its semantics.

For example:

```text
checked_out_to = principal Alp
```

is core state, while:

```text
openlab.return_state = "return_pending"
```

is an OpenLab-specific workflow attribute.

The core MUST NOT introduce first-class states such as `RETURN_PENDING`, `AWAITING_INSPECTION`, or similar deployment-specific workflow concepts unless a later RFC establishes a genuinely universal requirement.

---

## 4. Canonical Representation and Derived Views

For every important fact, the implementation MUST define exactly one canonical representation.

Convenience fields, API projections, search fields, and UI properties MAY expose derived forms, but MUST NOT become independently writable competing copies.

Recommended source-of-truth matrix:

| Fact | Canonical representation | Derived/view representation |
|---|---|---|
| Active checkout | `checkout` record | `object.checked_out_to` |
| Legal owner | owner principal relation/field | UI owner label |
| Physical custody | custody relation/field | current custodian display |
| Physical parent | one active structural-parent edge | computed physical path |
| Fungible quantity | `holding.quantity` | totals and availability summaries |
| Type tags | explicit type-tag relations | inherited effective object tags |
| Object tags | explicit object-tag relations | effective tag closure |
| Type attributes | type-level value | resolved effective object value |
| Object override | object-level attribute value | resolved effective object value |
| Authority | authority record/field + epoch | displayed home/write authority |
| Local short ID | identifier record | label/search alias |

The application MUST NOT separately persist a writable `checked_out_to` attribute if checkout records are authoritative.

The same principle applies to every derived convenience property.

---

## 5. Structural Graph Concurrency

### 5.1 Problem

Locking only the directly modified object does not guarantee graph invariants.

Two concurrent transactions can independently pass cycle checks and jointly create an invalid cycle.

This applies at minimum to:

- physical containment/installation hierarchy;
- tag inheritance hierarchy;
- any future graph where acyclicity or single-parent rules are global invariants.

### 5.2 MVP strategy

The MVP MUST serialize structural graph mutations with PostgreSQL transaction-scoped advisory locks.

Recommended conceptual locks:

```text
STRUCTURAL_LOCK_PHYSICAL_HIERARCHY
STRUCTURAL_LOCK_TAG_INHERITANCE
```

A structural mutation MUST execute conceptually as:

```text
BEGIN

acquire transaction-scoped structural advisory lock

perform cycle/invariant checks

apply mutation

write history/event records

write replication outbox record

COMMIT
```

The lock MUST be held until commit or rollback.

This locking requirement applies only to topology-changing operations. Ordinary operations such as checkout, quantity consumption, attribute updates, label printing, search, and non-structural workflow state changes MUST NOT require the global structural lock.

A later implementation MAY replace coarse advisory locking with a more scalable technique, but MUST preserve equivalent serializable graph invariants.

---

## 6. Physical Hierarchy vs. General Relationships

The general relationship system MAY support arbitrary typed graph edges such as:

```text
part_of
used_by
electrically_connected_to
manufactured_by
assigned_to
compatible_with
```

However, physical hierarchy MUST remain uniquely interpretable.

An object MUST have at most one active **physical-parent** relationship.

Physical-parent semantic subtypes MAY include:

```text
contained_in
installed_in
mounted_in
located_in
```

All physical-parent subtypes participate in the same acyclic hierarchy and therefore in physical path computation.

For example:

```text
EV Prototype
└── Front Assembly
    └── Motor Controller
```

is a physical hierarchy.

By contrast:

```text
Motor Controller --electrically_connected_to--> Battery Pack
```

does not affect physical location.

---

## 7. Cross-Site Authority Model

The architecture MUST distinguish the following concepts.

### 7.1 Legal owner

The principal that legally or organizationally owns the asset.

```text
legal_owner = OpenLab
```

### 7.2 Home authority

The inventoryzing installation responsible for the object's canonical identity and long-term authority history.

```text
home_authority = openlab.inventoryzing
```

### 7.3 Physical custodian

The principal currently responsible for physical possession.

```text
physical_custodian = XRI
```

### 7.4 Physical site

The inventoryzing site where the object is physically present.

```text
physical_site = xri.inventoryzing
```

### 7.5 Current write authority

The inventoryzing installation currently permitted to commit canonical state mutations for the object.

```text
write_authority = openlab.inventoryzing
```

These fields MUST NOT be treated as synonymous.

A checked-out or transferred object MAY therefore have different values for all of them.

---

## 8. Single-Writer Federation Rule

For the initial federation implementation, each object MUST have exactly one canonical write authority at a time.

A remote site that is not current write authority MAY record tentative/pending local observations or commands, but MUST NOT silently treat them as canonical state.

The UI MUST distinguish canonical state from pending/tentative local state if such offline or disconnected operations exist.

Field-level multi-master federation is explicitly deferred.

---

## 9. Permanent Authority Transfer

Changing an authority-site field alone is insufficient.

Permanent write-authority transfer MUST use an explicit handoff protocol.

Each authoritative object or authority scope SHOULD carry an authority epoch:

```text
authority_site = Site X
authority_epoch = 17
```

A transfer from X to Y SHOULD behave conceptually as:

```text
1. X creates an authority-transfer offer.
2. Y authenticates and accepts.
3. X commits the transfer:
       old authority = X
       new authority = Y
       authority_epoch = 18
4. X emits a signed/verified transfer record.
5. Y activates authority epoch 18.
```

After X commits the new epoch, it MUST NOT resume writes under the previous epoch even if communication with Y later fails.

Retries MUST be idempotent.

A later RFC MAY define delegation or temporary write authority, but the MVP federation model SHOULD remain single-writer.

---

## 10. Command Idempotency Contract

Idempotency MUST be atomic with the state mutation.

Every mutating command MUST carry a globally unique `command_id` generated by the client or initiating subsystem.

The server MUST maintain an idempotency/command-receipt record conceptually containing:

```text
command_id
request_fingerprint
status
result_code
result_payload
created_at
completed_at
```

`command_id` MUST be unique.

### 10.1 Request fingerprint

The server MUST compute or validate a deterministic fingerprint of the command's semantic payload.

Reusing the same `command_id` with a different request payload MUST fail with a conflict such as:

```text
IDEMPOTENCY_KEY_REUSED_WITH_DIFFERENT_REQUEST
```

### 10.2 Atomic execution

A successful mutation MUST commit the following in one PostgreSQL transaction:

```text
command receipt
state mutation
entity/object version change
history/audit records
replication outbox events
stored command result
```

No successful mutation may exist without its corresponding idempotency record and replication event.

### 10.3 Concurrent duplicate requests

If two requests using the same `command_id` arrive concurrently, only one MUST execute the mutation.

The other MUST wait for or observe the first transaction's committed result and return the same result.

### 10.4 Lost successful response

If the server commits successfully but the response is lost, retrying the same `command_id` MUST return the original successful result.

The server MUST NOT rerun current-state checks against the now-mutated object and convert a prior success into a conflict.

---

## 11. Replication: Events, Not Replayed Commands

Replication MUST primarily transmit committed domain events, not rerun original mutation commands.

A command means:

> Please perform this action.

An event means:

> This authoritative action has already happened.

Replicas MUST therefore consume idempotent events with stable event IDs.

Commands MAY still be routed from higher-level management or corporate systems to an authoritative site, but replication of resulting state MUST occur through events/projections.

---

## 12. Safe Replication Cursor Contract

Database sequence allocation MUST NOT be interpreted as transaction commit order.

The system MUST NOT implement a replication cursor by assuming that a sequence allocated inside arbitrary concurrent domain transactions is a safe commit cursor.

### 12.1 Publication sequence

Domain transactions SHOULD insert committed outbox records with:

```text
event_uuid
payload
entity/version metadata
publication_sequence = NULL
```

A publisher operating only on committed outbox rows SHOULD assign publication ordering afterward.

The publication stream SHOULD contain:

```text
stream_id
stream_epoch
publication_sequence
event_uuid
```

`publication_sequence` is used for ordered traversal.

`event_uuid` is used for deduplication.

The two MUST NOT be conflated.

### 12.2 Replica inbox

A downstream replica MUST atomically:

```text
record event UUID in replication inbox
apply projection changes
advance replication cursor
```

inside one transaction.

Receiving the same `event_uuid` multiple times MUST be harmless.

---

## 13. Stream Epochs, Restore, and Rebuild Safety

A restored or reseeded source MUST NOT silently resume an old event stream from an earlier point.

Every replication stream SHOULD have:

```text
stream_id
stream_epoch
sequence
```

A destructive restore, reseed, or topology operation that invalidates prior cursor assumptions MUST increment or replace `stream_epoch`.

A downstream node observing an unexpected epoch MUST enter reconciliation/resnapshot behavior rather than continuing incremental replay.

---

## 14. Replica Rebuild Contract

Each replicated projection MUST define a reproducible rebuild procedure.

At minimum:

1. obtain a consistent source snapshot or snapshot manifest;
2. identify the event-stream position corresponding to that snapshot;
3. construct projection state from the snapshot;
4. replay subsequent events;
5. atomically mark the projection current;
6. retain event deduplication semantics during replay.

The implementation MUST define:

- snapshot format;
- event retention period;
- minimum replay guarantees;
- behavior when required historical events have expired;
- schema compatibility requirements;
- tombstone handling.

A regional projection MUST be considered disposable/rebuildable state rather than a canonical source of truth.

---

## 15. Type Attributes and Inheritance

Type attributes SHOULD behave as dynamic defaults, not values copied into every instance on creation.

Effective object attribute resolution SHOULD be:

```text
object override
    else
type value
    else
unset
```

Changing a type attribute therefore changes the effective value of instances that have not overridden it.

If a type definition materially changes such that old instances must preserve different historical semantics, implementations SHOULD create a new type/revision or add explicit overrides rather than mutating history ambiguously.

The same general principle applies to inherited tags.

---

## 16. Stock Quantity Representation

Fungible quantity MUST use decimal-safe storage.

PostgreSQL `NUMERIC`/`DECIMAL` or equivalent MUST be used instead of binary floating point.

API quantities SHOULD be serialized as decimal strings:

```json
{
  "quantity": "12.860",
  "unit": "m"
}
```

rather than binary floating-point JSON values where decimal fidelity matters.

Each stock type SHOULD define:

```text
canonical_inventory_unit
optional_granularity
```

Examples:

```text
M5 slot nut -> ea, granularity 1
12 AWG wire -> m, granularity 0.01
PETG filament -> g, granularity 0.1
```

Input MAY be accepted in other compatible units and converted to the canonical unit.

---

## 17. Negative Quantity Policy

Negative inventory MUST be forbidden by default.

A deployment MAY explicitly enable negative inventory for a particular type or workflow when operationally justified.

Negative stock MUST NOT occur merely because validation was omitted.

---

## 18. Stock Conservation Rules

Ordinary application APIs MUST NOT expose arbitrary direct quantity overwrite as the normal inventory operation.

Quantity changes SHOULD be expressed as semantic operations.

### 18.1 Split

```text
500 ea
-> 400 ea + 100 ea
```

The total MUST be conserved.

### 18.2 Transfer

```text
Holding A: 500
Holding B: 0
```

becomes:

```text
Holding A: 400
Holding B: 100
```

The total MUST be conserved.

### 18.3 Consumption

```text
15 cm wire
-> consumed into Rear Blinker Harness
```

Consumption is an explicit sink and MUST generate an auditable event.

### 18.4 Adjustment

```text
expected: 12.4 m
counted: 11.8 m
adjustment: -0.6 m
reason: physical inventory count
```

Adjustments MUST be explicit and auditable.

---

## 19. Depleted Holdings

A holding that reaches zero quantity SHOULD remain addressable.

Example:

```text
AWG12 holding #428731
quantity = 0
status = depleted
```

Its physical label SHOULD continue to resolve.

The holding MAY later receive compatible replenishment.

Deleting or recycling a holding merely because quantity reached zero SHOULD be avoided because physical labels and history may continue to reference it.

---

## 20. Holding Merge Rules

Two holdings MUST NOT merge merely because their item type matches.

A merge policy SHOULD consider at least:

```text
item type
legal owner
condition
canonical unit
lot/provenance identity
traceability policy
```

A type MAY declare whether provenance distinctions are significant.

Commodity wire may permit merging.

Controlled or certified material may require lot identity to remain separate.

---

## 21. Identifier Namespace Contract

Identifier namespaces MUST explicitly define semantics.

Each namespace SHOULD specify:

```text
namespace identifier
target entity kind
issuer/scope
uniqueness rule
cardinality
normalization/parser
portability
```

Examples follow.

### 21.1 inventoryzing UUID

```text
namespace: inventoryzing.uuid
target: any entity
scope: global
cardinality: one identifier -> one entity
```

### 21.2 UIUC UIN/iCard

```text
namespace: uiuc.uin
target: account/principal credential
scope: UIUC
cardinality: one identifier -> one credential binding
```

### 21.3 UPC/EAN

```text
namespace: upc
target: item type/product identity
scope: global
cardinality: product identifier, not physical-asset identity
```

### 21.4 Manufacturer serial

```text
namespace: manufacturer.serial
target: physical object
scope: manufacturer + model or defined issuer scope
cardinality: one -> one within scope
```

The resolver SHOULD distinguish results such as:

```text
ResolvedPhysicalEntity
RecognizedItemType
ResolvedPrincipal
Ambiguous
Unknown
```

A workflow requiring a unique physical asset MUST reject a result that resolves only to an item/product type.

---

## 22. Offline Handheld Command State

Offline support MUST distinguish pending local work from committed canonical work.

A local queued mutation SHOULD have states conceptually equivalent to:

```text
pending_local
sending
accepted
rejected
needs_reconciliation
```

The client MUST NOT display `pending_local` state as if it were confirmed canonical state.

---

## 23. Offline Conflict Example

If two disconnected devices both queue contradictory checkouts such as:

```text
Drill #7 -> Alp
```

and:

```text
Drill #7 -> Jane
```

only one canonical operation may succeed.

The rejected operation MUST NOT simply disappear.

The client SHOULD enter a reconciliation state explaining:

```text
reported physical handover:
Drill #7 -> Jane

canonical state:
Drill #7 -> Alp
```

A privileged user or later workflow must resolve the real-world discrepancy.

The software MUST NOT invent physical truth.

---

## 24. Offline Workflow Safety Classification

Workflow definitions SHOULD declare an offline policy.

Recommended categories:

```text
DENY
QUEUE_WITH_CONFLICT_RISK
SAFE_APPEND
```

Examples:

```text
change trusted corporate peers
    -> DENY

checkout unique asset
    -> QUEUE_WITH_CONFLICT_RISK

record scan observation
    -> SAFE_APPEND
```

A later implementation MAY introduce richer conflict policies.

---

## 25. Undo and Compensation

Undo behavior MUST depend on command state.

### 25.1 Pending locally

If a command has not been transmitted, undo MAY remove the pending local operation.

### 25.2 Accepted remotely

If a command is already canonical, undo MUST issue a compensating semantic command.

### 25.3 Unknown due to lost connectivity

If the client does not know whether a command committed, it MUST query/retry by `command_id` before deciding whether to remove or compensate it.

The client MUST NOT guess.

---

## 26. Configuration Auto-Rollback Boundaries

Automatic configuration rollback MUST be restricted to declarative runtime configuration that is designed to be reversible.

Suitable examples include:

```text
replication targets
workflow bundle selection
service-discovery configuration
regional assignment
feature flags
UI/site settings
compatible policy configuration
```

Automatic rollback MUST NOT blindly apply to:

```text
database schema migrations
destructive data transformations
inventory mutations
authority-transfer transactions
irreversible security operations
```

Database migration rollback requires separate migration discipline and backup/recovery procedures.

---

## 27. Software/Schema Compatibility

A previous application binary MAY only be automatically restored if it declares compatibility with the currently installed database schema.

Recommended deployment discipline:

```text
expand schema
deploy code compatible with old + new schema
migrate data
verify
contract obsolete schema later
```

A configuration rollback MUST report failure rather than silently starting an incompatible binary.

---

## 28. Last-Known-Good Configuration

Sites SHOULD retain:

```text
current configuration
previous known-good configuration generations
configuration schema versions
```

Rollback is conditional, not magical.

A rollback attempt MUST validate:

```text
binary compatibility
configuration schema compatibility
required credentials/secrets still available
referenced services resolvable/reachable as required
```

The management plane SHOULD report explicit states such as:

```text
ROLLBACK_AVAILABLE
ROLLBACK_SUCCEEDED
ROLLBACK_FAILED
ROLLBACK_UNAVAILABLE
```

Old credential/secret generations SHOULD be retained for a configurable grace period when feasible to improve rollback reliability.

---

## 29. Bootstrap Identity and Certificate Recovery

The durable site bootstrap identity SHOULD be minimal and distinct from renewable credentials.

Recommended durable bootstrap state:

```text
site_id
site private key
corporate root/public trust anchor
bootstrap hostname/service identity
```

Leaf certificates are renewable operational state and SHOULD NOT be the sole proof of site identity.

If a site certificate expires, the bootstrap service MAY challenge the site to prove possession of the registered site private key and issue replacement credentials.

Loss of renewable certificates alone SHOULD NOT require physical intervention if the site private key and corporate trust relationship remain intact.

---

## 30. Site Incarnation

A restored old backup MUST NOT automatically resume as a current writer under stale assumptions.

Each site SHOULD have:

```text
site_id
site_incarnation
```

Corporate management tracks the currently valid incarnation.

A restored backup presenting an old incarnation MUST NOT automatically resume federated/corporate writes.

Recovery SHOULD explicitly create a new incarnation and trigger reconciliation or resnapshot where required.

This also prevents two cloned/restored copies of one site from simultaneously believing they are the active installation.

---

# 31. MVP Requirement: Site-Local Short Identifiers

Compact site-local identifiers are REQUIRED for the MVP.

The purpose is to support micro labels where encoding a UUID, authority URI, or full URL would create impractically dense machine-readable codes.

Example:

```text
Global UUID:
0199af28-7c31-7d12-a5c8-...

Site:
OpenLab

Local short ID:
428731
```

The short ID is an alias, not canonical identity.

---

## 32. Site-Local Identifier Semantics

A local identifier MUST be interpreted as:

```text
(site_id, local_id)
```

not simply:

```text
local_id
```

Therefore these are valid simultaneously:

```text
OpenLab / 428731 -> UUID A
IPI     / 428731 -> UUID B
```

The global UUID remains the entity's stable global identity.

A transferred object MAY receive a different short ID at each site.

---

## 33. Local ID Format

The initial implementation SHOULD support compact numeric identifiers approximately 6-8 digits long.

For example:

```text
482173
```

or:

```text
00482173
```

The database MUST NOT require one fixed textual length, because future deployments may prefer alternative compact alphabets.

Examples of future-compatible formats:

```text
482173
A82K41
```

The namespace and resolver MUST therefore treat the identifier as an opaque normalized value rather than assume that it is always a specific integer width.

---

## 34. Local IDs MUST NOT Be Recycled

Once a local ID has been assigned by a site, that site SHOULD NOT assign the same identifier to an unrelated future entity.

Example:

```text
OpenLab / 428731 -> UUID A
```

should remain historically associated with UUID A even if the object is archived, deleted, depleted, or permanently transferred away.

This prevents old physical labels from silently referring to unrelated future objects.

Archived/tombstoned IDs SHOULD continue to resolve to an archived/tombstone result rather than be reused.

---

## 35. Local ID Data Model

Local IDs SHOULD use the general identifier subsystem rather than an unrelated object column.

Conceptually:

```text
Identifier
----------
entity_id
namespace
issuer_site_id
value
created_at
retired_at
```

For site-local IDs:

```text
namespace      = inventoryzing.local
issuer_site_id = <site UUID>
value          = "428731"
entity_id      = <global entity UUID>
```

The database MUST enforce uniqueness equivalent to:

```text
UNIQUE(namespace, issuer_site_id, value)
```

---

## 36. Local ID Allocation

A site's local ID allocator MUST allocate identifiers atomically.

A PostgreSQL sequence or equivalent monotonic allocator is acceptable.

Unlike replication ordering, allocation gaps and transaction commit order are irrelevant here because the allocator is used only to guarantee unique aliases.

Implementations MUST NOT infer chronology, object age, or replication ordering from local ID values.

---

## 37. Micro-Label Payload

A micro label SHOULD contain the smallest practical identifier representation while remaining distinguishable from other numeric barcodes.

A recommended MVP payload is:

```text
I428731
```

where the `I` prefix indicates an inventoryzing site-local identifier.

This avoids accidental collision with:

- UIN/iCard values;
- UPC/EAN codes;
- manufacturer numeric identifiers;
- quantities;
- arbitrary printed numbers.

The exact encoding syntax SHOULD be standardized by inventoryzing rather than independently invented by each deployment.

---

## 38. Micro-Label Resolution

At its issuing site, scanning:

```text
I428731
```

resolves conceptually as:

```text
namespace = inventoryzing.local
issuer_site = current site
value = 428731
```

which resolves to the global entity UUID.

After resolution, all business logic MUST operate on the canonical UUID/entity identity.

The short ID is only an input alias.

---

## 39. Label Portability Classification

Label templates SHOULD declare their portability.

Example:

```text
Template: Micro Local QR
portability: site_local
```

versus:

```text
Template: Full Object QR
portability: global
```

The UI SHOULD expose this distinction.

For example:

```text
Micro QR #428731

Warning:
This identifier is local to OpenLab and is not independently portable.
```

Workflow logic MAY inspect label portability when preparing transfers.

---

## 40. Global/Portable Labels

A globally portable label SHOULD contain sufficient information to identify the object outside the issuing site's local identifier namespace.

Suitable payloads MAY include:

```text
global UUID
home authority identity
resolvable inventoryzing URI/URL
signed transfer token
```

The exact portable-label payload MAY evolve independently of local short IDs.

The UUID remains the canonical object identity regardless of label representation.

---

## 41. Cross-Site Transfer of Micro-Labeled Objects

A cross-site transfer MUST NOT assume that the source site's local short ID is independently meaningful at the destination.

If an object's only physical inventoryzing label is site-local, initiating a transfer SHOULD warn the operator.

Recommended UI wording:

> This object's current label is local to OpenLab and cannot independently identify the object at another inventoryzing installation.

The workflow SHOULD offer:

```text
Print full/transfer label
```

before physical shipment.

---

## 42. Destination-Local IDs

When another site accepts the object, it MAY assign its own local short ID.

Example:

```text
Global UUID:
0199af28-...

OpenLab local ID:
428731

XRI local ID:
107284
```

The UUID MUST remain unchanged.

The receiving site MAY print its own micro label.

The source site's historic alias MAY remain associated with the object indefinitely.

---

## 43. Historical Local Alias Preservation

The original site's local ID SHOULD remain resolvable even after transfer.

Example:

```text
OpenLab / 428731
```

may continue to map to UUID A while the object is physically at XRI.

If the item later returns to OpenLab, the existing micro label can still be recognized immediately.

The local alias therefore belongs to the issuing site's historical identifier mapping, not merely to current possession.

---

## 44. Transfer-Context Resolution

A full physical label does not have to be mandatory in every controlled transfer if the destination already has authenticated transfer context.

Example:

```text
OpenLab authorizes:
UUID A
source local ID 428731
destination XRI
```

XRI receives the transfer manifest.

If XRI scans:

```text
I428731
```

while processing that specific transfer, it MAY resolve the label using the source-site/transfer context.

Without explicit context, a foreign local ID MUST NOT be guessed or globally searched until something happens to match.

The correct response to an unscoped foreign local ID is:

```text
UNKNOWN OR UNRESOLVED LOCAL IDENTIFIER
```

unless source context is known.

---

## 45. Transfer Containers and Manifests

A future workflow MAY allow many micro-labeled objects to move under one globally identified transfer container or shipment.

Example:

```text
Transfer Shipment #ABC
├── OpenLab/428731 -> UUID A
├── OpenLab/428732 -> UUID B
├── OpenLab/428739 -> UUID C
└── ...
```

The transfer container carries a globally resolvable label.

At the destination:

```text
1. scan transfer container
2. establish authenticated source/manifest context
3. scan micro labels
4. resolve each against the manifest
5. assign destination-local IDs as desired
```

This avoids forcing large temporary labels onto every micro-tagged object in bulk transfers.

This feature MAY be deferred beyond the MVP, but the identifier model MUST NOT preclude it.

---

## 46. Search and UI Behavior for Short IDs

A site's UI SHOULD allow local-ID lookup.

Entering:

```text
428731
```

MAY resolve the local ID if unambiguous in the current site context.

The object page SHOULD expose aliases separately from canonical identity.

Example:

```text
Fluke 87V #12

Global UUID:
0199af28-...

Local ID:
428731

Labels:
- OpenLab Micro QR
- Full Global QR
```

APIs SHOULD use UUIDs as canonical identifiers unless an endpoint explicitly accepts aliases.

---

## 47. MVP Acceptance Tests for Short IDs

The MVP MUST include tests demonstrating all of the following:

1. two different sites may assign the same local numeric ID to different entities;
2. one site cannot assign one active/historical local ID to unrelated entities;
3. concurrent creation cannot allocate duplicate site-local IDs;
4. archived/deleted IDs are not silently recycled;
5. a local micro tag resolves correctly at its issuing site;
6. the same tag does not resolve at another site without explicit source/transfer context;
7. a transferred object keeps its global UUID;
8. the destination may allocate a new local ID;
9. transfer initiation detects lack of an independently portable label and warns the user;
10. a full label or authenticated transfer manifest can bootstrap identification at another site;
11. a returned object's historic source-site ID still resolves;
12. business logic receives the canonical UUID after alias resolution rather than operating directly on the short ID.

---

## 48. Revised MVP Foundation

The personal MVP SHOULD include the following foundational contracts even before federation or regional replication is actively deployed:

```text
stable global UUIDs
site identity
site-local short IDs
object/entity versions
atomic command idempotency
history/events
transactional outbox
typed identifier namespaces
principals and ownership
active checkout records
decimal quantity holdings
generic relationships
single physical-parent hierarchy
safe structural advisory locking
RBAC
basic labels
micro labels
scanner resolver
portable/full labels
```

It is acceptable for some infrastructure, such as the replication outbox, to exist before any downstream consumer uses it.

---

## 49. Recommended Delivery Sequence

### Phase 1: Correct local core

Implement and validate:

- site identity;
- users/principals/RBAC;
- objects/types;
- global UUIDs;
- site-local short IDs;
- identifier resolver;
- tags and typed attributes;
- physical hierarchy;
- structural locking;
- holdings and decimal quantities;
- checkout records;
- atomic idempotency;
- history/audit;
- transactional outbox.

### Phase 2: Personal inventory MVP

Deliver:

- fast web UI;
- search;
- scanner workflows;
- local container moves;
- stock consumption/splits;
- basic asset checkout;
- SVG/image label renderer;
- micro-label printing;
- full/portable labels;
- hardware-independent print/scanner interfaces.

The personal deployment MUST be pleasant and fast enough for routine real-world use before broader infrastructure is considered successful.

### Phase 3: OpenLab workflows

Add:

- iCard identifier bindings;
- monitor PIN/session behavior;
- scanner-driven repeated checkout/check-in;
- custom workflow actions;
- deployment-specific attributes such as return-pending state;
- kiosk views;
- audit/undo/compensation behavior.

### Phase 4: Offline handhelds

Add:

- Android/rugged-scanner client support;
- local cache;
- queued command IDs;
- explicit pending/accepted/rejected states;
- conflict/reconciliation flows;
- offline policy declarations.

### Phase 5: Federation and replication

Add:

- trusted installations;
- single-writer authority;
- transfer records;
- authority epochs;
- publication streams;
- regional projections;
- snapshots/rebuilds;
- stream epochs;
- portable transfer behavior.

### Phase 6: Corporate management

Add:

- zero-touch enrollment;
- service discovery;
- management overlay;
- desired-state configuration;
- last-known-good rollback;
- certificate recovery;
- site incarnations;
- regional/corporate topology management.

---

## 50. Architectural Summary

The amended architecture preserves the original direction while clarifying the contracts most likely to cause silent corruption if left ambiguous.

The most important invariants are:

1. **Every important fact has one canonical representation.**
2. **Structural graph edits are serialized before cycle/invariant checks.**
3. **Each federated object has one canonical writer at a time.**
4. **Authority transfer is explicit and epoch-based.**
5. **Command idempotency commits atomically with state, history, and outbox records.**
6. **Replication cursors are based on safe committed publication order, not raw sequence allocation.**
7. **Replica projections are rebuildable and never canonical.**
8. **Fungible stock uses decimal-safe accounting and semantic quantity operations.**
9. **Identifier namespaces explicitly distinguish physical instances, principals, product types, and local aliases.**
10. **Offline work remains tentative until accepted by the authority and conflicts are surfaced rather than hidden.**
11. **Automatic rollback applies only to reversible compatible configuration, not arbitrary migrations or data operations.**
12. **Site identity survives ordinary credential expiry and stale-backup recovery is incarnation-aware.**
13. **Global UUIDs remain canonical, while compact site-local IDs provide practical micro-label support.**
14. **Site-local IDs are never assumed to be globally meaningful.**
15. **The system remains optimized for fast real-world workflows rather than bureaucratic interaction overhead.**

The implementation SHOULD prefer simple, strongly correct mechanisms in the MVP—such as PostgreSQL advisory locks and single-writer authority—even where more scalable mechanisms may eventually exist.

Correctness and recoverability MUST be established before multiplying distributed execution patterns.
