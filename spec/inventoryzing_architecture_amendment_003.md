# inventoryzing Architecture Amendment 003

**Status:** Accepted architectural amendment  
**Scope:** Final adversarial-review corrections and explicit acceptance of micro-label transfer residual risk  
**Applies to:** Original architecture handoff + Amendments 001 and 002  
**Intent:** Close the remaining specification gaps without introducing new subsystems

---

# 1. Purpose

This amendment resolves the three remaining findings from the third adversarial review:

1. command-receipt expiration versus idempotency guarantees;
2. randomness requirements for site-local micro-ID allocation;
3. physical handling requirements for source-site micro labels during cross-site transfer.

No new major subsystem is introduced.

Where this amendment conflicts with earlier wording, **this amendment takes precedence**.

---

# 2. Engineering Position

The architecture intentionally does **not** attempt to eliminate every theoretically possible operational failure.

The system SHOULD provide strong guarantees where:

- silent software corruption is plausible;
- the guarantee is cheap to enforce;
- failures would be difficult to detect or reconcile.

The system MAY accept documented residual risk where:

- several independent operator/workflow failures must align;
- the resulting failure is detectable;
- the resulting failure is limited in consequence;
- eliminating the risk would require disproportionate protocol complexity.

The micro-label cross-site case described below is explicitly placed in the latter category.

---

# 3. Command Receipt Epochs

Command receipts MUST NOT be deleted while their commands remain eligible for execution.

To allow eventual receipt cleanup without violating idempotency, mutating commands MUST belong to a **command epoch**.

A command identity is therefore scoped by:

```text
authority_site
authority_epoch
command_epoch
command_id
```

Example:

```text
authority_site  = OpenLab
authority_epoch = 17
command_epoch   = 42
command_id      = 0199...
```

---

# 4. Command Epoch Lifecycle

A command epoch has two relevant states:

```text
OPEN
CLOSED
```

While an epoch is `OPEN`:

- new commands in that epoch may be accepted;
- retries may be accepted;
- command receipts required for idempotency MUST be retained.

Once an epoch is permanently `CLOSED`:

- no new command in that epoch may execute;
- retries from that epoch MUST NOT execute;
- retained receipts from that epoch MAY eventually be deleted.

The core invariant is:

> **A command receipt may only be discarded after the system has permanently stopped accepting executable commands from that command epoch.**

This prevents the failure mode:

```text
command succeeds
receipt deleted
same command ID retried
server treats it as new work
command executes again
```

---

# 5. Expired Command Behavior

If a client retries a command from an epoch whose deduplication state is no longer retained, the server MUST fail safely.

Recommended result:

```text
COMMAND_EPOCH_EXPIRED
```

or equivalent.

The server MUST NOT execute the command again merely because its old receipt no longer exists.

If the real-world or canonical state is uncertain, the client or operator MAY perform reconciliation and issue a new, explicitly distinct command.

---

# 6. Command Receipt Retention Policy

Receipt retention MAY be deployment-configurable.

A simple MVP policy is acceptable:

- retain all receipts for the active command epoch;
- retain one or more recently closed epochs for a grace period;
- only delete receipts after their epoch is permanently non-executable.

A future optimization MAY separate:

```text
full result payload
```

from:

```text
minimal deduplication tombstone
```

so that large response payloads can expire earlier than the compact evidence needed to prevent duplicate execution.

This optimization is not required for the MVP.

---

# 7. Offline Clients and Expired Command Epochs

An offline handheld may retain a queued command long enough that its command epoch closes before reconnection.

In that case, the server MUST NOT reinterpret the stale operation as a fresh command.

The client SHOULD transition the queued operation to a reconciliation state such as:

```text
REJECTED_COMMAND_EPOCH_EXPIRED
```

The user or workflow may then inspect the current canonical state and explicitly decide whether a new command should be created.

This is preferable to silently replaying stale physical intent after an arbitrarily long delay.

---

# 8. Site-Local Micro-ID Randomness

Micro-ID allocation MUST use operating-system-backed secure randomness or an equivalent platform CSPRNG.

The application MUST NOT use:

- time-seeded pseudo-random generators;
- deterministic application seeds persisted in the ordinary database backup;
- predictable counters as the default allocation source.

Suitable sources include platform APIs equivalent to:

```text
Linux getrandom()
Windows BCryptGenRandom
Java SecureRandom
Python secrets
Rust OsRng
Go crypto/rand
```

The application SHOULD use the standard secure-random API of its implementation language/runtime rather than implement its own RNG.

---

