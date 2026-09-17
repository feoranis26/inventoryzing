# inventoryzing Architecture & Development Handoff

**Status:** Architecture handoff for implementation  
**Audience:** Local implementation agent / future contributors  
**Scope:** Core inventory model, workflows, scanning/printing, site architecture, replication, federation, offline clients, RBAC, management-plane provisioning, failure handling, and staged delivery  
**Project name:** `inventoryzing` (stylized lowercase)

---

## 0. How to read this document

This document captures the architectural decisions made during the design discussion and turns them into implementable requirements. It is intentionally opinionated about invariants and boundaries while leaving ordinary implementation details (language, specific ORM, CSS framework, etc.) flexible.

The terms **MUST**, **SHOULD**, and **MAY** are used in the RFC sense:

- **MUST**: architectural invariant or behavior that should not be changed without revisiting the design.
- **SHOULD**: strong default; a different choice is acceptable only with a clear reason.
- **MAY**: optional capability or implementation detail.

Where a choice is not yet settled, it is explicitly labeled **DEFERRED** or **OPEN QUESTION**. The implementation agent should not silently turn deferred choices into irreversible architecture.

The central theme of the project is: **model physical reality faithfully, keep ordinary workflows extremely fast, and make specialized hardware/infrastructure optional rather than foundational.**

---

# 1. Product vision

`inventoryzing` began as a personal inventory/asset-tracking system for a collection large enough to resemble a small workshop, but the architecture should scale naturally to laboratories, makerspaces, tool cribs, workshops, warehouses, research groups, and multi-site organizations.

The core problem is broader than “where is this object?” The system should be able to answer questions such as:

- Where is a physical asset right now?
- What container hierarchy leads to it?
- Which assets of a particular type exist, and where are all of them?
- Who owns an asset, who is responsible for it, and who currently has custody of it?
- What parts are installed in a prototype or assembly?
- What quantities of a fungible item remain at each location?
- What material was consumed by a build or manufacturing process?
- Which exact STL/3MF/G-code and material spool produced a 3D-printed part?
- What was moved, checked out, consumed, or transferred, by whom, and when?
- Which remote inventory installation is authoritative for a foreign object?
- How can a workshop perform a checkout in seconds rather than navigating a generic CRUD UI?
- How can a handheld keep operating when Wi-Fi is congested or temporarily unavailable?
- How can corporate management replace regional infrastructure without manually editing every site?

The project should support this breadth without turning into an ERP suite or requiring enterprise infrastructure for a home installation.

---

# 2. Core design principles

## 2.1 Physical reality first

Containers, tools, stock, prototypes, subassemblies, projects, people/principals, manufacturing jobs, and remote installations are related but not identical concepts. The schema should preserve useful distinctions while using shared primitives where they genuinely simplify the system.

## 2.2 Fast workflows beat generic UI purity

A correct data model is worthless if users bypass the system because it is slower than doing the job manually. Routine actions MUST be optimized for minimal interaction.

Example OpenLab checkout target flow:

1. Monitor scans their iCard.
2. If required, enters a short PIN.
3. Large workflow buttons appear within a few hundred milliseconds.
4. Monitor presses **Checkout item** once.
5. Monitor scans a recipient card and an item in either order.
6. The pair commits immediately.
7. Additional pairs can be scanned continuously without touching the UI.
8. A summary is shown when the session ends.
9. After a short inactivity timeout, the terminal returns to the locked initial state.

The system MUST avoid workflows that turn a two-scan physical action into ten GUI interactions.

## 2.3 Specialized hardware is an accelerator, not a dependency

A handheld barcode scanner, USB label printer, rugged Android terminal, NFC reader, or slicer plugin should improve speed and convenience. The inventory system must remain usable without that exact hardware.

The core should consume generic events such as `ScanEvent` and `PrintJob`, not vendor-specific device semantics.

## 2.4 Site-local authority and operation

A site MUST continue ordinary inventory operations if WAN, corporate, or regional infrastructure is unavailable. Corporate/regional systems are projections and management layers, not prerequisites for local checkout or storage operations.

## 2.5 Avoid accidental distributed multi-master state

A canonical object/domain has a defined authority. Regional/corporate replicas consume authoritative events and serve search/reporting views. They do not become independent writable copies of site-owned state.

## 2.6 Event replication, not database replication

Cross-layer synchronization SHOULD occur at the application/domain-event layer. PostgreSQL physical/logical replication is not the product-level synchronization model.

## 2.7 Configuration should be recoverable

Corporate-managed site configuration should be disposable/reconstructible. A tiny bootstrap identity should be sufficient for a site to restore its desired configuration automatically.

## 2.8 Modular monolith first

The site coordinator SHOULD initially be a modular monolith rather than a constellation of microservices. Clean module boundaries matter; independent deployment does not yet.

---

# 3. Explicit non-goals for the first implementation

The first release does **not** need to become:

- A full ERP/accounting system.
- A procurement platform.
- A multi-master globally consistent distributed database.
- A general-purpose BPM engine rivaling enterprise workflow products.
- A full MRP/PLM suite.
- A mandatory cloud service.
- A mandatory native phone application.
- A mandatory Consul/Vault/Kubernetes deployment.
- A system that assigns UUIDs to every individual fungible screw or centimeter of wire.

The architecture should leave extension paths for some of these domains without forcing them into the MVP.

---

# 4. Terminology and conceptual model

## 4.1 Object Type

A conceptual type of thing, e.g.:

- M5 T-slot nut
- Fluke 87V multimeter
- 12 AWG red stranded copper wire
- Akro-Mils organizer
- EV motor mount revision 7

A type may carry default metadata, tags, properties, manufacturer data, part numbers, unit preferences, and default stock policies.

## 4.2 Object

A concrete physical instance or a concrete stock holding/lot. Every object has a stable UUID.

Examples:

- Fluke 87V #12 (serialized asset)
- Organizer D
- EV Prototype
- A holding of 500 M5 T-slot nuts in Organizer D
- A 12.86 m spool/remaining holding of 12 AWG wire

Objects MAY themselves contain or relate to other objects.

## 4.3 Entity

Use this as a broad conceptual term in code/docs for things that have identity in inventoryzing. Do not necessarily implement one giant universal `entities` table unless it materially helps. Important entities include objects, object types, principals, users/accounts, sites/installations, workflows, manufacturing jobs, etc.

## 4.4 Principal

A thing that can own, be responsible for, possess, or receive custody of something.

Examples:

- Person
- Team
- Department
- Project
- Inventoryzing installation/site
- External organization

A principal is **not** the same as a login account.

## 4.5 User Account

An authenticated operator identity that may invoke actions according to roles and permissions. A user account MAY be linked to a person principal.

## 4.6 Identifier

A value in a namespace that can resolve to an object, principal, account credential, site, etc.

Examples:

- `inventoryzing.uuid`
- `inventoryzing.short_id`
- `uiuc.icard.uin`
- `manufacturer.serial`
- `upc`
- `mac`
- remote inventoryzing URI

## 4.7 Relationship

A typed connection between entities/objects. Examples:

- `contained_in`
- `installed_in`
- `part_of`
- `located_at`
- `checked_out_to`
- `produced_by`
- `uses_material`

Relationships are first-class when their semantics matter. They are not merely tags.

## 4.8 Tag

A classification node, potentially part of an inheritance DAG. Tags identify categories/concepts, e.g. `Hardware`, `M5`, `Slot Nut`, `Measurement Equipment`, `Needs Repair`.

## 4.9 Typed Attribute

A key/value property with a declared or inferable type, e.g.:

- `gauge = 12 AWG`
- `voltage_rating = 600 V`
- `quantity = 12.86 m`
- `color = red`
- `printer = <entity-reference>`
- `openlab.checkout_state = return_pending`

## 4.10 Holding

A quantity-bearing object/record representing fungible stock at a particular context/location. A holding may be split/merged without assigning IDs to individual pieces.

