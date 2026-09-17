from pathlib import Path

from alembic import op

revision = "0005_label_templates"
down_revision = "0004_printing"
branch_labels = None
depends_on = None


def upgrade() -> None:
    op.execute(Path(__file__).with_suffix(".sql").read_text(encoding="utf-8"))


def downgrade() -> None:
    raise RuntimeError("Restore a verified backup instead of dropping label templates.")
