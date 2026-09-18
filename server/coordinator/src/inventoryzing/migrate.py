import os
from pathlib import Path

import psycopg
from alembic import command
from alembic.config import Config
from psycopg import sql
from sqlalchemy.engine import make_url

from inventoryzing.config import Settings


def main() -> None:
    configuration = Path(os.environ.get("IZ_ALEMBIC_CONFIG", "server/coordinator/alembic.ini"))
    command.upgrade(Config(str(configuration)), "head")
    password = os.environ["IZ_RUNTIME_PASSWORD"]
    if len(password) < 24:
        raise ValueError("IZ_RUNTIME_PASSWORD must contain at least 24 characters.")
    url = make_url(Settings().database_url)
    with psycopg.connect(
        host=url.host,
        port=url.port or 5432,
        dbname=url.database,
        user=url.username,
        password=url.password,
    ) as connection:
        with connection.cursor() as cursor:
            cursor.execute("SELECT pg_advisory_xact_lock(841899)")
            cursor.execute("SELECT 1 FROM pg_roles WHERE rolname = 'inventoryzing_app'")
            if cursor.fetchone() is None:
                cursor.execute(
                    "CREATE ROLE inventoryzing_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE"
                )
            cursor.execute(
                sql.SQL("ALTER ROLE inventoryzing_app PASSWORD {}").format(sql.Literal(password))
            )
            cursor.execute("GRANT USAGE ON SCHEMA iz TO inventoryzing_app")
            cursor.execute("GRANT USAGE ON SCHEMA iz_print TO inventoryzing_app")
            cursor.execute("GRANT USAGE ON SCHEMA iz_scanner TO inventoryzing_app")
            cursor.execute("GRANT SELECT ON ALL TABLES IN SCHEMA iz TO inventoryzing_app")
            cursor.execute("GRANT SELECT ON ALL TABLES IN SCHEMA iz_print TO inventoryzing_app")
            cursor.execute("GRANT SELECT ON ALL TABLES IN SCHEMA iz_scanner TO inventoryzing_app")
            cursor.execute("""
                GRANT INSERT, UPDATE ON iz.entities, iz.objects, iz.object_types, iz.placements,
                    iz.tags, iz.property_definitions TO inventoryzing_app;
                GRANT INSERT, DELETE ON iz.tag_edges, iz.entity_tags TO inventoryzing_app;
                GRANT INSERT, UPDATE, DELETE ON iz.type_property_declarations,
                    iz.property_values TO inventoryzing_app;
                GRANT INSERT, UPDATE ON iz.stock_policies TO inventoryzing_app;
                GRANT INSERT, UPDATE, DELETE ON iz.stock_holdings TO inventoryzing_app;
                GRANT INSERT, DELETE ON iz.stock_movements TO inventoryzing_app;
                GRANT INSERT ON iz.identifiers, iz.domain_events, iz.event_subjects,
                    iz.replication_outbox, iz.command_receipts TO inventoryzing_app;
                GRANT UPDATE(result) ON iz.command_receipts TO inventoryzing_app;
                GRANT INSERT, UPDATE, DELETE ON iz.sessions, iz.login_attempts TO inventoryzing_app;
                GRANT UPDATE ON iz.sites TO inventoryzing_app;
                GRANT INSERT, UPDATE ON iz.principals, iz.accounts, iz.roles
                    TO inventoryzing_app;
                GRANT INSERT, DELETE ON iz.account_roles, iz.role_permissions
                    TO inventoryzing_app;
                GRANT INSERT, UPDATE ON iz_print.label_templates TO inventoryzing_app;
                GRANT INSERT ON iz_print.label_template_revisions TO inventoryzing_app;
                GRANT EXECUTE ON FUNCTION iz.admit_epoch(uuid, bigint) TO inventoryzing_app;
                GRANT INSERT ON iz_print.artifacts, iz_print.jobs, iz_print.attempts,
                    iz_print.observations TO inventoryzing_app;
                GRANT UPDATE ON iz_print.attempts TO inventoryzing_app;
                GRANT INSERT, UPDATE ON iz_scanner.terminals TO inventoryzing_app;
                GRANT INSERT ON iz_scanner.agent_terminal_bindings TO inventoryzing_app;
            """)


if __name__ == "__main__":
    main()
