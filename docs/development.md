# Development and verification

Run commands from the repository root. For installation and everyday use, see the [user guide](../README.md).

## Develop

Use Python 3.13 and `uv`; never install dependencies into a shared interpreter.

```powershell
$env:UV_PROJECT_ENVIRONMENT = "$PWD/.venv"
uv sync --project server/coordinator --python 3.13 --locked
npm.cmd ci --prefix web/main-ui
```

For host development, configure `IZ_DATABASE_URL` to the local database, using
`127.0.0.1` rather than `localhost` on Windows. Docker binds the DB to IPv4; IPv6
localhost fallback can cause long connection delays. Run migrations with the
owner URL and `IZ_RUNTIME_PASSWORD`, then use the restricted account for the API.

```powershell
$env:IZ_PUBLIC_ORIGIN = 'http://127.0.0.1:5178'
$env:IZ_SECURE_COOKIES = 'false'
./.venv/Scripts/uvicorn.exe inventoryzing.app:create_app --factory --host 127.0.0.1 --port 8088 --reload
```

In another terminal, `npm.cmd run --prefix web/main-ui dev`. Open
http://127.0.0.1:5178. Vite proxies API requests to port 8088. Stop the Compose
coordinator first or configure different ports. No sibling application's process,
environment, hardware service, or Docker volume is needed.

To serve the built frontend directly, set `IZ_STATIC_DIR` to the absolute path of
`web/main-ui/dist` and use the coordinator's own origin instead of the Vite origin.

## Verify

Integration tests require a **dedicated, disposable** PostgreSQL database whose
name ends in `_test`. The fixtures truncate test data. Never point them at inventory.

```powershell
docker run -d --name inventoryzing-test-db -p 127.0.0.1:55439:5432 -e POSTGRES_DB=inventoryzing_test -e POSTGRES_USER=inventoryzing -e POSTGRES_PASSWORD=inventoryzing-test-only postgres:18.3-alpine
$env:IZ_DATABASE_URL = 'postgresql+psycopg://inventoryzing:inventoryzing-test-only@127.0.0.1:55439/inventoryzing_test'
$env:IZ_TEST_DATABASE_URL = $env:IZ_DATABASE_URL
$env:IZ_RUNTIME_PASSWORD = 'isolated-test-runtime-password-only'
./.venv/Scripts/python.exe -m inventoryzing.migrate
./.venv/Scripts/python.exe -m pytest server/coordinator/tests -q
./.venv/Scripts/ruff.exe check server/coordinator/src server/coordinator/tests db/migrations
./.venv/Scripts/pyright.exe --project server/coordinator --pythonpath .venv/Scripts/python.exe
npm.cmd run --prefix web/main-ui test
npm.cmd run --prefix web/main-ui lint
npm.cmd run --prefix web/main-ui build
```

Remove the test `IZ_RUNTIME_PASSWORD` environment override before using Compose
for real inventory so it uses the secret in `.env`.

Browser tests use a real API/database, not mocked inventory. Initialize test-only
credentials with `./.venv/Scripts/python.exe server/coordinator/tests/prepare_browser.py`.
Start the built SPA/API at `http://127.0.0.1:8099` with that public origin and test
database, then run:

```powershell
npm.cmd exec --prefix web/main-ui -- playwright install chromium
npm.cmd run --prefix web/main-ui e2e
```

Tests cover desktop/mobile create/scan/move/history, hierarchical classification,
inherited filtering, QR image loading, accessibility, horizontal overflow,
screenshots, and lost-response retry across a browser reload.
Browser-test credentials appear only in test fixtures and must never be used for
real inventory. Tests never initialize a production account.

The Windows hardware-proof contracts and guarded physical entry points are under
`agents/windows/inventoryzing-agent`. Their default suite is hardware-free; each
physical scanner or printer scenario remains skipped unless an operator explicitly
authorizes that one scenario through its wrapper script.

Provision a coordinator printer credential directly into an ACL-restricted Windows
file with `./scripts/provision-printer-agent.ps1 -Name 'workshop-printer'`. Complete
first-tag configuration and foreground-agent instructions are in the
[Windows device-agent guide](../agents/windows/inventoryzing-agent/README.md#run-the-first-managed-tag).

The Zebra DS22 live scanner path uses no durable scan queue. Provision its scoped
credential with `./scripts/provision-scanner-agent.ps1 -Name 'workshop-scanner'`,
then follow the [live scanner instructions](../agents/windows/inventoryzing-agent/README.md#run-the-live-scanner-agent).

```powershell
dotnet restore agents/windows/inventoryzing-agent/Inventoryzing.Agent.sln --locked-mode
dotnet format agents/windows/inventoryzing-agent/Inventoryzing.Agent.sln --verify-no-changes --severity info --no-restore
dotnet test agents/windows/inventoryzing-agent/Inventoryzing.Agent.sln --configuration Release --no-restore
```

Regenerate and commit both API contract artifacts after contract changes:

```powershell
./.venv/Scripts/python.exe -m inventoryzing.openapi web/main-ui/openapi.json
npm.cmd run --prefix web/main-ui generate:api
```

