# inventoryzing Architecture Amendment 007

**Status:** Accepted architectural amendment  
**Scope:** User-session authority, service principals, role assignment, trusted executable-content authorship, metadata-validation freshness, and audit redaction  
**Applies to:** Original architecture handoff + Amendments 001–006  
**Intent:** Resolve the second adversarial review of the metadata/module architecture without adding an unnecessary group abstraction

---

# 1. Purpose

This amendment resolves the remaining findings from the second adversarial review of Amendments 005 and 006.

It clarifies:

1. how trusted browser modules authenticate and authorize requests;
2. how non-interactive integrations authenticate;
3. that the existing role system already fulfills reusable permission-grouping needs;
4. the distinction between trusted installed code and trusted authors of executable stored content;
5. freshness rules for cached metadata validation;
6. permission filtering for raw metadata contained in audit/history events.

No new major subsystem is introduced.

Where this amendment conflicts with earlier wording, **this amendment takes precedence**.

---

# 2. No Separate Group Abstraction

inventoryzing MUST NOT introduce a separate user-group layer merely to bundle permissions.

The existing role system already fulfills that purpose.

The intended authorization structure is:

```text
User Account
    ↓ assigned roles
Role
    ↓ permissions
```

Roles MAY inherit from other roles as already specified.

Example:

```text
User: Alp

Assigned role:
    OpenLab Monitor

OpenLab Monitor:
    checkout.create
    checkout.close
    label.print
    item.create
    item.edit
```

There is no additional required:

```text
User
    ↓
Group
    ↓
Role
```

layer.

---

# 3. Roles Are Reusable Permission Bundles

A role is the reusable administrative unit for assigning a set of permissions to multiple users.

Example:

```text
Role:
    OpenLab Monitor
```

may be assigned to every authorized monitor.

Administrators therefore do not need to manually assign each permission to every user.

The system MAY support direct permission assignment in the future if a concrete need arises, but ordinary administration SHOULD use roles.

---

# 4. Role Assignment to Accounts

User accounts MAY have one or more roles.

Effective permissions are calculated from:

```text
directly assigned roles
+
inherited roles
```

subject to any future explicit scope rules.

Example:

```text
Alp:
    roles:
        OpenLab Monitor
        Inventory Maintainer
```

The effective permission set is the union of permissions granted through those roles.

The authorization engine remains authoritative.

---

# 5. Browser Modules Use the Current User Session

Trusted frontend modules MUST normally act through the currently authenticated user's session.

They MUST NOT require embedded privileged service credentials merely because they are modules.

Example:

```text
OpenLab frontend module
    ↓
current browser session for Alp
    ↓
POST /checkout
    ↓
backend checks Alp's effective permissions
```

If the operation requires:

```text
checkout.create
```

then Alp must possess that permission through one or more roles.

The fact that trusted module code initiated the request does not grant additional authority.

---

# 6. Module-Specific Permissions

Modules MAY register their own permissions.

Examples:

```text
manufacturing.job.create
manufacturing.job.cancel
manufacturing.template.author
openlab.return.request
openlab.return.approve
calibration.override
```

Modules MAY also register recommended/default roles containing those permissions.

Example:

```text
Role:
    OpenLab Monitor

Permissions:
    openlab.checkout.create
    openlab.checkout.close
    openlab.return.approve
    label.print
```

Administrators assign roles to users using the ordinary role-management system.

No separate module-specific account hierarchy is required.

---

# 7. Frontend Modules Must Not Contain Privileged Shared Secrets

A browser-delivered frontend bundle MUST NOT contain reusable privileged credentials such as:

```text
administrator API key
service-account secret
private signing key
unrestricted bearer token
```

that grant authority independent of the current authenticated user.

Any credential delivered to an ordinary browser user must be assumed discoverable by that user.

Therefore:

> **Browser module authority comes from the user's authenticated session or an explicitly user-scoped delegation, not from hidden privileged module credentials.**

---

# 8. User-Scoped Delegation

A future module MAY use a short-lived delegated token instead of the browser session cookie directly.

If so, the delegation MUST:

- identify the user or originating session;
- be scoped to explicit permissions/actions;
- expire;
- never exceed the authority of the user/session that obtained it unless a separate privileged approval flow explicitly grants such elevation.

This is optional.

The MVP MAY simply use the ordinary user session.

---

# 9. Non-Interactive Integrations Use Service Principals

Non-interactive software requires a distinct authenticated identity.

inventoryzing SHOULD support **service principals** or equivalent machine accounts.

Examples include:

```text
printer daemon
scanner daemon
slicer extension
regional replication process
corporate management agent
automated import tool
CI/integration process
```

A service principal is authenticated separately from a human user account.

---

# 10. Service Principal Credentials

Service principals MAY authenticate using mechanisms such as:

