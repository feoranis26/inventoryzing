from alembic import context
from inventoryzing.config import Settings
from sqlalchemy import create_engine


def run_migrations() -> None:
    engine = create_engine(Settings().database_url)
    with engine.connect() as connection:
        context.configure(connection=connection, target_metadata=None)
        with context.begin_transaction():
            context.run_migrations()
    engine.dispose()


run_migrations()