## 4.11 Checkout

A first-class custody/loan relationship/transaction indicating that an item is checked out to a principal. Organization-specific workflow state such as `return_pending` is **not** a mandatory first-class core checkout state; it may be implemented using typed attributes/tags plus workflow logic.

## 4.12 Site / Installation

An inventoryzing coordinator and its local authoritative dataset. A site has a cryptographic machine identity distinct from its network address.

---

# 5. High-level deployment architecture

```text
                           CORPORATE / GLOBAL

                 +---------------------------+
                 | Management / Directory    |
                 | Regional projections      |
                 | Corporate Web/API         |
                 +-------------^-------------+
                               |
                     events up / commands down
                               |
                 +-------------+-------------+
                 |                           |
           REGIONAL NODE A              REGIONAL NODE B
          replicated projection        replicated projection
                 ^                           ^
                 |                           |
      application-level event replication   |
                 |                           |
          +------+-------+            +------+-------+
          | SITE COORD.  |            | SITE COORD.  |
          | PostgreSQL   |            | PostgreSQL   |
          | Web/API      |            | Web/API      |
          +------+-------+            +--------------+
                 |
       +---------+----------+
       |                    |
 Host Device Agent     Android Handhelds
 scanners/printers     PWA/native client
 kiosk integration     offline queue/cache
```

The architecture has four broad layers:

1. **Site coordinator**: authoritative operational state for that site/domain.
2. **Client/device layer**: web, kiosk, host agent, handhelds, integrations.
3. **Regional replication layer**: searchable projection/cache and event archive.
4. **Corporate management layer**: desired configuration, directory, trust, fleet operations, global reporting, and command routing.

---

# 6. Site coordinator

The site coordinator is the primary server-side product component.

## 6.1 Deployment

Recommended baseline deployment:

```text
inventoryzing-coordinator container
PostgreSQL container
optional web-ui container
optional reverse proxy
host-native inventoryzing-agent
```

The coordinator MUST NOT require direct USB access.

## 6.2 Coordinator responsibilities

The coordinator should own modules for:

- Objects and object types
- Relationships / containment
- Tags and tag inheritance
- Typed attributes
- Quantity/units and holdings
- Principals and ownership/custody
- Users, roles, permissions, credentials
- Checkout core semantics
- Actions/workflows/scripts
- Identifier resolution
- Labels and rendering
- Attachments metadata/blob references
- Domain history/audit events
- Federation/trusted sites
- Replication outbox/feed
- Management agent/client
- Search/indexing
- API and realtime notifications

## 6.3 Modular monolith guidance

Keep these as internal modules with explicit interfaces. Do not prematurely deploy each module as a separate service. A future split should be possible, but ordinary local installations should remain easy to deploy and debug.

---

# 7. PostgreSQL role and database boundaries

PostgreSQL is the authoritative local transactional store.

It SHOULD store:

- Core normalized metadata
- Object/type identity
- Relationships
- Tags/attribute definitions and values
- User/RBAC state
- Checkout relationships/transactions
- Quantity state
- Workflow definitions/configuration
- Audit/history records
- Replication outbox/inbox bookkeeping
- Desired/applied local configuration metadata
- Attachment metadata and content hashes

Large attachment contents SHOULD generally live outside ordinary PostgreSQL table rows (filesystem, S3-compatible object store, etc.).

---

# 8. Write/concurrency invariants

This is a critical requirement.

## 8.1 No stale aggregate writeback

Application code MUST NOT implement bulk operations by reading a collection of objects into memory, mutating that collection, and later writing all snapshots back. This creates lost-update races.

The server may read arbitrarily many objects for display/search/planning. The restriction concerns **write paths**.

## 8.2 Object-scoped read/modify/write locking

For a normal object mutation:

1. Begin DB transaction.
2. Acquire an exclusive row/object lock for the object being modified (`SELECT ... FOR UPDATE` or equivalent).
3. Read the authoritative current state required for that mutation.
4. Validate preconditions/permissions.
5. Apply only the requested patch/delta.
6. Increment object/version metadata.
7. Append history/domain event and replication outbox record in the same transaction.
8. Commit.
9. Release lock immediately.

Locks MUST NOT be held while waiting for a human to scan, click, confirm, argue, walk to a shelf, etc.

## 8.3 Bulk operations

A bulk UI action should be decomposed into object-scoped mutations. Failure of one item should not silently overwrite unrelated concurrent changes.

The server MUST NOT commit stale snapshots merely because they were part of a previously generated bulk draft.

## 8.4 Multi-object invariants

Some actions (e.g. moving quantity from one holding to another, split/merge) logically touch multiple rows/objects. These may use a single database transaction **provided application code does not rely on stale in-memory aggregate snapshots**.

Preferred patterns:

- Atomic SQL deltas/conditional updates.
- Deterministic lock ordering if more than one row must be locked.
- Version predicates (`WHERE version = expected_version`) where appropriate.
- Idempotent command IDs.

Do not sacrifice correctness merely to obey a simplistic “one SQL row per transaction” interpretation. The true invariant is: **no stale multi-object read-modify-write snapshot overwrite.**

## 8.5 Optimistic versioning

Every mutable authoritative object SHOULD carry a monotonically increasing version/revision. External/offline commands MAY include `expected_version` for conflict detection.

---

# 9. Core data model

This is an illustrative relational model, not a mandatory exact schema.

## 9.1 Object types

```text
object_types
------------
id UUID PK
name
canonical_name/search_name
description
manufacturer_principal_id nullable
part_number nullable
preferred_quantity_unit_id nullable
created_at
updated_at
version
archived_at nullable
```

## 9.2 Objects

```text
objects
-------
id UUID PK
object_type_id nullable FK
name_override nullable
status (active/archived/etc.)
owner_principal_id nullable
assigned_principal_id nullable
custodian_principal_id nullable
created_at
updated_at
version
```

`owner_principal_id`, `assigned_principal_id`, and `custodian_principal_id` are deliberately distinct concepts.

## 9.3 Relationships

```text
relationships
-------------
id UUID PK
subject_entity_id
predicate
object_entity_id
valid_from
valid_until nullable
metadata JSONB or normalized metadata table
created_by
created_at
version
```

Important predicates should have known semantics and validators. Do **not** turn the whole product into an untyped RDF store.

Examples:

- `contained_in` must not create containment cycles.
- `installed_in` may affect availability.
- `produced_by` does not affect physical location.
- `part_of` represents assembly structure.
- `located_at` can express a non-container physical location.

A deployment MAY define additional namespaced predicates.

## 9.4 Principals

```text
principals
----------
id UUID PK
type (person/team/org/project/site/etc.)
display_name
external_ref nullable
metadata
created_at
archived_at nullable
```

## 9.5 User accounts

```text
user_accounts
-------------
id UUID PK
principal_id nullable
username / external identity
status
created_at
last_login_at
```

Do not store possession as a user-account relation; possession/custody belongs to a principal.

---

# 10. Ownership, assignment, custody, location, and checkout

These concepts MUST remain separate.

Example:

```text
Asset: Fluke 87V #12
Owner: OpenLab
Assigned principal: Electronics Team
Current custodian: Alp
Physical location: Alp's possession / external
Checked out to: Alp
```

When the item is placed on the return desk, ownership and assignment do not change. Depending on local workflow, custody/location may change while the checkout remains active until a monitor verifies the return.

## 10.1 Ownership

Who legally/organizationally owns the item. Usually stable and not equivalent to current physical possession.

## 10.2 Assignment

Who is responsible for or primarily associated with the item. Assignment alone does not imply checkout.

## 10.3 Custody

Who/what currently has operational possession/responsibility.

## 10.4 Location

Where the item physically resides. May be represented by containment, location relationships, or both depending on deployment.

## 10.5 Checked-out-to

A first-class loan/custody concept because it is broadly useful across deployments. It should reference a principal and generate auditable transaction history.