```text
API key
client certificate
mTLS machine identity
signed token
other explicitly supported machine credential
```

The exact credential mechanism is deployment- and subsystem-specific.

A credential proves the identity of the service principal.

It does **not** itself grant unrestricted authority.

---

# 11. Service Principals Use the Same Permission Model

Service principals MUST be subject to ordinary authorization.

They MAY be assigned roles just like user accounts.

Conceptually:

```text
Service Principal:
    OpenLab Printer Agent

Role:
    Printer Agent

Permissions:
    label.job.read
    label.job.claim
    label.job.complete
```

Another example:

```text
Service Principal:
    PrusaSlicer Integration

Role:
    Manufacturing Integration

Permissions:
    manufacturing.job.create
    attachment.upload
    stock.consume
```

Possession of an API key alone does not bypass these checks.

---

# 12. Scanner and Printer Agent Examples

The local hardware agent discussed in the main architecture SHOULD authenticate as an appropriate service principal when performing autonomous server operations.

Possible permissions include:

```text
scanner.event.submit
label.job.read
label.job.claim
label.job.complete
device.status.report
```

A scanner daemon SHOULD NOT need:

```text
item.delete
role.manage
peer.manage
```

unless a specific deployment explicitly grants them.

This preserves least privilege.

---

# 13. Slicer Extension Example

A slicer integration MAY authenticate using a service principal such as:

```text
PrusaSlicer Workstation #4
```

or a user-bound integration if appropriate.

Its role may grant:

```text
manufacturing.job.create
manufacturing.job.update
attachment.upload
material.read
stock.consume
```

The slicer integration MUST remain subject to the same backend validation and permission checks as any other API client.

---

# 14. Trusted Installed Code vs. Trusted Content Author

Amendment 006 established:

```text
installed module code = trusted executable code
```

This does NOT imply:

```text
any user who can write module metadata = trusted code author
```

These are separate trust decisions.

A module may intentionally interpret stored content as executable HTML, JavaScript, template source, expressions, or another active language.

If so, authorship of that content MUST be separately authorized.

---

# 15. Executable Stored Content Must Be Explicit

A metadata/property schema that is intentionally interpreted as executable or active content MUST explicitly declare that semantic.

Conceptually:

```text
property:
    dashboard.custom_template

content_semantics:
    executable_template
```

Ordinary metadata MUST default to:

```text
content_semantics:
    data
```

The core MUST NOT infer executable semantics merely from a string containing HTML or JavaScript syntax.

---

# 16. Executable-Content Author Permission

Writing executable stored content MUST require an explicit permission appropriate to the owning module.

Example:

```text
dashboard.template.author
```

or:

```text
manufacturing.custom_ui.author
```

Possessing a generic permission such as:

```text
metadata.raw.write
```

MUST NOT automatically grant the ability to author content that a trusted module will execute in other users' browsers.

Likewise, ordinary module data-edit permissions MUST NOT imply executable-content authorship.

---

# 17. Module Ownership Does Not Establish Author Trust

Namespace ownership alone is insufficient.

For example:

```text
dashboard.*
```

may be owned by the dashboard module.

That establishes which module interprets the data.

It does not mean every user allowed to edit some `dashboard.*` data is trusted to provide browser-executable code.

Executable authoring permission must remain explicit.

---

# 18. Ordinary User Input Must Be Treated as Data

If a trusted module accepts ordinary user-authored content such as:

```text
notes
descriptions
Markdown
labels
comments
```

the module MUST treat those inputs as untrusted data unless the user has the explicit executable-content author permission.

If the module renders such data into HTML, it is responsible for appropriate:

```text
escaping
sanitization
restricted parsing
```

according to its chosen format.

---

# 19. Deliberately Trusted Templates Are Allowed

A module MAY intentionally support administrator-authored or developer-authored active templates.

Example:

```text
dashboard.custom_template
```

Such a feature is valid.

The rule is not:

> Stored executable content is forbidden.

The rule is:

> **Stored executable content requires an explicit trusted-author boundary and must not arise accidentally from ordinary metadata write access.**

---

# 20. Metadata Validation Results Are Cached Assertions

A cached metadata validation result is not timeless state.

It is an assertion that:

> a specific effective value was validated against a specific metadata schema revision.

Therefore every cached validation result MUST identify both:

```text
schema revision
effective value/source version
```

or an equivalent freshness identity.

---

# 21. Validation Cache Identity

A conceptual validation record SHOULD contain:

```text
property_definition_id
validated_schema_revision

target_entity_id

effective_value_source
effective_value_version

validation_result
validated_at
```

The exact storage format is implementation-defined.

The important invariant is that validation freshness depends on both:

```text
current schema
current effective value
```

---

# 22. Direct Metadata Value Validation

For a direct entity metadata value, the validation cache MAY reference:

