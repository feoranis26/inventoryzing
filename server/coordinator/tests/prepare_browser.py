import os

from sqlalchemy import create_engine, text

from inventoryzing.auth import bootstrap


def main() -> None:
    url = os.environ["IZ_TEST_DATABASE_URL"]
    if not url.rsplit("/", 1)[-1].endswith("_test"):
        raise RuntimeError("Browser fixtures require an isolated _test database.")
    engine = create_engine(url)
    with engine.begin() as connection:
        connection.execute(text("TRUNCATE iz.sites CASCADE"))
        connection.execute(text("TRUNCATE iz.roles CASCADE"))
        connection.execute(text("TRUNCATE iz.login_attempts"))
    bootstrap(engine, "Browser test workshop", "browser-test", "browser-test-only-password")
    engine.dispose()


if __name__ == "__main__":
    main()