Organization-specific states such as:

- return pending
- awaiting inspection
- ready for pickup
- repair approval pending

should normally be implemented as namespaced typed attributes/tags controlled by workflows.

---

# 11. Fungible inventory and quantities

Not every physical unit needs a UUID.

## 11.1 Holdings

A holding represents a quantity of an item type in a location/context.

Example:

```text
Type: M5 T-slot Nut
Holding A: 500 ea -> Organizer D
Holding B: 80 ea  -> Robotics Toolbox
Holding C: 120 ea -> Storage Unit / Shelf 4
```

Searching the type should aggregate and show all holdings and their paths.

## 11.2 Quantity type

Quantity MUST NOT be restricted to integers.

Represent quantity as:

```text
value: decimal
unit: unit_id
```

Examples:

- `500 ea`
- `12.86 m`
- `482 g`
- `1.25 L`
- `15 cm` display, canonicalized internally if desired

Use a decimal/numeric type rather than IEEE floating point for inventory balances.

## 11.3 Units

Implement or integrate a dimensional unit registry with canonical conversion for compatible units.

Examples:

- length: mm/cm/m/ft
- mass: g/kg
- count: ea
- volume: mL/L

The UI can preserve/display the user's preferred unit while storing canonical normalized values if useful.

## 11.4 Consumption precision

The system should permit approximate consumption. A user should not be forced to measure every cheap piece of wire precisely.

Possible future metadata:

```text
quantity = 0.2 m
accuracy = approximate
```

or workflow presets such as “short piece.” This is optional for MVP.

## 11.5 Split/merge

A holding can be split into multiple holdings or merged when compatible. History must preserve the operation.

---

# 12. Tags and typed attributes

## 12.1 Tag inheritance is a DAG

Tags may inherit from multiple parents.

Example:

```text
M5 Slot Nut
  -> Slot Nut
      -> Nut
      -> T-slot Hardware
  -> M5
      -> Metric Hardware
```

Applying `M5 Slot Nut` implies ancestor tags for search/classification.

Cycles MUST be rejected.

## 12.2 Tags can apply to types and objects

Type-level tags describe common classification; object-level tags describe instance-specific facts.

Example:

```text
Type tags:
  Oscilloscope
  Measurement Equipment
  Rigol

Object tags:
  EUV Lab
  Needs Repair
```

## 12.3 Typed attributes

Support at least:

- string
- integer
- decimal
- boolean
- datetime
- enum
- entity reference
- quantity/unit-bearing number
- URL
- file/blob reference

Namespaced attribute keys are strongly recommended:

```text
openlab.checkout_state
manufacturing.material
calibration.due_at
```

## 12.4 Admin power vs workflow constraints

Direct privileged administrators may edit tags/attributes freely. Normal users should generally interact through actions/workflows that enforce organizational rules.

Do not attempt to make generic attributes themselves enforce every organization-specific FSM.

---

# 13. History, transactions, and audit

Do not make the current object state the only source of historical truth.

Maintain append-oriented events/history for actions such as:

- create/archive
- move
- containment change
- checkout/checkin
- custody change
- quantity adjust
- consume
- split/merge
- install/remove
- manufacture
- transfer
- tag/attribute change
- trust change
- role/permission change
- configuration rollout

The system does **not** need to be fully event-sourced; current state can remain in normalized tables. History/events should be sufficient for auditing and replication.

Administrative audit records SHOULD be append-only from the application perspective.

---

# 14. Checkout model and organization-specific workflow state

The core checkout record should remain simple and universal.

Illustrative record:

```text
checkout
--------
id
object_id
borrower_principal_id
owner_principal_id
checked_out_at
checked_out_by_user
closed_at nullable
closed_by_user nullable
metadata
```

Do not hardcode OpenLab-specific states such as `RETURN_PENDING` and `INSPECTION` into the universal checkout schema.

OpenLab may instead use:

```text
openlab.checkout_state = "return_pending"
```

with workflow/script logic controlling legal transitions.

The first-class invariant is **who the item is checked out to**. The process around return/inspection is deployment-specific.

---

# 15. Actions, workflows, and scripts

This subsystem is central to making inventoryzing adaptable without forcing every deployment to write a custom frontend.

## 15.1 Action concept

An action is a user-visible or programmatic capability such as:

- Checkout item
- Check in item
- Return item
- Move items
- Consume stock
- Transfer to another site
- Print label

An action can declare:

- label/icon
- invocation permission
- visibility condition
- typed inputs
- steps
- validations
- effects/core API calls
- success behavior
- timeout behavior
- elevated execution capabilities

## 15.2 Workflow vs core state

Workflows may implement organization-specific FSMs using attributes/tags. The workflow is responsible for transition validity.

## 15.3 Script privilege separation

Distinguish:

1. User permissions
2. Permission to invoke an action/script
3. Capabilities granted to that workflow/script

Example:

```text
User Alp:
  may invoke: openlab.return_item
  may NOT directly edit arbitrary tags

Workflow openlab.return_item:
  may read checkout
  may write openlab.checkout_state
  may emit audit event
```

The workflow does not automatically inherit unlimited administrator authority.

## 15.4 Script API only

Scripts/automations MUST mutate state through supported inventoryzing APIs/action primitives, not by direct SQL.

This preserves:

- validation
- authorization
- versioning
- history
- replication outbox
- invariants

## 15.5 Declarative first, scripting escape hatch second

Common workflows SHOULD be expressible declaratively. Advanced scripts (language/runtime TBD) MAY extend behavior.

The system should ship templates for common patterns such as:

- Simple checkout
- Staff-approved checkout
- Self-service checkout
- Desk return + inspection
- Consumable issue
- Tool crib
- Equipment reservation
- Inter-site transfer

---

# 16. Scanner-driven workflows

Scanning should be a first-class workflow input type, not a text-field hack.

## 16.1 Generic scan event

```text
ScanEvent {
  payload
  source_id
  source_type
  timestamp
  optional symbology
}
```

The workflow engine consumes resolved entities, not raw vendor device data.

## 16.2 Typed scan slots

A workflow may declare requirements such as:

```text
Workflow: OpenLab Checkout
requires:
  1 Asset
  1 Principal
order: any
repeat: true
```

The engine accumulates scans until a valid tuple exists, then invokes the checkout command and clears the tuple.

## 16.3 Order-independent pairing

For OpenLab checkout:

- Scan recipient then item, OR
- Scan item then recipient.

Input type/identifier namespace determines which is which.

## 16.4 Continuous high-throughput mode

After entering checkout mode, the operator should be able to scan repeated pairs without touching the GUI.

Potential optional modes:

- Pair mode: principal + item, reset after each transaction.
- Sticky principal mode: scan principal once, then multiple assets until another principal is scanned.

Pair mode is safer as a default; sticky mode may be useful in high-throughput environments.

## 16.5 Immediate feedback

Each committed transaction should produce lightweight immediate feedback:

```text
✓ Fluke 87V #12 -> Alp
```

with optional beep/sound.

Do not require confirmation after every successful normal scan unless the deployment explicitly configures it.

## 16.6 Session summary and correction

At the end of a workflow session, display a grouped summary and provide at least:

- Undo last
- Correct/reverse
- Done

Error correction must be easy without slowing the happy path.

---

# 17. Identifier and credential resolution

## 17.1 Namespaced identifiers

Model identifiers as namespace + value, not as globally ambiguous strings.

Illustrative schema:

```text
identifiers
-----------
id
namespace
value_normalized
entity_kind
entity_id
active
metadata
UNIQUE(namespace, value_normalized)
```

## 17.2 Input formats

Configurable parsers map raw scanner input to candidate namespaces/types.

Examples:

```text
inventoryzing object QR -> inventoryzing.uuid
UIUC numeric iCard     -> uiuc.icard.uin
UPC                     -> upc
manufacturer barcode    -> manufacturer.serial or custom namespace
```

