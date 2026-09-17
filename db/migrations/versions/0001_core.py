from pathlib import Path

from alembic import op

revision = "0001_core"
down_revision = None
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.execute(Path(__file__).with_suffix(".sql").read_text(encoding="utf-8"))


def downgrade() -> None:
    raise RuntimeError(
        "Restore a verified backup instead of deleting inventory history."
    )
