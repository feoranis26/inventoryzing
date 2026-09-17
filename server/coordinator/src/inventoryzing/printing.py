"""Compatibility imports for the first-party labeling module.

New coordinator code must import from ``inventoryzing.modules.labeling``. This module
keeps existing integrations and test clients working during the module extraction.
"""

from inventoryzing.modules.labeling import PIXEL_HEIGHT, PIXEL_WIDTH, render_tag_png

__all__ = ["PIXEL_HEIGHT", "PIXEL_WIDTH", "render_tag_png"]
