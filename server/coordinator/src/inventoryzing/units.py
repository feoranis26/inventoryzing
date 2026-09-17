"""Shared unit catalog. Factors are exact decimals relative to each canonical unit."""

from decimal import Decimal, localcontext
from typing import Literal

from pydantic import BaseModel

Dimension = Literal["count", "volume", "length", "mass", "area"]


class Unit(BaseModel):
    id: str
    label: str
    factor: str


class UnitDimension(BaseModel):
    id: Dimension
    label: str
    canonical_unit: str
    units: list[Unit]


def dimension(
    identity: Dimension, label: str, canonical: str, entries: list[tuple[str, str, str]]
) -> UnitDimension:
    return UnitDimension(
        id=identity,
        label=label,
        canonical_unit=canonical,
        units=[Unit(id=key, label=name, factor=factor) for key, name, factor in entries],
    )


CATALOG = [
    dimension(
        "count",
        "Count",
        "each",
        [
            ("each", "each", "1"),
        ],
    ),
    dimension(
        "volume",
        "Volume",
        "ml",
        [
            ("ml", "mL", "1"),
            ("l", "L", "1000"),
            ("cubic_cm", "cm³", "1"),
            ("cubic_m", "m³", "1000000"),
            ("us_fl_oz", "US fl oz", "29.5735295625"),
            ("us_pt", "US pt", "473.176473"),
            ("us_qt", "US qt", "946.352946"),
            ("us_gal", "US gal", "3785.411784"),
            ("imperial_fl_oz", "imperial fl oz", "28.4130625"),
            ("imperial_pt", "imperial pt", "568.26125"),
            ("imperial_qt", "imperial qt", "1136.5225"),
            ("imperial_gal", "imperial gal", "4546.09"),
        ],
    ),
    dimension(
        "length",
        "Length",
        "m",
        [
            ("mm", "mm", "0.001"),
            ("cm", "cm", "0.01"),
            ("m", "m", "1"),
            ("km", "km", "1000"),
            ("inch", "in", "0.0254"),
            ("ft", "ft", "0.3048"),
            ("yd", "yd", "0.9144"),
            ("mile", "mi", "1609.344"),
        ],
    ),
    dimension(
        "mass",
        "Mass / weight",
        "kg",
        [
            ("mg", "mg", "0.000001"),
            ("g", "g", "0.001"),
            ("kg", "kg", "1"),
            ("tonne", "tonne", "1000"),
            ("oz", "oz", "0.028349523125"),
            ("lb", "lb", "0.45359237"),
        ],
    ),
    dimension(
        "area",
        "Area",
        "square_m",
        [
            ("square_mm", "mm²", "0.000001"),
            ("square_cm", "cm²", "0.0001"),
            ("square_m", "m²", "1"),
            ("square_km", "km²", "1000000"),
            ("hectare", "ha", "10000"),
            ("square_inch", "in²", "0.00064516"),
            ("square_ft", "ft²", "0.09290304"),
            ("square_yd", "yd²", "0.83612736"),
            ("acre", "acre", "4046.8564224"),
        ],
    ),
]
DIMENSIONS = {item.id: item for item in CATALOG}
UNIT_LABELS = {unit.id: unit.label for item in CATALOG for unit in item.units}


def normalize_amount(amount: str, dimension_id: str, unit_id: str) -> str:
    definition = DIMENSIONS[dimension_id]
    factor = Decimal(next(unit.factor for unit in definition.units if unit.id == unit_id))
    number = Decimal(amount)
    # Multiplication must not silently round inputs longer than Decimal's default precision.
    with localcontext() as context:
        context.prec = max(28, len(number.as_tuple().digits) + len(factor.as_tuple().digits))
        return format(number * factor, "f")


def display_amount(canonical_amount: str, dimension_id: str, unit_id: str) -> str:
    definition = DIMENSIONS[dimension_id]
    factor = Decimal(next(unit.factor for unit in definition.units if unit.id == unit_id))
    number = Decimal(canonical_amount)
    with localcontext() as context:
        context.prec = max(28, len(number.as_tuple().digits) + len(factor.as_tuple().digits))
        return format(number / factor, "f")
