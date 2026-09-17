# inventoryzing Architecture Amendment 002

**Status:** Accepted architectural amendment  
**Scope:** Distributed edge-case contracts, realistic engineering bounds, and corrections to Amendment 001  
**Applies to:** Original architecture handoff + `inventoryzing_architecture_amendment_001.md`  
**Intent:** Tighten correctness where inexpensive, and explicitly relax guarantees where stronger guarantees would impose disproportionate complexity

---

# 1. Purpose

This amendment resolves the remaining findings from the second adversarial review.

The core architectural direction remains unchanged. This amendment does **not** introduce new major subsystems. Its purpose is to make several edge-case guarantees explicit and to avoid over-engineering requirements whose practical value is low relative to their implementation cost.

The guiding principle is:

> inventoryzing SHOULD provide strong guarantees where they are cheap, local, and operationally important. It SHOULD explicitly document weaker guarantees where eliminating the remaining edge case would require disproportionate distributed-systems complexity.

This amendment specifically resolves:

1. foreign micro-label ambiguity,
2. site-local ID reuse after stale backup restoration,
3. structural advisory-lock snapshot correctness,
4. idempotency across authority handoff,
5. publication-stream ordering,
6. site-incarnation fencing after disaster recovery,
7. practical micro-label encoding validation.

Where this amendment conflicts with Amendment 001, **this amendment takes precedence**.

---

# 2. Engineering-Bounds Principle

inventoryzing MUST distinguish between:

- **correctness requirements whose violation can silently corrupt canonical inventory state**, and
- **rare physical-world ambiguity or recoverability edge cases whose complete elimination would require disproportionate complexity**.

The system MUST strongly protect the first category.

The system MAY explicitly accept bounded risk in the second category if all of the following are true:

1. the risk is documented;
2. the common-path behavior remains deterministic;
3. failure does not silently create repeated destructive actions;
4. the UI exposes uncertainty when it is known;
5. a reasonable manual recovery path exists;
6. the risk is materially smaller than ordinary real-world inventory error, loss, or misplacement.

This principle is especially relevant to:

- micro-label aliases after stale disaster recovery;
- uncertain outcomes across an authority handoff;
- physical truth after conflicting disconnected actions.

---

# 3. Micro Labels Are Strictly Site-Local

The micro-label model is simplified.

A micro label MUST be treated as a compact alias valid only within the issuing site's local namespace.

Example payload:

```text
I428731
```

The payload does **not** contain:

- source-site identity;
- a UUID;
- a globally resolvable authority;
- a transfer token;
- enough information to distinguish identical aliases created by another site.

Therefore:

> A bare micro label MUST NOT be resolved outside the issuing site's local namespace.

The implementation MUST NOT attempt heuristic foreign resolution by:

- querying trusted peers;
- searching transfer manifests without explicit prior source context;
- matching against arbitrary remote aliases;
- falling back to the receiving site's own local alias namespace after a failed foreign lookup.

A micro label is deliberately **not portable**.

---

# 4. Cross-Site Transfer Requires Global Identification

Before a micro-labeled object is transferred to another inventoryzing site, the object MUST have a globally identifying label or equivalent globally identifying transfer artifact.

The normal transfer workflow SHOULD require a label containing at least the object's canonical UUID.

A transfer workflow SHOULD behave conceptually as:

```text
transfer requested
    ↓
object has portable/global identifier?
    ├── yes -> continue
    └── no  -> require printing/applying full UUID label
```

A full label MAY additionally contain:

- home authority identity;
- globally resolvable URI;
- human-readable name;
- transfer metadata.

Those are optional.

The UUID is sufficient for identity.

---

# 5. Foreign Micro Labels MUST NOT Remain Active in Normal Destination Scanning

Because two sites may validly issue the same local alias to different objects, a foreign micro label left active at the destination can cause accidental local misidentification.

Before an object enters another site's ordinary scanning environment, the source site's micro label SHOULD be:

- removed,
- covered,
- disabled,
- visually marked invalid,
- or otherwise prevented from being interpreted as a local destination-site micro label.

The destination MAY assign and print a new local micro label after receipt.

Example:

```text
Global UUID: 0199af28-...

OpenLab local alias: I428731
XRI local alias:     I107284
```