```text
metadata_value_id
metadata_value_version
```

Example:

```text
Property:
    manufacturing.material_used

Schema revision:
    7

Value row:
    UUID V
    version 12
```

A cached validation result for:

```text
schema revision 7
value version 12
```

is stale if either number changes.

---

# 23. Metadata Values SHOULD Have Value Versions

Metadata value rows SHOULD have their own lightweight version or immutable revision identity.

This allows precise dependency checking without relying solely on the enclosing entity's version.

A metadata mutation still increments the target entity version as required by Amendment 005.

The metadata value version exists additionally for:

- validation freshness;
- inherited-value dependency tracking;
- effective-value caches;
- fine-grained indexing.

---

# 24. Inherited Effective Value Validation

If an entity inherits a metadata value from a type/default provider, validation freshness MUST depend on the source value.

Example:

```text
Object A
    no override

ObjectType T
    electrical.voltage_rating = 60 V
```

Validation of Object A's effective value may record:

```text
source_entity = ObjectType T
source_metadata_value = V
source_value_version = 3
```

If the type default changes:

```text
60 V -> 100 V
```

and the source value becomes version 4, the prior validation is stale even if Object A's own entity version did not change.

---

# 25. Current Schema Determines Validation Freshness

Suppose a cached result contains:

```text
validated_schema_revision = 16
result = VALID
```

while the current active schema is:

```text
revision = 17
```

The effective validation state is:

```text
STALE
```

not:

```text
VALID
```

No mass update of every cache row is required merely to express this.

The revision mismatch itself is sufficient.

---

# 26. Stale Validators Must Not Overwrite Newer Results

Asynchronous/background validation jobs MUST use compare-and-set or equivalent conditional persistence.

A validation result may be committed only if the assumptions used during validation are still current.

Conceptually:

```text
write result only if:

current property schema revision
    == validated schema revision

AND

current effective value/source version
    == validated effective value/source version
```

If either assumption changed, the result MUST be discarded or marked stale.

---

# 27. Validation Race Example

Scenario:

```text
Validator A starts:
    schema revision 17
    value version 8

Schema revision 18 activates.

Validator B validates:
    schema revision 18
    value version 8
    result INVALID

Validator A finishes late:
    result VALID
```

Validator A MUST NOT overwrite the revision-18 result.

Its conditional write fails because:

```text
current schema revision != 17
```

A stale asynchronous result therefore cannot resurrect validity under an obsolete schema.

---

# 28. Value Change Race Example

Scenario:

```text
Validator A starts:
    schema revision 18
    value version 8

Metadata changes:
    value version becomes 9

Validator A finishes.
```

Its result MUST NOT be considered current because:

```text
validated value version 8
!=
current value version 9
```

A new validation may be scheduled or performed on demand.

---

# 29. Read-Time Validation Behavior

The MVP MAY use a simple read-time freshness rule.

Conceptually:

```text
if cached validation exists
AND cached schema revision == current schema revision
AND cached effective value identity == current effective value identity:
    use cached result
else:
    validation is stale
    validate synchronously or schedule validation according to policy
```

No dedicated distributed validation service is required.

---

# 30. Bulk Revalidation

After a schema revision, the owning module MAY schedule asynchronous bulk revalidation.

This is useful for:

- discovering invalid legacy values;
- updating search indexes;
- generating repair queues.

However, asynchronous bulk validation MUST still obey the freshness rules in this amendment.

Old jobs MUST NOT overwrite validation results for newer schemas or values.

---

# 31. Search and Index Freshness

Any materialized search/index field representing metadata validity MUST identify or depend on the schema revision and effective value identity that produced it.

A search index MUST NOT continue reporting:

```text
VALID
```

after a schema revision invalidates the cached result.

Implementations MAY achieve this through:

- revision-aware index entries;
- explicit invalidation;
- background reindex;
- query-time comparison.

---

# 32. Validation Status Is Not Canonical Metadata

Validation status is derived state.

It MUST NOT be treated as an ordinary authoritatively writable metadata property.

Users/modules do not set:

```text
validation_status = VALID
```

as a normal metadata mutation.

The validation engine derives that status from:

```text
effective value
+
active schema
```

---

# 33. Raw Metadata Security Extends to Audit History

Permissions restricting raw metadata visibility MUST also apply to audit/history APIs.

An ordinary user who lacks:

```text
metadata.raw.read
```

MUST NOT gain equivalent raw access merely by viewing event history.

---

# 34. Audit Records Remain Complete Internally

The canonical audit record MAY retain complete event information required for:

- debugging;
- compliance;
- repair;
- replication;
- forensic analysis.

The server-side API representation MUST filter/redact the record according to the requesting principal's permissions.

The stored audit event itself need not be destructively stripped.

---

# 35. Permission-Aware Audit Rendering