If a raw payload can map to multiple known records, report ambiguity rather than guessing.

## 17.3 Unknown identifiers

Scanning an unknown iCard/UIN MUST NOT auto-create a user account by default.

Privileged onboarding flow:

1. Create/select user account/principal.
2. Choose “Associate credential.”
3. Scan credential.
4. Confirm detected namespace/value.
5. Store binding.

## 17.4 Credential strength

A barcode can identify someone without being a strong authenticator.

Credential/auth policy should allow levels such as:

- identification only
- weak/single-factor
- stronger multi-factor

OpenLab example:

- General user: iCard identification may be enough for unprivileged views/actions.
- Monitor: iCard + PIN.
- Supervisor/admin: stronger login/SSO/WebAuthn as deployment chooses.

---

# 18. User accounts, roles, permissions, and service principals

## 18.1 RBAC model

Permissions are atomic capabilities. Roles are bundles of permissions. Code MUST NOT be littered with checks such as `role == "monitor"`.

Illustrative permissions:

```text
item.read
item.create
item.edit
item.archive
item.move
item.checkin.self
item.checkin.any
item.checkout.self
item.checkout.other
label.print
label.template.manage
transfer.request
transfer.authorize
peer.view
peer.manage
system.config.view
system.config.edit
role.manage
role.assign
user.manage
audit.view
workflow.invoke.<name>
```

## 18.2 Example OpenLab roles

Regular user:

- identify themselves
- view relevant own loans
- request return / limited workflow actions
- possibly check in self according to policy

Monitor:

- check items in/out
- create/manage ordinary inventory
- print labels
- run monitor workflows

Supervisor:

- authorize transfers
- manage trusted installations (or this may be moved to a separate system-admin role)
- manage configuration/policies
- manage roles/users
- view broader audit data

## 18.3 Role inheritance

Role inheritance MAY be supported, with cycle prevention. Keep it understandable; do not build an incomprehensible role graph.

## 18.4 Scoped permissions

Permissions SHOULD eventually support resource/location scope.

Example:

```text
item.edit @ OpenLab/Electronics
```

may apply recursively to objects within that branch.

## 18.5 Service/device principals

Device agents, kiosks, automations, and integrations should receive narrowly scoped service identities rather than using an all-powerful admin credential.

---

# 19. Label architecture

Label generation must be device-independent.

## 19.1 Pipeline

```text
Object/data
  -> Label Template
  -> Device-independent rendered label
  -> Output backend / print profile
```

## 19.2 Canonical render format

SVG is the preferred canonical generated representation because it supports:

- exact physical dimensions
- vector QR/barcodes
- crisp text
- browser preview
- conversion to PNG/PDF
- rasterization at printer-specific DPI

## 19.3 Label template

A template defines physical dimensions and content/layout, not a specific printer model.

Example:

```text
Template: Compact Object Label
Size: 38 x 25 mm
Contents:
  object name
  short ID
  QR/DataMatrix
  optional category
```

## 19.4 Output options

The user should be able to:

- one-click print to a configured default printer
- preview
- download SVG
- download PNG at chosen DPI
- download PDF
- batch print
- choose another printer/profile

## 19.5 Print profiles/backends

Profiles adapt the same rendered artifact to:

- Brother/Ql-style printers
- Zebra/ZPL
- OS print queue/CUPS
- sheet labels/PDF imposition
- future vendor adapters

## 19.6 Machine-readable payload

Prefer stable opaque identity in the QR/barcode. Do not encode mutable location/quantity state.

Large labels MAY encode a resolvable URL/URI to the object's home inventoryzing authority. Small labels MAY contain only a compact UUID/short ID.

The URL is a discovery convenience, not the object identity itself.

---

# 20. Host device agent

Create a host-native process, tentatively `inventoryzing-agent`, for hardware integration.

## 20.1 Why host-native

USB passthrough and printer/scanner integration through containers is an unnecessary operational burden. The device agent should run on the host and expose a clean local API.

## 20.2 Responsibilities

Potential adapters:

```text
scanners:
  HID keyboard wedge
  serial
  vendor SDK

printers:
  OS print queue
  CUPS
  raw USB
  Brother
  ZPL

kiosk/local integration:
  scan delivery
  beeper/sound
  local status
```

## 20.3 Local UI integration

The kiosk UI and device agent should be conceptually separate. The agent may expose a localhost WebSocket/HTTP endpoint. A browser-based kiosk can then use hardware capabilities when the agent is present without making the whole UI native.

## 20.4 Security

The local agent should authenticate to the coordinator with a narrow device/service identity. Its localhost API should not be an unauthenticated arbitrary command surface.

---

# 21. Web UI and custom views

## 21.1 Stateless web tier

The main web UI should be a stateless client of the coordinator API. It should not contain authoritative inventory state.

Possible deployments:

- coordinator serves bundled static UI for small installs
- separate web container
- CDN/static hosting
- horizontally scaled corporate web frontends

## 21.2 Data views

Saved/custom views can define filters, columns, sorting, grouping, and actions.

Examples:

- My checked-out items
- Items awaiting return
- Low stock
- Items outside site
- OpenLab Electronics inventory

## 21.3 Workflow views

A workflow view is a purpose-built screen assembled from generic action/workflow primitives.

Example OpenLab monitor desk:

```text
[ CHECKOUT ] [ CHECK IN ] [ RETURN QUEUE ] [ FIND ITEM ]
```

This avoids forcing ordinary users through the full generic inventory browser.

## 21.4 Performance requirement

Kiosk/workflow UIs should remain loaded and transition locally where possible. Do not trigger full page reloads for every scan state.

After a credential scan, the next UI state should appear in perceptually immediate time (target: tens to low hundreds of milliseconds on a healthy local network).

---

# 22. Rugged Android handhelds

Rugged Android barcode terminals are a first-class client category.

## 22.1 Initial compatibility

A browser/PWA can support devices configured as keyboard-wedge scanners with minimal special code.

## 22.2 Native thin client

A later native Android client is justified for:

- vendor scanner APIs/intents
- offline queue/cache
- reliable foreground scanning
- device management hooks
- better kiosk experience

The Android client should remain thin and use the standard coordinator API.

## 22.3 Scan provider abstraction

```text
ScanProvider
  Camera
  Keyboard wedge
  Android intent
  Zebra adapter
  Honeywell adapter
  Datalogic adapter
  future vendor adapters
```

All produce the same logical `ScanEvent`.

---

# 23. Offline handheld behavior

Offline support is specifically valuable in congested RF environments.

## 23.1 Cached data

A handheld MAY cache:

- workflow definitions
- recently accessed objects
- identifier mappings
- limited user/session data
- required lookup metadata

## 23.2 Queued commands

Offline-capable mutations should be queued with:

```text
command_id UUID
command_type
payload
actor/session identity
client timestamp
expected_object_version optional
```

## 23.3 Idempotency

Every externally submitted mutation MUST support idempotency. Replaying a command after uncertain network failure must not duplicate the action.

## 23.4 Conflict handling

When the device reconnects, the server may:

- accept the command
- reject it due to version/precondition conflict
- request user resolution

Do not attempt fully arbitrary disconnected editing in the MVP. Focus on well-defined scanner workflows.

## 23.5 Offline trust/session policy

DEFERRED: exact rules for offline user authentication and how long cached credentials remain valid. Keep the architecture capable of policy-based configuration.

---

# 24. 3D printing and manufacturing provenance

This is a valuable domain built naturally on the core model.

## 24.1 Manufacturing job

Represent a print/manufacturing job with references to:

- printer/machine
- start/end time
- result/status
- source model (STL/etc.)
- slicer project (e.g. 3MF)
- generated G-code
- material/spool
- estimated/actual material consumed
- operator
- settings/metadata

## 24.2 Produced part

A physical printed part may be an object linked via:

```text
Printed Part #742 --produced_by--> Print Job #195
```

