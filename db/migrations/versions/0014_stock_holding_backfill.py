from pathlib import Path

from alembic import op

revision = "0014_stock_holding_backfill"
down_revision = "0013_stock_holdings"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.execute(Path(__file__).with_suffix(".sql").read_text(encoding="utf-8"))


def downgrade() -> None:
    raise RuntimeError("Restore a verified backup instead of removing initialized stock holdings.")
