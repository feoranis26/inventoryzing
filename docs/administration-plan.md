# Administration implementation plan

Agreed scope: site settings, account/role administration, principal management,
then credential bindings and checkout/return. The architecture handoff and ordered
amendments remain authoritative, especially Amendments 007 and 008.

## 1. Site settings and branding

- Provide an authorized administration page to edit the site display name and
  upload, preview, replace, or remove an optional site logo.
- Preserve the site's stable identity when changing its presentation.
- Store the logo as site-owned data. Site administration must not depend on the
  labeling module or any hardware agent.
- Initially accept PNG and JPEG uploads, validate decoded dimensions and size,
  normalize to a safe image representation, and retain transparency for PNGs.
  SVG upload support can follow with an explicit sanitization policy.
- Show the configured logo alongside the site name in the application.
- Keep the label editor's existing `site_branding` element as a dynamic reference:
  at preview/print time use the current site logo if present, otherwise the current
  site name. Do not copy a logo asset into each template or template revision.
- Fit the logo proportionally inside the element's existing bounds; do not resize
  the label. Respect horizontal and vertical alignment. Composite transparency on
  white and use the labeling module's monochrome conversion for printer output.
- Keep textual site-name placeholders available independently of the branding
  element. Inventoryzing branding remains separate.
- Invalidate browser branding/preview caches after updates. The next render must
  use the new name/logo; already-rendered requests need not be rewritten.
- Use explicit settings permissions, optimistic concurrency, retry-safe mutations,
  and audit events. Include logo data in the normal backup/restore coverage.

Acceptance: upload/replacement/removal and name changes appear in the UI, SVG
preview, PNG preview, and printer rendering; missing logos fall back to the site
name; aspect ratios and transparency are preserved; malformed or oversized uploads
are rejected; unauthorized changes fail; concurrent edits cannot silently overwrite
each other; installing labeling is not required to manage the site.

## 2. Accounts and roles

- List, create, edit, and disable human accounts; provide password change/reset
  and session revocation.
- Manage reusable roles and their atomic permissions, including registered module
  permissions. Assign multiple roles and show effective permissions.
- Use the same permission evaluation for human and service identities; keep daemon
  enrollment, routing, and device configuration in the owning modules.
- Audit administrative mutations without recording passwords or reusable secrets.
- Protect the last usable administrator without relying on hardcoded role names.
- Recheck operator authorization for active scanner operations after account
  disablement, session revocation, or role changes. Starting a workflow must not
  grant lasting authority independent of the current account/session.
- Defer optional role inheritance, location-scoped permissions, and SSO initially.

## 3. Principals

- List, create, edit, and archive people, teams, organizations, projects, and sites.
- Optionally associate a person principal with a login account; principals do not
  require accounts and are not permission groups.
- Expose object ownership, assignment, and custody independently of physical
  placement. Preserve historical references when archiving principals.

## 4. Credential bindings and checkout/return

- Add explicit authorized binding of scanned identifiers to principals/accounts.
  Unknown credentials must not automatically create accounts.
- Distinguish identification from authentication strength.
- Implement core checkout/return and the previously planned scanner workflows on
  top of principals and permissions, including the distinction between acting on
  one's own behalf and acting for another principal.
- Keep scan delivery transient, consistent with the accepted scanner behavior;
  committed inventory/administrative operations retain their normal audit history.

## Implemented first slice

The first three sections are now implemented for the MVP:

- Site settings have versioned name and logo APIs, a settings screen, PNG/JPEG
  validation and normalization, and dynamic use by the labeling module.
- Accounts can be created, linked to a person principal, assigned multiple roles,
  disabled, and have passwords reset. Role changes and account changes revoke
  affected human sessions. The last account with the required administrative
  capabilities cannot be made unusable.
- Roles are editable bundles of registered atomic permissions. The access screen
  shows their assigned accounts and their effective permissions.
- Principals are independently managed records for people, teams, organizations,
  projects, and sites. They may be archived when they have no linked account.
- A running scanner workflow verifies the terminal operator's current permission
  before each scan is applied, so it cannot continue after access is revoked.

The next unimplemented slice is credential binding and checkout/return. Role
inheritance, scoped permissions, SSO, service-account administration, and SVG logo
uploads remain intentionally deferred.

## Existing implementation to extend

- `server/coordinator/src/inventoryzing/auth.py`: human sessions, service credentials,
  direct role permission evaluation, and bootstrap identities.
- `db/migrations/versions/0001_core.sql`: site name, principals, account links,
  roles, role permissions, account roles, and object principal relationships.
- `server/coordinator/src/inventoryzing/modules/labeling.py`: dynamic site logo or
  site-name branding and existing inventoryzing-logo SVG/bitmap rendering.
- `server/coordinator/src/inventoryzing/modules/scanning.py`: terminal-bound active
  workflows with per-scan operator authorization revalidation.