It can later be installed into an assembly/prototype.

## 24.3 Material consumption

The job may decrement a filament holding/spool automatically when completion is confirmed.

## 24.4 Slicer integration

A slicer extension/plugin may POST print-job details automatically. The API should be designed so this integration does not require direct database access.

## 24.5 Content addressing

Store SHA-256 or equivalent content hashes for model/slicer/G-code files. Identical blobs should deduplicate naturally in capable storage backends.

---

# 25. Stock policies and alerts

Item types/holdings may define policies such as:

```text
preferred_unit = m
warning_threshold = 5 m
critical_threshold = 2 m
target_stock = 25 m
```

Stock views should distinguish:

- available quantity
- reserved/allocated quantity if implemented
- installed/consumed historical quantity

Low-stock conditions can emit events for automation/notifications.

---

# 26. Federation and trusted inventoryzing installations

Cross-system asset transfer is a core future capability and should influence identity/authority design now.

## 26.1 Site identity

Each installation has a cryptographic identity (public/private keypair and certificate/trust representation). Trust another installation by identity/fingerprint, not IP address.

## 26.2 Home authority

An object may have a home/owner authority distinct from its current custody location.

Example:

```text
Object UUID: ...
Home authority: OpenLab
Owner: OpenLab
Current custodian: IPI
Current site: IPI
```

## 26.3 Transfer record

Cross-site transfer should be first-class and auditable:

```text
transfer
--------
id
object_id
source_site
destination_site
status
authorized_at
accepted_at
returned_at
metadata
```

## 26.4 QR/URI discovery

Large QR codes may encode an authority URL/URI. Tiny codes may contain only the object UUID/compact ID.

If authority is not encoded, a receiving site may query configured trusted peers: “Do you know this UUID, and is it authorized for transfer to me?”

Use a trusted peer list first; broad LAN broadcast discovery can be a later plugin.

## 26.5 Network failure

A destination may record a pending foreign check-in and reconcile when connectivity returns.

## 26.6 Avoid IP-based trust

Authorization targets should reference cryptographic site identity, not addresses such as `10.0.0.53`.

---

# 27. Replication architecture

## 27.1 Replicate domain events, not PostgreSQL state

A site command is a request to perform work. Replication should distribute the committed result as an event.

Example command:

```text
CheckoutItem(object=Drill14, borrower=Alp)
```

Resulting event:

```text
ItemCheckedOut {
  event_id
  source_site
  object_id
  object_version
  borrower_principal_id
  actor_id
  timestamp
}
```

## 27.2 Why events, not commands

Commands can have side effects if replayed. Events describe facts that already occurred and can be deduplicated by `event_id`.

## 27.3 Transactional outbox

Every authoritative mutation should append its replication event/outbox record in the same PostgreSQL transaction as the state mutation.

```text
BEGIN
  lock object
  validate + mutate
  increment version
  append history
  insert replication_outbox event
COMMIT
```

A background worker ships outbox events and marks delivery progress.

## 27.4 Inbox/deduplication

Replication consumers maintain an inbox/received-event table keyed by source/event ID. Duplicate delivery is safe.

Delivery semantics can therefore be at-least-once while processing remains effectively exactly-once per event ID.

## 27.5 Ordering

Events SHOULD include object version and source sequence metadata. Consumers should detect gaps/out-of-order delivery rather than silently applying impossible state.

---

# 28. Regional replication nodes

A regional node maintains a **projection/cache**, not a second writable canonical database.

Potential regional data:

- object projections
- principal projections
- current quantity projections
- checkout projections
- event archive
- search indexes
- source-site metadata
- attachment metadata/cache

Each projected record should retain:

```text
source_site
source_uuid
source_version
last_event_id
last_verified_at
```

Corporate search can then answer global questions without live-querying every site.

---

# 29. Corporate commands and write routing

Corporate/regional UIs MUST NOT directly mutate projected site state as if it were canonical.

Preferred flow:

```text
Corporate UI
   -> Regional/Corporate command router
   -> Authoritative site coordinator
   -> site validates + commits
   -> authoritative event replicates upward
   -> projection updates
```

If the site is offline, the management layer may queue appropriate commands or report that the operation cannot currently be committed, depending on action semantics.

Do not silently create multi-master state.

---

# 30. Replication verification and repair

Incremental event replication should be periodically verified against authoritative site state.

## 30.1 State manifest

Sites may expose manifests such as:

```text
object UUID
version
content/state hash
```

Regional nodes compare these against local projections.

## 30.2 Merkle trees

A Merkle-tree-based reconciliation mechanism is a strong future optimization for large datasets, but is not required for the MVP.

## 30.3 Repair behavior

On mismatch:

- fetch missing events if possible
- fetch authoritative object snapshot if needed
- repair projection
- emit replication-health alert

The replica must never silently “win” over the authoritative site merely because its data differs.

---

# 31. Corporate management plane

Corporate management needs to configure and operate sites without relying on manually entered IP addresses or emergency SSH for routine recovery.

Separate three concerns:

1. Discovery/name resolution
2. Secure enrollment/machine identity
3. Desired configuration distribution

Do not force a single product to solve all three.

---

# 32. Zero-touch enrollment

A fresh corporate-managed site should require minimal bootstrap information.

## 32.1 Bootstrap information

Ideally only:

```text
bootstrap hostname/domain
corporate root-of-trust public key/fingerprint
```

A one-time enrollment token may be supplied during installation.

## 32.2 Local key generation

The site MUST generate its private key locally. Do not distribute long-lived private site keys from corporate.

## 32.3 Enrollment flow

```text
Fresh install
  -> generate Site UUID + keypair
  -> connect to stable bootstrap endpoint
  -> present one-time enrollment token + public key
  -> corporate validates token
  -> corporate assigns site/region/policy
  -> site receives certificate/trust chain + desired configuration
  -> enrollment token is invalidated
```

## 32.4 Site identity vs network identity

The site is identified cryptographically. IP address is merely a transport detail.

---

# 33. Service discovery and network addressing

## 33.1 Do not configure raw IPs as canonical endpoints

Configuration should reference logical services/regions, e.g.:

```text
primary_replication = region:illinois
secondary_replication = region:midwest
```

not:

```text
primary_replication = 10.52.13.87
```

## 33.2 Overlay/VPN network

Corporate deployments will likely use an overlay VPN/WireGuard-derived network or equivalent. inventoryzing should not depend on a specific VPN implementation, but should work cleanly over stable internal DNS/service names.

## 33.3 DNS first

Ordinary internal DNS (potentially SRV records) is sufficient for the initial service-discovery model.

Example:

```text
_iz-repl._tcp.illinois.inventoryzing.internal
```

## 33.4 Consul optional

Consul or another discovery system can be integrated later if scale/dynamic topology justifies it. It should not be a mandatory dependency for small installations.

---

# 34. Desired-state configuration and auto-rollback

This is a key reliability requirement.

## 34.1 Configuration generations

Corporate distributes signed desired-state documents with monotonically increasing generation/version numbers.

Example conceptual document:

```yaml
site: openlab
generation: 193
replication:
  primary: region:illinois
  secondary: region:midwest
policy_bundle: 71
software_channel: stable
```

## 34.2 Apply sequence

When a site receives a new generation:

1. Verify corporate signature/trust.
2. Validate schema.
3. Resolve referenced services.
4. Validate policy/config semantics.
5. Stage configuration.
6. Test critical connectivity where practical.
7. Activate.
8. Perform health checks.
9. Report applied generation and status.

## 34.3 Last-known-good rollback

Maintain:

```text
desired generation
currently applied generation
last-known-good generation
```

If the new generation fails validation/health checks, automatically roll back to last-known-good and report failure upstream.

## 34.4 Protect the management path

Corporate-distributed configuration MUST NOT be able to permanently remove the only path used to fetch corrected configuration.

The bootstrap/recovery identity path is special and protected.

---