The global UUID does not change.

Historical alias records MAY remain in the database for audit/history even if the physical old micro label is no longer active.

---

# 6. MVP Micro-Label Payload Grammar

For the MVP, the canonical micro-label payload SHALL be:

```text
I + exactly six decimal digits
```

Examples:

```text
I000001
I004821
I428731
I999999
```

The following are invalid MVP encodings:

```text
I4821
428731
i428731
I42A731
```

Leading zeroes are significant in the encoded form.

The input resolver MAY offer human-facing convenience search for the numeric portion alone, but machine-readable micro labels MUST use the canonical payload form.

A future protocol version MAY introduce:

- eight-digit aliases;
- alphanumeric aliases;
- a different compact prefix;
- versioned micro-label namespaces.

Such changes MUST use an unambiguous new grammar.

---

# 7. Barcode Symbology Is a Label-Template Decision

The phrase "micro label" MUST NOT imply a specific barcode symbology.

The label-rendering system MAY select among supported machine-readable formats such as:

- QR Code,
- Data Matrix,
- another future supported symbology.

Selection SHOULD be based on:

- physical label dimensions;
- printer DPI;
- scanner capabilities;
- required quiet zones;
- encoded payload size;
- expected damage/print quality.

The core identifier protocol remains:

```text
I###### 
```

independent of visual barcode format.

---

# 8. Micro-Label Physical Validation Is an MVP Acceptance Requirement

Before bulk use of micro labels, the implementation MUST perform representative physical print-and-scan validation.

At minimum, test:

- intended physical label size;
- intended 300 DPI printer class;
- expected scanner hardware;
- at least one phone-camera workflow if supported;
- representative low-quality or slightly damaged print samples;
- QR and/or Data Matrix candidates where supported.

The specification MUST NOT assume that a shorter payload automatically guarantees reliable scanning at a given physical size.

The chosen default micro-label template SHOULD document:

```text
physical dimensions
printer DPI
symbology
module size
quiet-zone requirement
minimum tested scanner class
```

This is an empirical hardware constraint, not merely a data-model decision.

---

# 9. Site-Local Alias Reuse: Normal-Operation Guarantee

During normal operation, a site-local alias MUST NOT be reassigned to an unrelated entity.

If:

```text
Site A / 428731 -> UUID X
```

has ever been recorded in the site's current retained history, the application MUST NOT intentionally assign `428731` to another unrelated entity.

Archived, deleted, depleted, or transferred objects do not make their known alias eligible for reuse.

This rule remains stronger than the wording in Amendment 001:

> A known alias MUST NOT be reassigned to an unrelated entity.

---

# 10. Disaster-Restore Limitation for Site-Local Aliases

A stale backup may predate issuance of a physical micro label.

Example:

```text
T0: backup created
T1: I428731 assigned to Object A and physically printed
T2: site storage is lost
T3: backup from T0 is restored
```

The restored database has no knowledge that `I428731` was ever issued.

Without any surviving state outside that backup, it is impossible to guarantee that the alias will never be issued again.

inventoryzing explicitly accepts this limitation for local micro labels.

The system MUST NOT claim disaster-proof uniqueness for allocations absent from all surviving records.

---

# 11. Micro IDs SHOULD Use Random Allocation

To reduce deterministic alias reuse after stale backup restoration, the MVP SHOULD allocate six-digit aliases uniformly or pseudo-randomly from:

```text
000000 .. 999999
```

The allocator MUST reject values already present in the current database and retry.

The allocator MUST NOT use a simple restored monotonic sequence as the default micro-ID strategy.

This does not provide a mathematical no-collision guarantee against aliases that existed only after the restored backup, but it avoids deterministic reuse of the same post-backup sequence.

For the expected use case of only tens of micro-labeled artifacts per site, this residual risk is acceptable.

Deployments requiring stronger guarantees MAY use:

- a larger local-ID namespace;
- replicated allocation ledgers;
- externally durable allocation state;
- full UUID labels instead of micro labels.

None of those are required for the personal MVP.

---

# 12. Local Alias Values MUST NOT Carry Semantic Meaning

Applications MUST NOT infer from the numeric micro ID:

- creation time;
- age;
- ordering;
- type;
- site identity;
- ownership;
- transfer history.

The value is only an opaque local alias.

