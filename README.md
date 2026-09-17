# inventoryzing

Basic inventory management system

## Get running

Install Docker Desktop with Linux containers. The container includes the web app; you do not need Python or Node.js on your computer.

From the repository root in PowerShell:

```powershell
./scripts/setup-local.ps1
docker compose up --build -d
docker compose run --rm migrate python -m inventoryzing.cli bootstrap --site-name "My workshop" --login admin
```

Enter a password of at least 12 characters when prompted. There is no default password. Bootstrap is only needed for a new installation and refuses an already initialized database.

Open [http://localhost:8088](http://localhost:8088) and sign in. Use `localhost` as shown so the browser address matches the configured origin.

The setup script creates `.env` without overwriting an existing file. Keep it private. On Linux or macOS, copy `.env.example` to `.env` instead and replace both password values with independently generated random hex secrets of at least 24 characters each, then run the Docker commands above.

If a port is occupied, change `IZ_HTTP_PORT` or `IZ_DB_PORT` in `.env` before starting. Use the new HTTP port in your browser address.

## Start using your inventory

1. Open **Object types** and create types for the things you track, such as tools or storage boxes. Add properties and defaults when useful.
2. Open **Inventory** to add objects and choose where they belong. Locations and containers can hold other objects.
3. Use **Quick create** when adding several items. Choose a type and destination, fill in the details, and add each item.
4. Open an object's record to edit its details, move it, inspect its contents, or view its history.
5. Use search, type filters, and tags to find items. Expand tree branches to browse storage locations; use **All objects** for a flat list.

For step-by-step guidance on tags, properties, stock, scanning, labels, and administration, see the [web app guide](web/main-ui/README.md).

## Labels and scanners

You can download an object's QR label or use browser printing. Direct Brother QL-820NWB printing and Zebra DS22 scanning need an optional device agent. Follow the [device setup guide](agents/windows/inventoryzing-agent/README.md) to connect them.

Choose a label template that matches the installed roll. Preview it with an inventory object before printing. Local identifiers such as `I000042` belong to this installation.

## Stop, restart, and troubleshoot

Run these commands from the repository root:

```powershell
docker compose ps
docker compose logs --tail 50 coordinator
docker compose stop
docker compose up -d
```

If startup fails, inspect migration and database logs with `docker compose logs --tail 50 migrate db`. If sign-in or saving fails, check that you are using `http://localhost:<your HTTP port>`.

Data survives container restarts in the `inventoryzing_postgres-data` Docker volume. **Do not run `docker compose down -v` on an installation whose inventory you want to keep.**

Back up before updating, then rebuild and start with `docker compose up --build -d`. Database migrations run before the app starts.

## Backups and access

Set up and verify [database backups and restores](docs/operations/backup-restore.md) before relying on the installation. Backup scheduling, retention, and failure notifications need to be configured separately.

The supplied Compose setup serves HTTP on this computer only. Network deployment requires HTTPS and changes to the coordinator's `IZ_PUBLIC_ORIGIN`, `IZ_ALLOWED_HOSTS`, and `IZ_SECURE_COOKIES` settings in Compose; adding them to `.env` alone does not override the supplied configuration. Keep the database private.

Keep the app connected while making changes. It does not provide an offline work queue. If a save fails, use its retry action; if you closed the tab, inspect the object and its history before repeating the action.

## Further reference

- [Web app guide](web/main-ui/README.md)
- [Printer and scanner setup](agents/windows/inventoryzing-agent/README.md)
- [Development and verification](docs/development.md)