A user authorized to see the entity but not raw metadata MAY receive:

```text
Manufacturing metadata changed
Actor: ...
Time: ...
```

without receiving:

```text
property key
raw old value
raw new value
internal module payload
```

A user with:

```text
metadata.raw.read
```

may receive the detailed representation where otherwise authorized.

---

# 36. Audit Filtering Must Be Server-Side

Raw metadata redaction MUST occur before unauthorized data is returned to the client.

The frontend MUST NOT receive the complete payload and merely hide fields visually.

This is a backend authorization requirement.

---

# 37. Service Principals and Audit

Actions performed by service principals MUST be auditable using the service principal's identity.

Example:

```text
Actor:
    PrusaSlicer Workstation #4

Action:
    ManufacturingJobCreated
```

If a service principal is acting on behalf of a known user, the event MAY additionally record that delegation context.

The service identity itself must remain visible for accountability.

---

# 38. Role Model Summary

The authorization model after this amendment is:

```text
Human:
    User Account
        ↓ assigned Roles
    Roles
        ↓ Permissions

Machine:
    Service Principal
        ↓ assigned Roles
    Roles
        ↓ Permissions
```

Roles are therefore reusable permission bundles for both human and machine identities where appropriate.

No separate generic group abstraction is required.

---

# 39. Browser Authority Summary

Trusted frontend code executes inside the inventoryzing application environment.

Its normal request authority is:

```text
current user session
```

or an explicitly scoped user delegation.

It MUST NOT rely on hidden privileged service credentials embedded in browser-delivered code.

Backend authorization remains authoritative.

---

# 40. Service Credential Summary

Service credentials are appropriate for non-interactive clients such as:

```text
hardware agents
slicer extensions
replication processes
automation
management agents
```

A service credential authenticates a service principal.

Permissions still come from the ordinary role/permission system.

---

# 41. Executable Content Summary

The trusted-code boundary is:

```text
installed module code
    = trusted executable code

ordinary metadata
    = data

explicit active-template metadata
    = executable only when:
        owning module intentionally interprets it
        AND author has explicit executable-author permission
```

Generic raw metadata write permission is insufficient to cross this boundary.

---

# 42. Validation Summary

A validation result is valid only for:

```text
one property definition
one schema revision
one effective value/source identity
```

If any dependency changes, the cached result is stale.

Late validation jobs MUST NOT overwrite newer validation state.

---

# 43. Acceptance Tests

The authorization/module/metadata implementation SHOULD add at least the following tests.

## Roles and browser sessions

1. multiple users can be assigned the same role without per-user permission duplication;
2. one user may hold multiple roles;
3. role inheritance contributes effective permissions correctly;
4. no separate group object is required for ordinary reusable permission assignment;
5. frontend module requests execute using the current user's authority;
6. a frontend module cannot gain a permission the current user lacks merely because the module is trusted;
7. a browser-delivered module contains no reusable privileged service secret in the standard architecture.

## Service principals

8. a service principal can authenticate independently of a user;
9. a service principal may be assigned a role;
10. service-principal API requests are denied when required permissions are absent;
11. printer/scanner agent roles can be restricted to hardware-related permissions;
12. slicer integration permissions can be independently scoped;
13. actions performed by service principals identify the service principal in audit history.

## Executable stored content

14. ordinary metadata writers cannot author executable module content without the explicit author capability;
15. namespace write permission alone does not grant executable-content authoring;
16. active-template properties must explicitly declare executable semantics;
17. ordinary user-authored strings remain data;
18. authorized trusted-template authors can persist content intentionally interpreted by the owning module.

## Validation freshness

19. cached `VALID` from an older schema revision is treated as stale after schema activation;
20. a direct metadata value change makes validation against the old value version stale;
21. an inherited type/default change invalidates child effective-value validation without incrementing every child entity version;
22. a late validation job cannot overwrite a result for a newer schema;
23. a late validation job cannot overwrite a result for a newer value;
24. search/index validity state does not outlive the schema/value dependency that produced it.

## Audit visibility

25. ordinary entity-view access does not expose raw metadata through event history;
26. `metadata.raw.read` permits detailed raw metadata history where otherwise authorized;
27. redaction is performed server-side;
28. canonical internal audit records remain complete even when client representations are redacted.

---

# 44. Final Clarification

No new group abstraction is introduced.

Roles already fulfill the reusable assignment purpose:

```text
user/service principal
    -> role(s)
    -> permissions
```

Trusted browser modules use the user's authority.

Non-interactive software uses service principals and credentials.

Both remain subject to ordinary backend authorization.

Trusted installed module code does not imply trust in every user who can edit that module's data.

Executable stored content requires explicit author permission.

Metadata validation caches are revision- and value-aware derived state, not permanent truth.

Raw metadata authorization applies consistently across normal entity APIs and audit/history APIs.