---

# 13. Structural Advisory Locking Requires Fresh Reads

Amendment 001's structural advisory locking requirement is strengthened.

Any operation that may alter a structural graph invariant MUST:

1. begin a database transaction using `READ COMMITTED`, or another mode proven to provide equivalent fresh post-lock reads;
2. acquire the appropriate transaction-scoped structural advisory lock;
3. only then perform the graph reads used for validation;
4. validate all relevant invariants against state visible after acquiring the lock;
5. apply the mutation;
6. commit history/outbox/state atomically.

The implementation MUST NOT:

1. create a `REPEATABLE READ` or stale snapshot,
2. read structural state,
3. wait for the structural advisory lock,
4. then validate using that older snapshot.

Recommended conceptual flow:

```text
BEGIN ISOLATION LEVEL READ COMMITTED;

acquire STRUCTURAL_LOCK_PHYSICAL_HIERARCHY;

read current physical hierarchy;
validate cycle and parent constraints;
apply mutation;
write history;
write outbox;

COMMIT;
```

---

# 14. Structural Locks Are Effect-Based, Not Workflow-Based

Lock requirements MUST be attached to semantic mutation primitives, not to high-level command names.

For example:

```text
set_physical_parent(...)
```

MUST acquire the physical-hierarchy structural lock whenever it changes the hierarchy.

```text
add_tag_inheritance(...)
```

MUST acquire the tag-inheritance structural lock.

A checkout workflow normally does not require the physical structural lock.

However, if a particular checkout workflow also moves an object to another physical parent, the underlying physical-parent mutation MUST acquire the lock automatically.

Scripts and workflow actions MUST NOT be able to bypass graph invariants merely because their high-level action type was not expected to alter structure.

---

# 15. Commands Are Bound to an Authority Domain

A mutating command MUST be bound to the authority that was responsible for it when the command was created.

The effective command identity SHOULD include:

```text
authority_site
authority_epoch
command_id
```

Example:

```text
authority_site  = Site X
authority_epoch = 17
command_id      = UUID C
```

A command MUST NOT be silently replayed against another authority site or a later authority epoch.

---

# 16. Idempotency Does Not Migrate Across Authority Handoffs

The system explicitly does **not** require global migration of command receipts across sites during authority transfer.

Scenario:

```text
1. Site X executes command C.
2. X commits successfully.
3. Response to client is lost.
4. Authority transfers from X epoch 17 to Y epoch 18.
5. Client retries C against Y.
```

Site Y MUST NOT execute C.

Y SHOULD return a result such as:

```text
COMMAND_BELONGS_TO_PREVIOUS_AUTHORITY
```

or:

```text
OUTCOME_REQUIRES_PREVIOUS_AUTHORITY
```

If X remains reachable, the client MAY query/retry the original command receipt against X.

If X is permanently unavailable and the outcome cannot be determined, the system MAY report:

```text
OUTCOME_UNKNOWN
```

and require manual or workflow-based reconciliation.

This uncertainty is accepted.

The system prefers:

> occasional uncertainty about whether one rare operation completed

over:

> accidentally executing the same destructive operation twice.

---

# 17. Commands MUST NOT Cross Authority Epochs Automatically

Command routers, regional nodes, federation gateways, and clients MUST NOT transform:

```text
command for Site X / epoch 17
```

into:

```text
equivalent new command for Site Y / epoch 18
```

without an explicit new user/system decision.

A new command may be created after reconciliation, but it is a distinct operation with a distinct `command_id`.

---

# 18. Command Receipt Retention

Each authority SHOULD retain command receipts long enough to cover realistic retry and recovery windows.

The exact retention period is deployment policy.

Receipt access MUST remain authorization-checked.

Possession of a `command_id` alone MUST NOT grant permission to inspect a command result.

Distributed permanent receipt replication is not required for the MVP.

---

# 19. Publication Assignment MUST Be Serialized Per Stream

Amendment 001's publication-sequence mechanism is strengthened.

Assigning publication sequence numbers MUST itself be serialized per replication stream through commit.

Multiple publisher processes MAY exist, but only one transaction at a time may assign the next ordered prefix for a given stream.

Conceptually:

```text
BEGIN;

acquire PUBLICATION_STREAM_LOCK(stream_id);

select committed unpublished outbox records;
assign contiguous publication sequence numbers;
mark records published;

COMMIT;
```

The publication-stream lock MUST be held until commit or rollback.

This prevents two publishers from independently assigning adjacent sequences and committing out of order.

---

# 20. Publication Order Is a Delivery Contract, Not Domain Commit Order

The publication sequence defines:

> the order in which committed events are exposed on a replication stream.

It does not claim to reproduce exact PostgreSQL transaction start order or internal sequence-allocation order.

This distinction is intentional.

Once assigned and committed, publication sequence numbers MUST form a safe committed prefix for downstream cursor traversal.

---

# 21. Snapshot Creation Requires a Publication Barrier

A consistent replica snapshot must not include a state mutation while omitting the corresponding event from the snapshot's declared event cursor.

Therefore, snapshot creation for a replication stream MUST use a protocol that establishes a consistent state/event boundary.

The initial recommended protocol is a short write/publication barrier:

```text
1. fence new relevant mutations;
2. wait for in-flight relevant transactions to finish;
3. publish all committed outbox rows for the stream;
4. record publication high-water mark N;
5. establish a consistent PostgreSQL snapshot;
6. release the mutation fence;
7. build/export the snapshot using that established database snapshot.
```

The bulk snapshot export MAY continue after writes resume once the database snapshot and event high-water mark are fixed.

This mechanism is required only when replication/snapshot functionality is implemented.

It does not block the personal MVP.

---

# 22. Incarnation Numbers Require External Enforcement

A `site_incarnation` field is not itself a fencing mechanism.

For incarnation fencing to be effective, remote systems MUST maintain and enforce the currently active incarnation.

Example:

```text
site_id = OpenLab
active_incarnation = 15
```

Requests, replication streams, and federation traffic from:

```text
OpenLab incarnation 14
```

MUST be rejected after incarnation 15 has been activated.

---

# 23. Disaster Recovery Activation

Replacing a failed/restored site instance is an exceptional management operation.

Recommended flow:

```text
1. administrator restores/rebuilds site;
2. management plane authorizes recovery;
3. new incarnation is allocated;
4. new/updated machine credentials are issued;
5. regional/corporate/federated peers record the new active incarnation;
6. old incarnation traffic is rejected;
7. reconciliation/resnapshot occurs as required.
```

A recovered site MUST NOT self-increment its incarnation and assume legitimacy without authorization from the management authority responsible for that identity.

---

# 24. Old Incarnations May Continue Local Offline Operation

External fencing cannot force a fully disconnected old clone to stop performing local actions.

This limitation is accepted.

However, once the old incarnation reconnects to managed peers, its traffic MUST be rejected.

The system therefore guarantees:

> stale incarnations cannot rejoin or overwrite the managed distributed system after a newer incarnation has been activated.

It does not guarantee:

> a physically isolated obsolete machine can be remotely prevented from doing local work.

---

# 25. Normal Operation Must Remain WAN-Independent

Incarnation activation and disaster recovery MAY require management-plane connectivity and explicit authorization.

Normal healthy site operation MUST NOT.

A site that has valid local identity and current incarnation SHOULD continue normal local inventory operation during WAN loss, including:

- local search;
- scanner workflows;
- checkout/check-in;
- quantity mutations;
- local printing;
- local authentication according to deployment policy;
- event/outbox accumulation.

This preserves the site-local-first architecture.

---

# 26. Revised Micro-Label Acceptance Tests

The MVP micro-label acceptance suite is revised.

The MVP MUST verify:

1. `I######` is the canonical machine-readable local payload grammar.
2. Six digits are treated as fixed-width in the encoded payload.
3. Known aliases are never intentionally reassigned during normal operation.
4. Random allocation does not collide with currently known aliases.
5. The implementation explicitly documents that stale disaster recovery can lose knowledge of post-backup alias allocations.
6. Micro labels resolve only in the current local site namespace.
7. A micro label from another site is not automatically recognized as foreign.
8. A cross-site transfer of a micro-labeled object requires a full UUID/global identifier.
9. Source local micro labels are removed, covered, or otherwise disabled before normal destination-site scanning.
10. The destination may assign a new local micro ID.
11. Business logic operates on canonical UUIDs after alias resolution.
12. Representative physical micro-labels are successfully printed and scanned on intended hardware.
13. The default symbology is selected empirically, not assumed from payload size alone.