# 35. Bootstrap recovery invariant

Split site state into two categories.

## 35.1 Precious bootstrap identity

Small, durable, carefully backed up:

- site UUID
- site private key
- corporate trust root
- stable bootstrap hostname/domain

## 35.2 Disposable managed configuration

Reconstructible from corporate:

- regional assignment
- replication upstreams
- policy bundles
- workflow bundles
- UI configuration
- most management settings

**Invariant:** if managed configuration disappears but bootstrap identity survives, the site should be able to reconstruct corporate-managed state automatically.

If the private key/bootstrap identity is lost, the correct recovery path is explicit re-enrollment.

---

# 36. Secrets and PKI

## 36.1 Minimize long-lived shared secrets

Prefer machine identity and mTLS over distributing many static passwords/API keys.

## 36.2 Certificate issuance

Use a mature CA/PKI implementation rather than inventing certificate infrastructure. Exact product is deployment-specific.

## 36.3 Secret provider abstraction

If applications need secrets, support a provider interface:

```text
SecretProvider
  local encrypted store
  Vault
  Infisical
  cloud KMS/secret manager
  future backends
```

Small installations must not require enterprise secret-management infrastructure.

## 36.4 SSH

SSH/VPN access is break-glass management, not the normal mechanism for repairing ordinary configuration mistakes.

---

# 37. Failure-mode requirements

The system should be designed explicitly around failure.

## 37.1 Corporate unavailable

Site continues:

- local checkout/checkin
- movement
- local search
- local authentication/workflows
- printing/scanning

Replication events queue locally.

## 37.2 Regional node unavailable

Use secondary target if configured. Otherwise queue events until recovery.

## 37.3 DNS/discovery temporarily unavailable

Use reasonable cached endpoint information for a bounded period where safe; never erase pending work.

## 37.4 New corporate config invalid

Reject before activation; remain on last-known-good.

## 37.5 Config valid syntactically but breaks connectivity

Health check fails; auto-rollback.

## 37.6 Managed config wiped

Recover from corporate using protected bootstrap identity.

## 37.7 Entire site storage lost including key

Require explicit site re-enrollment. Restore inventory data from backups/replicas according to disaster-recovery procedures.

## 37.8 WAN split during ordinary site operation

Local site remains authoritative for its domain and queues upstream events.

---

# 38. API philosophy

The API is the product boundary. Official clients should use the same supported API rather than private DB shortcuts.

Clients include:

- web UI
- kiosk UI
- Android handheld
- host device agent
- automations/scripts
- slicer extension
- corporate UI
- third-party integrations

## 38.1 Baseline protocol

REST/JSON is sufficient for ordinary resources/commands.

SSE or WebSocket may be used for:

- scan/workflow session events
- live UI updates
- print status
- notifications
- replication/management status

GraphQL/gRPC are not required unless a concrete need emerges.

## 38.2 Command endpoints

Use semantic domain commands rather than exposing unrestricted patch endpoints for important operations.

Examples:

```text
POST /actions/checkout
POST /actions/checkin
POST /actions/move
POST /actions/consume
POST /actions/split-holding
POST /actions/transfer
```

Generic attribute/tag editing still exists for authorized users/scripts, but normal business operations should preserve semantic audit history.

## 38.3 Idempotency key

Every mutation endpoint used by unreliable networks/clients should accept an idempotency key.

## 38.4 Expected version

Offline/stale-sensitive actions should optionally include expected object version/preconditions.

---

# 39. Event model

A common event envelope is recommended:

```json
{
  "event_id": "uuid",
  "event_type": "item.checked_out",
  "schema_version": 1,
  "source_site_id": "uuid",
  "occurred_at": "timestamp",
  "actor": {"user_id": "...", "principal_id": "..."},
  "subject": {"kind": "object", "id": "...", "version": 42},
  "correlation_id": "command/workflow/session id",
  "payload": {}
}
```

Events should be versioned for compatibility.

Potential event types:

```text
object.created
object.updated
object.moved
relationship.created
relationship.removed
quantity.adjusted
stock.consumed
holding.split
holding.merged
checkout.created
checkout.closed
transfer.requested
transfer.authorized
transfer.accepted
print.completed
manufacturing.completed
config.applied
config.rollback
peer.trusted
role.updated
```

---

# 40. Attachments and blob storage

## 40.1 Metadata in PostgreSQL

Store:

- attachment UUID
- filename
- MIME type
- size
- hash
- logical owner/reference
- storage backend/key
- created metadata

## 40.2 Blob backend abstraction

Support at least a filesystem backend for simple deployments. Later backends may include S3/MinIO/cloud object stores.

## 40.3 Content hashes

Use SHA-256 or equivalent for integrity/deduplication. Replication can avoid transferring a blob already present by hash.

---

# 41. Search

MVP search should support:

- object name
- object type
- identifiers
- tags (including inherited tags)
- typed attributes
- owner/custodian/checked-out-to
- containment/location path
- quantity availability

Examples:

```text
tag:M5
inside:"Storage Unit" type:"Stepper Motor"
tag:"Measurement Equipment" NOT tag:"EUV Lab"
```

Exact query syntax is DEFERRED. Do not let search syntax design block the data model.

PostgreSQL recursive CTEs are adequate for early containment/path traversal.

---

# 42. Containment and assemblies

An object can be both a searchable asset and a container/assembly.

Examples:

```text
Storage Room
  -> Shelf Unit
      -> Rack
          -> Box
              -> Organizer
                  -> M5 nuts holding
```

and:

```text
EV Prototype
  -> Front Cosmetic Panel Assembly
      -> Printed Panel
      -> 12 x M5 T-slot nuts (consumption/allocation record)
      -> Bracket
```

Moving a parent container changes the effective location of descendants without rewriting every child path.

Containment cycles must be rejected.

Do not persist human-readable paths as the source of truth. They are derived/cached data.

---

# 43. Usage / installed-in semantics

Unique assets can be installed in another object:

```text
Engine #1 --installed_in--> EV Prototype
```

This does not destroy the engine object. It may affect availability/custody/location.

Fungible consumables are different:

```text
12 ea M5 T-slot nuts consumed/allocated to Front Panel Assembly
0.15 m AWG12 wire consumed by Rear Blinker Harness
```

Do not invent 12 individual nut UUIDs merely to express use.

Maintain enough transaction history to answer what material went into a build even after it is no longer available stock.

---

# 44. OpenLab reference workflow

This is a reference use case and performance target, not a hardcoded product mode.

## 44.1 Monitor authentication

```text
Idle screen: "Scan monitor iCard"
  -> scan UIN barcode
  -> resolve credential binding
  -> prompt short PIN if monitor policy requires
  -> create short-lived kiosk session
```

The UI should become actionable almost immediately.

## 44.2 Checkout

Monitor taps **Checkout** once.

Workflow enters continuous scan mode.

Each transaction requires:

- 1 asset
- 1 recipient principal/iCard
- any order

When both are available:

- validate asset availability
- commit checkout atomically
- emit history/event
- give beep/visual success
- clear pair
- accept next scans

No additional GUI interaction is required between normal transactions.

## 44.3 Return request

A regular user may invoke **Return item** for an item currently checked out to them.

OpenLab-specific workflow may:

- set `openlab.checkout_state = return_pending`
- show instructions: leave item at monitor desk
- emit notification/event

The core checkout remains open until monitor verification.

## 44.4 Monitor check-in

Monitor enters Check In mode and scans returned item(s). Workflow validates and closes checkout, clears local workflow state, and restores appropriate location/custody.

## 44.5 Timeout

After checkout session completion, if operator does not continue within a short configured period (e.g. 10 seconds), return to initial locked state and end monitor session.

Exact timing should be configurable but defaults should strongly favor unattended-terminal security and speed.

---

# 45. Automation/event rules

Support event-driven automation such as:

```text
WHEN checkout.return_requested
THEN notify OpenLab monitors
```

