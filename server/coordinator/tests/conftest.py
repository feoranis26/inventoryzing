import os
from uuid import uuid4

import pytest
from sqlalchemy import create_engine, text


@pytest.fixture
def database():
    url = os.getenv("IZ_TEST_DATABASE_URL")
    if not url or not url.rsplit("/", 1)[-1].endswith("_test"):
        pytest.skip("Set IZ_TEST_DATABASE_URL to an isolated database ending in _test")
    engine = create_engine(url, isolation_level="READ COMMITTED")
    site_id, actor_id = uuid4(), uuid4()
    with engine.begin() as connection:
        connection.execute(text("TRUNCATE iz.sites CASCADE"))
        connection.execute(text("TRUNCATE iz.roles CASCADE"))
        connection.execute(text("TRUNCATE iz.login_attempts"))
        connection.execute(
            text(
                "INSERT INTO iz.sites(id, display_name, local) VALUES (:id, 'Test workshop', true)"
            ),
            {"id": site_id},
        )
        connection.execute(
            text("""
            INSERT INTO iz.entities(id, kind, home_site_id, write_site_id)
            VALUES (:id, 'account', :site, :site)
        """),
            {"id": actor_id, "site": site_id},
        )
        connection.execute(
            text(
                "INSERT INTO iz.accounts(id, login, password_hash) VALUES (:id, 'test', 'invalid')"
            ),
            {"id": actor_id},
        )
        connection.execute(
            text("INSERT INTO iz.command_epochs(site_id, epoch) VALUES (:site, 1)"),
            {"site": site_id},
        )
    yield engine, site_id, actor_id
    engine.dispose()
