# inventoryzing Architecture Amendment 008

**Status:** Draft architectural amendment  
**Scope:** Hardware-independent inventory core, optional scanner and labeling/printing modules, and separate device daemons  
**Applies to:** Original architecture handoff + Amendments 001–007  
**Intent:** Make hardware integration an optional extension without coupling inventory core to scanning, label generation, printing, or vendor devices

---

# 1. Purpose and Precedence

inventoryzing core MUST remain unaware of scanner and printer integration concepts.

All scanner support, label generation, printer support, and device-specific behavior MUST belong to optional modules and their separate daemon processes.

This is a stronger boundary than merely hiding vendor SDKs behind generic `ScanEvent` and `PrintJob` interfaces inside inventory core. Those interfaces are themselves hardware-module concepts and MUST NOT become dependencies of inventory core.

Where this amendment conflicts with earlier wording, **this amendment takes precedence**. Earlier documents remain historical specifications governed by amendment order; they do not require individual superseded markers.

This amendment does not bring federation, corporate management, automatic disaster recovery, or distributed reconciliation into the MVP.

---

# 2. Meaning of Core, Coordinator, Module, and Daemon

The following distinctions are normative:

| Term | Responsibility |
|---|---|
| Inventory core | Canonical inventory semantics and hardware-independent services |
| Coordinator host | Application composition, hosting, and registration of core and enabled server modules |
| Scanner module | Scan ingestion, delivery, workstation/session routing, and authorized scanner feedback |
| Labeling/printing module | Label templates, encoding, rendering, profiles, print intent, jobs, attempts, and user-facing print status |
| Device daemon | A separate host-native process responsible for device access and durable local device orchestration |
| Vendor provider | An adapter within the device subsystem that implements a specific SDK or transport |

The coordinator host is not synonymous with inventory core. A capability may run in the coordinator process without becoming part of inventory core.

Likewise, an existing project named `Inventoryzing.Agent.Core` is a shared library of the device subsystem. Its name does not make its contracts part of inventory core.

---

# 3. Inventory Core Responsibilities

Inventory core continues to own the applicable universal semantics described in the earlier amendments:

- entity and object identity;
- objects, types, classification, and physical hierarchy;
- principals, ownership, assignment, custody, checkout, and quantities;
- namespaced identifiers, alias allocation, uniqueness, and resolution;
- metadata definitions and values;
- authorization and human/service identities;
- semantic inventory commands, versions, receipts, history, and outbox behavior.

Some of these remain later implementation milestones. Listing them here defines ownership, not a requirement to finish them before the current device work.

Inventory core MAY provide hardware-independent extension facilities such as module registration, permission registration, event publication, blob storage, and command execution support.

It MUST NOT contain scanner/printer domain objects, hardware protocols, device discovery, barcode symbologies, label templates, printer profiles, spool jobs, device feedback commands, or vendor SDK dependencies.

A printer or scanner MAY still be tracked as an ordinary inventory asset. Core treats it like any other asset; creating that record does not register or control a device.

---

# 4. Dependency Direction

The dependency direction MUST be:

```text
Coordinator host
    -> Inventory core
    -> Enabled server modules

Scanner and labeling/printing modules
    -> Supported inventory-core contracts and shared platform services

Device daemons
    -> Versioned device-module protocols
    -> Device runtime libraries
    -> Selected vendor providers
```

Inventory core MUST NOT depend on the scanner module, labeling/printing module, device daemons, or their contracts.

Modules MUST use supported core interfaces to read inventory and perform semantic inventory mutations. They MUST NOT bypass core invariants by writing directly to core-owned tables.

Within one process, these interfaces MAY be ordinary application-service calls. HTTP between colocated modules and core is not required. Interface boundaries MUST NOT depend on sharing private ORM entities or unrestricted database handles with module callers.

Shared hosting infrastructure may dispatch registered module routes, permissions, or events without understanding their scanner/printing semantics.

---

# 5. Identifiers Remain Core; Their Physical Representation Does Not

Core owns canonical UUIDs and the namespaced identifier mappings that identify inventory entities.

The accepted site-local identifier semantics, allocation rules, and `I######` textual grammar remain unchanged. Textual identifier parsing does not require awareness of whether text was typed, pasted, scanned, or imported.

Core resolves an identifier to an entity or a defined resolution failure. It does not decode a barcode image or select a barcode format.

The labeling module owns encoding identifiers into QR, Data Matrix, or other supported physical representations. The scanner subsystem owns obtaining decoded input from hardware, camera, keyboard-wedge, or other scanner integrations.

Business commands receive canonical entity identities after resolution.

Identifier allocation authorization MUST be independent of label printing. A hardware-independent permission such as `identifier.issue` may protect allocation; `label.print` must not be the core permission for issuing an identifier.