```text
WHEN stock.total_available < warning_threshold
THEN create restock alert
```

```text
WHEN manufacturing.print_completed
THEN decrement filament holding
```

Simple trigger/condition/action rules should be possible without custom code. Scripts/webhooks can be an advanced escape hatch.

Automations run under service/script principals with explicit capabilities.

---

# 46. Suggested repository structure

A monorepo is recommended initially.

```text
inventoryzing/
  server/
    coordinator/
  web/
    main-ui/
    kiosk-ui/
  agent/
    device-agent/
  handheld/
    android/
  replication/
    regional-node/
  management/
    control-plane/
  packages/
    api-schema/
    protocol/
    workflow-sdk/
    label-sdk/
    client-sdk/
    units/
  db/
    migrations/
    seeds/
  deploy/
    docker/
    compose/
    kubernetes/        # later / optional
  docs/
    architecture/
    federation/
    replication/
    workflows/
    api/
```

Do not create empty microservices merely because directories exist. Start with the components actually needed.

---

# 47. Deployment profiles

## 47.1 Personal/home

```text
coordinator container
PostgreSQL container
web UI
optional host device agent
filesystem blob storage
local accounts
```

No regional/corporate infrastructure required.

## 47.2 Workshop/lab

Add:

- kiosk UI
- device agent
- barcode/iCard credential bindings
- richer RBAC
- configured workflows
- optional SSO
- backups

## 47.3 Enterprise/multi-site

Add:

- site machine enrollment
- overlay network/internal DNS
- regional replication nodes
- corporate control plane
- desired-state config + rollback
- federated transfers
- centralized secret/PKI integration
- global reporting/search

The same coordinator codebase should serve all three profiles where practical.

---

# 48. Security requirements

## 48.1 Principle of least privilege

Users, scripts, device agents, and integrations receive only the capabilities they require.

## 48.2 Trust boundaries

Treat these as separate trust domains:

- browser/user
- kiosk/device agent
- site coordinator
- database
- remote peer installation
- regional replica
- corporate management plane

## 48.3 CSRF/session security

Normal web security requirements apply. Scanner convenience must not imply a globally privileged unauthenticated kiosk.

## 48.4 Audit sensitive changes

At minimum audit:

- role/permission changes
- trusted peer changes
- machine enrollment
- transfer authorization
- system configuration changes
- workflow/script changes
- high-value inventory adjustments

## 48.5 Credential privacy

Institutional identifiers such as UINs are identifiers, not authentication secrets. Store only the data needed for the workflow and protect access appropriately.

## 48.6 External QR safety

An object QR should not expose sensitive mutable inventory details. Prefer opaque identifiers/authority URLs with access control enforced server-side.

---

# 49. Observability

Provide structured logs and metrics for:

- API latency/error rate
- DB transaction failures/deadlocks
- scan resolution latency
- workflow execution failures
- print jobs
- replication lag/outbox depth
- regional projection lag
- config desired/applied generation mismatch
- auto-rollbacks
- certificate/enrollment health
- offline handheld queue depth/conflicts

Use correlation IDs across command -> event -> replication -> projection where possible.

---

# 50. Backup and disaster recovery

At minimum define:

- PostgreSQL backup/restore
- attachment/blob backup strategy
- site identity/key backup policy
- management-plane configuration backup
- replication-node rebuild procedure

A regional replica should help recovery but should not be treated as the only backup unless explicitly designed/tested as such.

Site identity private keys require special handling: loss implies re-enrollment; theft implies revocation.

---

# 51. Performance goals

These are architectural targets, not hard benchmark commitments yet.

Local kiosk operations should feel immediate.

Recommended targets on a healthy local deployment:

- credential/scan resolution: typically <100 ms server-side where cached/indexed
- UI state transition after scan: visible within a few hundred milliseconds end-to-end
- normal checkout commit: comfortably sub-second, ideally well below that
- no full page reloads in scanner workflows
- batch scan processing should keep up with human scanner cadence without queue buildup

Performance must be measured from the operator's perspective, not merely API benchmarks.

---

# 52. MVP implementation order

## Phase 0 - Foundations

Implement:

- repository/tooling
- coordinator skeleton
- PostgreSQL migrations
- API conventions
- UUID/version/idempotency conventions
- authentication/session skeleton
- transactional outbox skeleton

Acceptance: server boots in containers, migrations are repeatable, one mutation can atomically update state + history + outbox.

## Phase 1 - Core inventory

Implement:

- object types
- objects
- containment relationship
- typed attributes
- tags + inheritance DAG
- identifiers
- basic search
- history
- quantity + units/holdings

Acceptance: user can create nested storage hierarchy, add stock/assets, search by type/tag, and move objects without stale overwrite races.

## Phase 2 - Labels and generic scanning

Implement:

- SVG label renderer
- label templates
- QR/short-ID resolver
- browser camera scan support if practical
- host device agent MVP for HID scanner + print queue

Acceptance: an object/container can be labeled, scanned, resolved, moved, and reprinted without dependence on one printer model.

## Phase 3 - Principals, checkout, RBAC

Implement:

- principals
- user accounts
- credential bindings
- roles/permissions
- checkout core
- audit events

Acceptance: OpenLab-style monitor/user permissions can be represented without hardcoded role names.

## Phase 4 - Workflow engine/kiosk

Implement:

- actions
- declarative inputs
- scan slots
- conditions
- core effects
- script/service execution capabilities
- timeout/session behavior
- kiosk workflow renderer

Acceptance: implement the reference OpenLab checkout flow with one initial button press followed by scan pairs and no per-item GUI clicks.

## Phase 5 - Android/offline client

Implement:

- PWA keyboard-wedge support first
- native Android thin client if needed
- offline command queue
- idempotency/retry
- expected-version conflict reporting

Acceptance: a supported checkout/move workflow can continue through temporary RF loss and reconcile without duplicate actions.

## Phase 6 - Federation and replication

Implement:

- site identities/trust
- transfer records
- outbox shipping/inbox dedup
- regional projection
- reconciliation manifest

Acceptance: two sites can perform an authorized object transfer and a regional node can maintain a searchable eventually consistent projection.

## Phase 7 - Corporate management plane

Implement:

- enrollment service
- desired-state config generations
- signed config bundles
- stable bootstrap recovery
- logical service discovery integration
- last-known-good health validation/rollback

Acceptance: deliberately push a bad replication endpoint and verify the site automatically rolls back without SSH/manual repair.

## Phase 8 - Manufacturing/advanced integrations

Implement as demand justifies:

- slicer plugin
- manufacturing jobs
- attachment/object storage improvements
- stock automations
- advanced custom views
- more scanner/printer adapters

---

# 53. Testing strategy

## 53.1 Unit tests

Test:

- tag DAG cycle detection
- quantity/unit conversion
- relationship validators
- permission evaluation
- workflow scan-slot matching
- credential parser/namespace resolution
- config schema/rollback logic
- event deduplication

## 53.2 Database concurrency tests

Explicitly reproduce races:

- two users move same object concurrently
- bulk import while another user edits one included object
- stale client retries mutation
- split/consume same holding concurrently

Expected result: no unrelated changes are lost; conflicts are explicit.

## 53.3 Workflow performance tests

Automate sequences of scan events at realistic/high cadence and verify:

- no dropped scans
- deterministic pairing
- low latency
- safe timeout/logout
- easy undo/reversal

## 53.4 Offline tests

Simulate:

- response lost after server commit
- duplicate retry
- reconnect with stale version
- long disconnection
- partial queue sync

Verify idempotency and conflict behavior.

## 53.5 Replication tests

Simulate:

- duplicate events
- out-of-order events
- missing events
- regional node restart
- site offline for extended period
- reconciliation mismatch

## 53.6 Management-plane chaos tests

Test:

- invalid signed/unsigned config
- malformed config
- unreachable primary endpoint
- DNS failure
- corporate unavailable
- erased managed config
- certificate expiration/renewal failure

