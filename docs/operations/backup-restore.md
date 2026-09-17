# Local Backup and Restore

This milestone stores canonical inventory, history, aliases, accounts, and receipts
in PostgreSQL. Blob storage and protected corporate identity keys do not exist yet;
add them to the backup contract when those features are implemented. A database
backup alone must not later be advertised as a complete distributed-site backup.

## Backup

Choose a protected destination outside this workspace and outside the live Docker
volume. The repository does not select a destination, create a schedule, or install
failure alerting. Those operator decisions must be made before relying on scheduled
backups.

Run the guarded backup script from the workspace root:

```powershell
.\scripts\backup-database.ps1 `
		-DestinationDirectory 'D:\protected\inventoryzing-backups' `
		-RetentionDays 30 `
		-ArchiveApplicationImage
```

The destination is an example, not a configured default. The script refuses paths
inside the workspace. By default it resolves the running `db` service in the main
Compose project and creates:

- a timestamped PostgreSQL custom-format dump;
- a JSON manifest with SHA-256, byte size, PostgreSQL image/version, Alembic
	revision, application image ID, and observed table counts;
- optionally, one content-addressed application-image archive when
	`-ArchiveApplicationImage` is supplied.

`-RetentionDays 0` is the default and deletes nothing. A positive value removes
only expired dump/manifest pairs for the selected database after a new backup has
completed. Encrypt off-machine copies and restrict access: dumps contain password
hashes, sessions, and personal inventory information. Protect `.env` separately;
the application role can be reprovisioned by the migration command.

The script writes the dump inside PostgreSQL's container, validates its archive
catalog, and copies it out without routing binary bytes through Windows PowerShell
5.1. It exits nonzero on failure, removes partial output, and does not stop the
coordinator or database. Custom-format dumps are transactionally consistent while
normal inventory writes continue. Manifest table counts are a timestamped
observation and may differ from the dump during concurrent writes; the restored
database itself is the verification baseline.

For a non-Compose or test database, pass the container and database explicitly:

```powershell
.\scripts\backup-database.ps1 `
		-DestinationDirectory 'D:\protected\inventoryzing-backups' `
		-ContainerName inventoryzing-test-db `
		-Database inventoryzing_test
```

## Isolated Restore Verification

Verify a dump without exposing or replacing the live site:

```powershell
.\scripts\verify-database-restore.ps1 `
		-DumpPath 'D:\protected\inventoryzing-backups\inventoryzing-20260915T010203000Z.dump'
```

The verifier requires the matching manifest, checks the dump SHA-256, and creates
a fresh randomly named `inventoryzing-restore-*` Compose project. It refuses to
reuse any existing container, network, or volume with that project identity. The
restore stack:

- publishes no host ports and starts no coordinator or hardware agent;
- uses random temporary database/runtime passwords and the fixed database name
	`inventoryzing_restore_test`;
- restores with `--no-owner --no-privileges --exit-on-error`;
- records counts across identity, accounts/RBAC, objects/placements, taxonomy,
	identifiers, epochs/receipts, events/subjects, outbox, and sessions;
- runs the selected application image's migrations and rejects unexpected changes
	to those restored counts;
- truncates restored browser sessions and proves zero remain;
- writes a timestamped `*.restore-verified-*.json` report beside the dump; and
- removes its containers, network, and volume, including after a failure.

Use `-ApplicationImage` or `-PostgresImage` to test an intentional target version.
The report records source and restore image/version information so an upgrade drill
does not obscure which artifacts were exercised.

## Current Evidence and Limits

On 2026-09-15 the scripts completed a disposable `inventoryzing_test` drill from
PostgreSQL 18.3 at Alembic revision `0003_classification`: all current table-family
counts survived restore and migration, one deliberately seeded browser session was
restored then invalidated, and all temporary Docker resources were removed. This
validates the tooling against test data only. It is not evidence that a live-site
backup has been copied to protected storage or restored successfully.

Before storing irreplaceable data, configure the real protected destination,
retention, schedule, and failure notification, archive the corresponding
application image, then exercise a live dump in this isolated verifier. Do not run
application migrations before restoring a schema dump into a production recovery
database. A promoted recovery also needs newly controlled secrets and operator
review before network exposure.

There is no event-stream snapshot/export protocol in this milestone. Restored
clones remain isolated because replication/enrollment and clone/incarnation fencing
are not implemented. Accepted lost-allocation and stale-label risks remain
unchanged. Never restore a drill over the live database or delete the live volume.