Migration from the existing permission mapping MUST preserve intended authorized behavior without granting new privileges to unrelated roles.

---

# 6. Scanner Module Ownership

The scanner module owns:

- neutral scan-observation contracts and scanner source information;
- durable server-side ingestion and event deduplication;
- workstation and browser-session routing;
- scanner capabilities and current/last-known status;
- delivery acknowledgements;
- authorized semantic feedback requests and their results;
- scanner configuration, diagnostics, and module-specific APIs/UI.

Scanner observations are inputs, not inventory commands. Receiving a scan MUST NOT itself grant permission to move, check out, consume, or otherwise mutate inventory.

An explicitly active workflow may resolve the input and invoke a supported inventory command under the appropriate user or authorized workflow identity. Ordinary lookup remains lookup.

Workflow modules MAY consume the scanner module's public input contract. Inventory core MUST NOT acquire a scanner dependency to support those workflows. Manual input remains possible through the same identifier and inventory services.

Camera scanning and keyboard-wedge integration also belong to modules or their clients; browser implementation does not make scanning a core concern.

---

# 7. Labeling and Printing Module Ownership

The labeling/printing subsystem owns:

- versioned templates and template revisions;
- QR/barcode encoding and label-specific payload formatting;
- canonical SVG rendering and downloadable output;
- physical dimensions, color, DPI, media, and output profiles;
- authoritative preview and final artifact conversion;
- immutable render snapshots and artifact hashes;
- logical print jobs and distinct physical attempts;
- claim, dispatch, completion-evidence, and reconciliation protocols;
- print status, observations, history, and Resume/Retry/Reprint actions;
- label editor, print controls, and label-specific permissions.

These may be separate modules or separately owned components of one optional module package. The MVP does not require a general plugin marketplace or one service per component.

Label rendering and export MUST remain usable without a running printer daemon or attached printer. Physical printing requires the relevant daemon and provider.

The authoritative renderer belongs to the server-side labeling module. The browser authors template data and requests preview; the daemon receives the resolved dispatch artifact. Neither browser nor daemon becomes a competing authoritative layout engine.

Earlier statements that “the coordinator owns templates, artifacts, and print jobs” mean **the coordinator-hosted labeling/printing module owns them**, not inventory core.

---

# 8. Separate Device Daemons

Printer and scanner access MUST run in separate, independently startable host-native daemon processes.

The printer daemon owns durable local dispatch, artifact verification/cache, physical-printer serialization, provider invocation, completion monitoring, and local recovery/reporting.

The scanner daemon owns scanner connection lifecycle, bounded capture, durable local delivery, reconnect behavior, and constrained feedback execution.

Vendor SDKs, COM, Winspool, device handles, and equivalent platform-specific integration MUST remain in the device subsystem. They MUST NOT be loaded into the inventory core or coordinator-hosted server modules.

Shared device runtime libraries are allowed. Separate processes remain independently configurable and independently operable: missing or failed printer support must not prevent scanner operation, and vice versa.

Daemons MUST NOT become inventory authorities. A scanner daemon reports observations; it does not autonomously decide inventory mutations. A printer daemon executes authorized immutable attempts; it does not create new user print intent.

---

# 9. Hosting and Deployment

The initial server deployment MAY remain a modular monolith:

```text
Coordinator process
    Inventory core
    Optional scanner server module
    Optional labeling/printing server module

Separate printer daemon
Separate scanner daemon
```

The coordinator's composition layer MAY register only the enabled modules at startup. Static registration is sufficient initially; hot loading and dynamic installation are not MVP requirements.

Modules MAY share the coordinator's PostgreSQL instance, but MUST own their domain tables and migrations. Core schema and startup MUST NOT require scanner/printer tables, device registration, SDK availability, or reachable daemons.

Module-owned records MAY reference core identities through supported relationships. Core-owned inventory records MUST NOT require a print job, scanner, or device record to exist.

Future separate deployment of server modules is permitted. Clear contracts and data ownership should limit the work needed, but this amendment does not claim that moving an in-process module into a service requires no transport, authentication, or transaction changes.

Separate server deployments are not required now merely to achieve hardware independence.

---

# 10. Authentication and Transport

Amendment 007 remains controlling for authorization:

- browser modules normally use the current user's session;
- reusable privileged daemon credentials MUST NOT enter browser-delivered code;
- daemons authenticate as distinct service identities using the shared role/permission model;
- backend authorization remains mandatory regardless of which UI or module initiated a request.

Device registration, permissions, routing, and command validation are owned by the relevant modules. Core authentication may establish a service identity without understanding its hardware role.

