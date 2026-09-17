import os

import pytest
from sqlalchemy import create_engine, text

pytestmark = pytest.mark.integration


def test_schema_and_receipt_history_are_separate():
    url = os.getenv("IZ_TEST_DATABASE_URL")
    if not url:
        pytest.skip("IZ_TEST_DATABASE_URL must point to a migrated test database")
    engine = create_engine(url)
    try:
        with engine.connect() as connection:
            assert (
                connection.scalar(text("SELECT version_num FROM alembic_version"))
                == "0013_stock_holdings"
            )
            tables = set(
                connection.scalars(
                    text(
                        "SELECT table_name FROM information_schema.tables WHERE table_schema = 'iz'"
                    )
                )
            )
            assert {
                "command_epochs",
                "command_receipts",
                "domain_events",
                "placements",
                "tags",
                "tag_edges",
                "entity_tags",
                "stock_policies",
                "stock_holdings",
                "stock_movements",
            } <= tables
            print_tables = set(
                connection.scalars(
                    text(
                        "SELECT table_name FROM information_schema.tables "
                        "WHERE table_schema = 'iz_print'"
                    )
                )
            )
            assert {"artifacts", "jobs", "attempts", "observations"} <= print_tables
            scanner_tables = set(
                connection.scalars(text(
                    "SELECT table_name FROM information_schema.tables "
                    "WHERE table_schema = 'iz_scanner'"
                ))
            )
            assert {"terminals", "agent_terminal_bindings"} <= scanner_tables
            dependencies = connection.scalar(
                text("""
                SELECT count(*) FROM pg_constraint
                WHERE conrelid = 'iz.domain_events'::regclass
                  AND confrelid = 'iz.command_receipts'::regclass
            """)
            )
            assert dependencies == 0
    finally:
        engine.dispose()