# 9. Micro-ID Allocation Procedure

For the six-digit MVP namespace:

```text
000000 .. 999999
```

allocation SHOULD behave conceptually as:

```text
1. obtain candidate from OS-backed CSPRNG
2. attempt database insert
3. database uniqueness constraint decides whether candidate is free
4. if conflict:
       generate another candidate
5. repeat up to bounded retry limit
6. fail explicitly if retry limit is exhausted
```

The database uniqueness constraint remains the final authority.

Concurrent allocators therefore remain safe.

Example:

```text
Process A chooses 428731
Process B chooses 428731

A insert succeeds
B insert conflicts
B retries
```

---

# 10. Bounded Retry

Micro-ID allocation MUST use bounded retries.

The exact limit is implementation-defined.

A limit such as:

```text
32 attempts
```

is more than sufficient for the expected sparse usage of the six-digit namespace.

Exhaustion MUST fail explicitly with an allocation error rather than loop indefinitely.

If a deployment reaches density where random allocation regularly encounters conflicts, that deployment SHOULD migrate to a larger namespace or stop using six-digit micro labels for newly labeled assets.

---

# 11. Restored RNG State

Because the allocator relies on the host operating system's secure random source rather than an application-persisted deterministic generator state, restoring an old inventory database backup does not intentionally replay the previous application's random sequence.

This materially reduces the stale-backup alias-reuse risk described in Amendment 002.

It does not create a mathematical guarantee against collision with aliases whose allocation history was completely lost.

That remaining bounded risk remains accepted.

---

# 12. Cross-Site Micro-Label Handling

Site-local micro labels remain strictly local aliases.

A cross-site transfer requires the object's canonical UUID or another approved globally identifying label/artifact.

The sending workflow SHOULD instruct the operator to:

1. apply or verify the full UUID/global label;
2. remove, cover, or otherwise disable the source site's micro label before normal destination-site use.

The software MAY require operator confirmation.

The software cannot prove that the physical source label was actually disabled.

---

# 13. Accepted Human-Factor Residual Risk

The architecture explicitly accepts the possibility that an operator fails to disable the source-site micro label before transfer.

For an actual wrong-object resolution to occur, several independent conditions must align.

A representative failure chain is:

1. the object is small enough that a site-local micro tag was used;
2. the object is later selected for cross-site transfer;
3. the sending operator fails to remove, cover, or disable the source micro tag;
4. the receiving operator does not process the item through the expected incoming-transfer workflow;
5. the receiving operator scans the source micro tag instead of the required UUID/global tag;
6. the destination site happens to have independently allocated the exact same six-digit local alias to another local entity;
7. the current terminal/workflow accepts an ordinary local-item scan in that context.

This is intentionally treated as a **Swiss-cheese human-factor failure**, not as a protocol problem requiring additional distributed identity machinery.

---

# 14. Incoming Transfer Mode Must Reject Micro Labels

The normal incoming-transfer workflow MUST require globally identifying transfer input.

A bare site-local micro label MUST NOT be accepted as sufficient identity in incoming-transfer mode.

If the operator scans:

```text
I428731
```

while the workflow is expecting a globally identified incoming object, the workflow SHOULD reject it with a message such as:

```text
LOCAL MICRO LABEL NOT VALID FOR INCOMING TRANSFER
SCAN FULL UUID / GLOBAL LABEL
```

This prevents the intended transfer workflow from accidentally resolving the alias in the destination site's local namespace.

---

# 15. Ordinary Local Scan Behavior Remains Local

Outside incoming-transfer mode, an ordinary scan of:

```text
I428731
```

continues to mean:

```text
resolve local alias 428731 in the current site
```

The scanner cannot infer that the physical label originated at another site because the micro-label payload intentionally contains no issuer information.

Therefore, if a foreign micro label survives transfer and collides with a destination alias, ordinary local scanning may resolve to the destination-local entity.

This limitation is known and accepted.

---

# 16. Why This Risk Is Accepted

The residual risk is accepted because:

- micro labels are expected to be uncommon;
- they are used mainly for very small objects;
- cross-site transfer of those objects is a minority workflow;
- transfer procedures require a full UUID/global label;
- source-label disablement is explicitly instructed;
- incoming-transfer mode rejects micro-label identity;
- destination alias collision is itself unlikely in a sparse random six-digit namespace;
- a random scan outside the transfer workflow does not by itself complete the transfer;
- the actual transfer remains outstanding until explicit reception acknowledgement occurs.

The software SHOULD NOT grow additional issuer-discovery or foreign-micro-label protocols solely to eliminate this edge case.