Daemons initiate authenticated outbound connections to their server modules. Durable idempotent operations and a versioned bidirectional control channel may share the coordinator's hosting infrastructure. The selected HTTPS/WebSocket transport does not make its domain handlers part of inventory core.

Browser delivery remains through authenticated same-origin module endpoints in the initial deployment. No direct browser-to-vendor or raw device-command tunnel is required.

---

# 11. Durability and Side Effects

Moving functionality into modules MUST NOT weaken existing correctness requirements.

Inventory mutations still use core semantic commands and atomically persist their required state, versions, receipts, history, and outbox records.

Module mutations use the applicable shared transaction/idempotency infrastructure for module-owned state. Core infrastructure may persist an opaque registered module event without interpreting printer/scanner semantics.

Physical device work MUST NOT occur while an inventory database transaction is held open. Committed module intent and durable daemon state coordinate that external effect; a database rollback cannot undo a printed label or a physical handover.

Printer acceptance is not completion. Unknown physical outcomes must not cause automatic redispatch. Resume targets existing work; Retry and risk-acknowledged Reprint retain their distinct semantics.

Scanner capture/delivery must distinguish observation, durable capture, server acknowledgement, and successful inventory action. Device decode feedback is not proof of a committed inventory mutation.

The detailed daemon state machines, journals, and completion-evidence rules belong to the device modules and their implementation plan, not inventory core.

---

# 12. Optional UI and Module Removal

Scanner controls, device status, label editors, print buttons, and module-specific workflow pages MUST be contributed through module UI integration points.

Core object pages may host generic registered sections/actions. They MUST NOT require hardcoded scanner/printer imports or special knowledge of a label profile or print attempt.

Disabling these modules MUST leave core inventory startup, authentication, manual identifier lookup, and implemented inventory operations usable. Module routes/actions should be absent or explicitly unavailable rather than silently falling back to untracked hardware operations.

Module disablement/removal MUST NOT automatically delete persisted templates, artifacts, jobs, scan records, or other module-owned state. Explicit retention or destructive cleanup is a separate operation.

Disabling dispatch must not erase unresolved attempts. Monitoring/reconciliation and safe shutdown of in-flight work remain module responsibilities; reenabling a module must not blindly replay physical work.

---

# 13. Application to the Existing Implementation

Existing working components should be retained and moved behind these boundaries rather than rewritten solely to change their ownership.

The implementation should:

1. preserve the Brother and Zebra providers, their tests, the shared device runtime, and the separate executable hosts;
2. keep device-only contracts in the device subsystem, including the existing `Inventoryzing.Agent.Core` library;
3. move Segno/QR rendering and label endpoints out of core routes into the labeling module;
4. separate identifier-allocation permission from label-print permission;
5. move scanner and label-specific frontend behavior behind module registration;
6. place new device protocols, server records, and job handlers in module-owned code and migrations;
7. verify a core-only deployment before calling the boundary complete.

Existing API paths MAY remain as compatibility routes owned by the module. An unchanged URL does not require unchanged internal ownership.

This amendment defines the destination architecture. Migration may proceed incrementally, but new hardware functionality MUST follow the module boundary rather than deepen the existing coupling.

---

# 14. Acceptance Criteria

The boundary is complete when tests and deployment checks establish that:

1. inventory core builds/imports and starts without scanner, labeling, printer, or vendor dependencies;
2. core-only operation supports authentication, manual identifier resolution, and the implemented inventory workflows;
3. dependency checks reject imports from inventory core into the device modules or their contracts;
4. enabling modules registers their routes, permissions, migrations, and UI contributions without changing core domain behavior;
5. disabling modules does not require deleting core inventory or module-owned persisted data;
6. label rendering/export works with no printer daemon or physical printer;
7. printer and scanner daemons can run, stop, and fail independently;
8. browser module actions use user authority, and daemon credentials cannot authorize unrelated inventory operations;
9. scan delivery alone performs no inventory mutation, while an explicitly active authorized workflow can consume scans;
10. labels encode the same canonical identifiers that manual core lookup resolves;
11. print attempts retain durable identity and evidence-based outcomes across module/daemon interruptions without automatic redispatch of uncertain work;
12. module reenablement preserves unresolved work and does not silently replay physical side effects.

Default tests MUST remain hardware-free. Physical device scenarios continue to require explicit authorization for the selected scenario.

---

# 15. Scope Boundary

This amendment does not require microservices, a general-purpose plugin marketplace, runtime hot loading, a new authorization engine, or a distributed transaction system.

Corporate recovery, replication ordering, and distributed reconciliation remain deferred to their corresponding implementation milestones. They are not prerequisites for the current MVP module extraction.

The architectural requirement is:

> **Inventory core owns inventory. Optional modules own scanning, labels, and printing. Separate daemons own hardware access. The core remains useful without any of them.**