Verify last-known-good and bootstrap recovery invariants.

---

# 54. Acceptance scenarios

The following end-to-end scenarios should guide implementation.

## Scenario A: Personal nested inventory

Create:

```text
Storage Room A
 -> Shelving Unit B
   -> Rack 2
     -> Box C
       -> Organizer D
         -> M5 Slot Nut, 500 ea
```

Search `M5 Slot Nut` and return the quantity and complete effective path.

Move Box C elsewhere; descendants' effective paths change without rewriting each descendant.

## Scenario B: Split fungible stock

Start with 500 nuts in Organizer D. Move 100 to Robotics Toolbox. End with two holdings and correct history, without creating 500 serialized nut objects.

## Scenario C: EV assembly usage

Install unique Engine #1 into EV Prototype. Consume 12 M5 nuts and approximately 0.15 m of 12 AWG wire into child assemblies. Search the EV's as-built contents and trace unique and fungible usage.

## Scenario D: OpenLab checkout

Monitor scans iCard + PIN, presses Checkout, then scans item/user pairs continuously. No GUI interaction between pairs. Session summary groups items by borrower. Timeout logs monitor out.

## Scenario E: Return pending without core schema change

Borrower invokes Return Item. Workflow writes `openlab.checkout_state=return_pending` and shows instructions. Monitor later scans item and closes core checkout.

## Scenario F: Label portability

Render one object label to SVG. Print through configured thermal backend, download as PNG, and impose on PDF sheet without changing object identity or label content model.

## Scenario G: Offline handheld

Handheld loses Wi-Fi after user scans a valid checkout. It queues command, reconnects, retries with same idempotency key, and results in exactly one checkout.

## Scenario H: Cross-site transfer

System X authorizes Item A transfer to trusted System Y. Y scans Item A, discovers/queries X, validates pending transfer, imports required metadata/custody record, and X records external custody.

## Scenario I: Regional projection

Site commits an object move. Event appears in outbox, regional node consumes it, updates projection, corporate search sees new location. Duplicate event delivery does not duplicate effects.

## Scenario J: Bad corporate config

Corporate pushes a configuration generation with an unreachable replication target. Site stages/tests it, fails health checks, rolls back automatically, reports failure, and remains manageable through bootstrap path.

---

# 55. Decisions that are intentionally deferred

Do not lock these down prematurely:

- Coordinator implementation language/framework
- ORM/query builder
- Exact JavaScript/native web framework
- Android native framework
- Script language/runtime (JavaScript/Python/WASM/etc.)
- Exact search query syntax
- Exact unit library
- Exact VPN overlay product
- Exact internal PKI product
- Exact secret manager
- Exact regional service-discovery implementation
- Exact blob/object-storage backend
- Exact serverless/cloud deployment platform
- Merkle-tree reconciliation implementation
- Exact federation protocol transport details

Implement interfaces/boundaries that preserve the option to choose later.

---

# 56. Architecture decisions that should NOT be silently changed

The implementation agent should treat these as project-level decisions:

- PostgreSQL is the baseline transactional database.
- Site coordinator is authoritative for local operational data.
- Coordinator begins as a modular monolith.
- Hardware access belongs in host-native/device clients, not as a coordinator USB requirement.
- Labels are generated device-independently before printer-specific output.
- Scanner inputs resolve through generic namespaced identifiers.
- User accounts and possession principals are distinct.
- Ownership, assignment, custody, location, and checkout are distinct concepts.
- Checkout target is first-class; organization-specific return/inspection state is workflow metadata/attributes unless a broader universal need emerges.
- Normal users interact through actions/workflows, not arbitrary tag editing.
- Scripts have explicit execution capabilities separate from caller permissions.
- Routine scanner workflows must be faster/easier than manual administrative entry.
- Fungible stock is tracked by quantity/holding, not one UUID per unit.
- Quantity supports non-integer unit-bearing values.
- Tag inheritance is a DAG.
- Important relationships are typed/validated; do not reduce the entire system to untyped triples.
- Writes must avoid stale aggregate snapshot overwrite.
- Every authoritative mutation creates history and a replication outbox event atomically.
- Replication uses application/domain events, not PostgreSQL replication as the product protocol.
- Regional/corporate stores are projections, not independent writable masters of site-owned state.
- External mutations are idempotent.
- Managed site configuration uses logical services/identities, not hand-entered raw IPs.
- Sites enroll with cryptographic machine identity.
- Desired configuration is versioned, signed, staged, health-checked, and auto-rolled back to last-known-good on failure.
- A protected bootstrap identity/path survives ordinary managed-config mistakes.
- A site must continue core local inventory work when WAN/corporate is down.

---

# 57. Suggested first database tables

This is a concrete starting point, not a frozen schema.

```text
object_types
objects
relationships
relationship_types/definitions (optional)
principals
identifiers
user_accounts
credential_bindings
roles
permissions
role_permissions
user_roles
role_inheritance (optional)
tags
tag_edges
type_tags
object_tags
attribute_definitions
object_attribute_values
type_attribute_values
units
holdings / quantity_state
checkouts
attachments
label_templates
label_profiles
workflows
actions/workflow_versions
workflow_invocation_permissions
service_accounts
history_events
domain_events
replication_outbox
replication_inbox
trusted_sites
transfers
site_identity/config metadata
idempotency_records
```

Use migrations from day one.

---

# 58. Suggested API surface for early development

Illustrative only.

```text
GET/POST /api/v1/object-types
GET/POST /api/v1/objects
GET/PATCH /api/v1/objects/{id}
GET /api/v1/objects/{id}/path
GET /api/v1/objects/{id}/contents
GET /api/v1/search

POST /api/v1/actions/move
POST /api/v1/actions/consume
POST /api/v1/actions/split
POST /api/v1/actions/checkout
POST /api/v1/actions/checkin

POST /api/v1/resolve
POST /api/v1/scans/resolve

GET/POST /api/v1/labels/templates
POST /api/v1/labels/render
POST /api/v1/print-jobs

GET/POST /api/v1/workflows
POST /api/v1/workflows/{id}/sessions
POST /api/v1/workflow-sessions/{id}/scan

GET/POST /api/v1/principals
GET/POST /api/v1/users
POST /api/v1/credentials/bind

GET /api/v1/events
GET /api/v1/replication/feed
POST /api/v1/replication/events
```

Use OpenAPI or another machine-readable schema early so official clients can share generated types/SDKs.

---

# 59. Suggested internal command handling pattern

A command handler should roughly follow:

```text
receive request
 -> authenticate caller
 -> authorize action/invocation
 -> validate input schema
 -> resolve identifiers
 -> validate business preconditions
 -> begin DB transaction
 -> lock authoritative rows in deterministic order
 -> re-read current authoritative state
 -> re-check state-dependent preconditions
 -> apply delta/patch
 -> update version(s)
 -> append history/domain event(s)
 -> append replication outbox event(s)
 -> commit
 -> publish local realtime notification
 -> return result + versions + event/correlation IDs
```

For idempotent endpoints, idempotency lookup/recording wraps this process so retries return the same committed result.

---

# 60. Final implementation philosophy

The implementation should favor a small number of powerful, composable primitives:

```text
objects/types
principals
relationships
tags + typed attributes
quantities/units
identifiers
permissions
workflows/actions
events
```

Then use those primitives to create extremely fast domain-specific experiences.

The system should not force every organization to become a software company, but technically capable deployments should be able to extend it deeply without forking the core.

The ideal result is a platform where:

- a home user can run two containers and print labels;
- a university workshop can build a two-scan checkout kiosk;
- a warehouse can use rugged offline handhelds;
- a research group can trace parts/files/material into a prototype;
- a corporation can aggregate thousands of site projections and reconfigure infrastructure without manually touching every server;
- and none of those use cases make the simpler ones painful.

That is the architectural bar for `inventoryzing`.