The MVP does **not** need to test foreign micro-label transfer resolution because that feature is removed.

---

# 27. Revised Distributed Acceptance Gates

The following requirements apply before the corresponding distributed feature is considered production-ready.

## Before federation

Verify:

- authority-site + authority-epoch command binding;
- commands cannot automatically cross authority boundaries;
- duplicate retry at the original authority returns the original result;
- retry at a new authority does not re-execute the command;
- authority transfer uses an explicit epoch handoff;
- stale authority epochs are rejected.

## Before replication

Verify:

- publication assignment is serialized through commit per stream;
- event UUID deduplication is independent of stream sequence;
- cursor advancement and projection application are atomic;
- snapshot creation establishes a valid state/event high-water boundary;
- stream epochs handle destructive restores/reseeds;
- projections can be rebuilt from supported snapshot + retained-event inputs.

## Before distributed disaster recovery

Verify:

- incarnation activation is externally authorized;
- managed peers reject old incarnations;
- new credentials are associated with the new incarnation as required;
- stale restored nodes cannot rejoin as current writers;
- ordinary WAN loss does not prevent healthy local operation.

---

# 28. Explicitly Accepted Residual Risks

The architecture deliberately accepts the following bounded risks.

## 28.1 Lost micro-label allocation history

If all records of a post-backup micro-ID allocation are lost and an older backup is restored, the system cannot guarantee that the same six-digit alias will never be randomly issued again.

This is accepted because:

- micro labels are expected to be relatively rare;
- they are local-only;
- the probability is low with sparse random allocation;
- physical inventory error is expected to dominate this risk;
- eliminating it completely would require external durable allocation state or a larger namespace.

## 28.2 Unknown command outcome across permanent authority loss

If a command committed at an old authority, its response was lost, authority then transferred, and the old authority/receipt is permanently unavailable, the system may be unable to prove whether the operation committed.

This is accepted.

The system MUST NOT execute the old command at the new authority merely to eliminate uncertainty.

## 28.3 Conflicting disconnected physical actions

Two offline devices may record mutually incompatible real-world handovers before either can see canonical state.

The system may reject one command and require reconciliation.

This is accepted because software cannot reconstruct physical truth that was never synchronized.

---

# 29. Guarantees That Remain Strong

Relaxing the preceding edge cases does **not** relax the following core guarantees.

inventoryzing MUST still guarantee:

1. canonical UUID identity;
2. no intentional duplicate local alias in current known state;
3. one canonical write authority per object/authority scope;
4. structural graph invariants under concurrent mutations;
5. atomic mutation + history + outbox + idempotency receipt;
6. no duplicate execution of the same command at the same authority/epoch;
7. no silent command replay across authority epochs;
8. decimal-safe stock accounting;
9. semantic stock conservation rules;
10. deterministic local identifier resolution;
11. safe committed-prefix replication cursors;
12. idempotent replica event application;
13. explicit offline pending/rejected/reconciliation states;
14. externally enforced active incarnation for distributed participation.

---

# 30. Implementation Priority

These corrections do not justify another architecture-expansion cycle.

Before implementing the local core, the specification SHOULD be updated with these targeted contracts.

Then development SHOULD proceed.

The implementation order remains:

```text
1. local core correctness
2. personal inventory MVP
3. OpenLab workflow support
4. offline handheld support
5. federation and replication
6. corporate management
```

The engineering team SHOULD resist adding new distributed machinery unless a concrete use case demonstrates that the accepted residual risks are no longer acceptable.

---

# 31. Final Design Principle

The intended philosophy is:

> Do not spend six months eliminating a one-in-a-million administrative edge case while making the common workflow slower, harder to deploy, or impossible to finish.

inventoryzing is a physical inventory system.

Its software correctness must be strong enough that the software is not itself a meaningful source of silent corruption.

It does not need to pretend that distributed systems, stale backups, disconnected physical actions, damaged labels, and real-world human handling can all be reduced to perfect mathematical certainty.

Where perfect guarantees are cheap, use them.

Where they are expensive, document the boundary, fail safely, preserve auditability, and provide a recovery path.