---

# 17. Transfer State Provides Detection

A cross-site transfer is not considered complete merely because some barcode was scanned at the destination.

Completion requires explicit reception acknowledgement of the actual transfer/object identity.

Therefore, if an item physically arrives but is never correctly processed:

```text
transfer status remains outstanding
```

The sending or receiving organization can later detect that the transfer was never acknowledged.

This provides an operational recovery path.

Typical later investigation may reveal:

```text
sent by OpenLab
not acknowledged by XRI
physical shipment known to have arrived
item located manually
correct UUID scanned
transfer completed/reconciled
```

The residual micro-label mistake therefore does not silently finalize an incorrect cross-site transfer.

---

# 18. Wrong Local Resolution Does Not Imply Automatic Mutation

Merely resolving an ordinary local scan to some destination-local object MUST NOT itself perform an unrelated destructive inventory mutation.

A scan produces an entity resolution result for the currently active workflow.

The workflow still determines what operation, if any, is performed.

This further limits the consequence of accidentally scanning a surviving foreign micro label outside transfer mode.

Implementations SHOULD preserve this principle:

> **Scanning identifies input; workflow state determines action.**

---

# 19. Operator Compliance Boundary

inventoryzing MAY:

- display warnings;
- require acknowledgement;
- print transfer instructions;
- require a full UUID label before transfer;
- reject micro labels in incoming-transfer mode.

inventoryzing cannot guarantee:

- that a human physically removes or covers a source label;
- that a human enters the correct workflow;
- that a human scans the intended label.

These are operator-process responsibilities.

The specification MUST NOT claim physical compliance guarantees that the software cannot observe.

---

# 20. Deployment Policy Option

Deployments with a lower tolerance for this residual risk MAY adopt a stricter operational policy such as:

```text
assets expected to move between sites MUST NOT use micro labels as their only routine physical identifier
```

or:

```text
micro labels forbidden on transferable asset classes
```

This is a deployment policy, not a core architectural requirement.

---

# 21. Revised Acceptance Tests

The specification readiness test suite SHOULD now include the following.

## Command receipt tests

1. successful duplicate retries in an open epoch return the original result;
2. a receipt cannot be deleted while its epoch remains executable;
3. after epoch closure and receipt deletion, retry fails with `COMMAND_EPOCH_EXPIRED`;
4. an expired command is never re-executed as new work;
5. offline stale commands rejected due to epoch expiry enter reconciliation state.

## Micro-ID allocation tests

1. allocation uses platform/OS secure randomness;
2. no deterministic database-backed seed is restored with the ordinary backup;
3. database uniqueness prevents concurrent duplicate aliases;
4. conflicting inserts retry;
5. retry exhaustion fails explicitly;
6. known aliases remain non-reassignable during normal retained-history operation.

## Transfer-label tests

1. incoming-transfer mode rejects `I######`;
2. incoming-transfer mode accepts the approved global identifier format;
3. transfer cannot be acknowledged merely by an ordinary local micro-label resolution;
4. unacknowledged transfers remain outstanding;
5. UI instructs the sender to disable the source micro label;
6. software records operator confirmation where configured;
7. ordinary local scan mode continues to resolve local aliases locally.

---

# 22. Final Residual-Risk Statement

The following case is explicitly accepted:

> A source-site micro label survives cross-site transfer, the recipient bypasses incoming-transfer mode, scans the wrong physical label in an ordinary local workflow, and the six-digit value happens to collide with a destination-local alias.

The system does not attempt to cryptographically or globally disambiguate that scan.

The mitigation is operational:

- full UUID/global transfer labels;
- source micro-label disablement;
- transfer-mode rejection of micro labels;
- explicit receipt acknowledgement;
- outstanding-transfer visibility;
- manual reconciliation if necessary.

This risk is considered sufficiently small and sufficiently recoverable that additional protocol complexity is not justified.

---

# 23. Specification Freeze Recommendation

With these corrections recorded, the foundational architecture SHOULD be considered ready for local implementation.

Further adversarial review SHOULD focus on implemented behavior and tests rather than adding new abstract distributed mechanisms.

Priority test targets include:

```text
concurrent structural mutations
duplicate command execution
lost responses
command-epoch closure
micro-ID conflict allocation
backup/restore behavior
strict payload parsing
atomic history/outbox/receipt persistence
scanner workflow state
```

Federation, replication snapshotting, incarnation recovery, and corporate management remain later implementation gates.

No further architecture expansion is required for the personal MVP.
