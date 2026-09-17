from decimal import Decimal

import pytest

from inventoryzing.commands import CommandError, normalize_property_value
from inventoryzing.properties import format_stored_property
from inventoryzing.units import CATALOG, normalize_amount


@pytest.mark.parametrize("dimension,unit,amount,expected,canonical", [
    ("mass", "g", "500", "0.500", "kg"),
    ("mass", "lb", "2", "0.90718474", "kg"),
    ("length", "mm", "25.4", "0.0254", "m"),
    ("length", "inch", "1", "0.0254", "m"),
    ("area", "square_ft", "10", "0.92903040", "square_m"),
    ("volume", "us_gal", "20", "75708.235680", "ml"),
    ("volume", "imperial_gal", "1", "4546.09", "ml"),
])
def test_quantity_normalization_and_display(dimension, unit, amount, expected, canonical):
    result = normalize_property_value({
        "value_type": "quantity", "quantity_dimension": dimension, "allowed_units": [unit],
    }, {"amount": amount, "unit": unit})
    assert Decimal(result["amount"]) == Decimal(expected)
    assert result["unit"] == canonical
    assert result["display_amount"] == amount and result["display_unit"] == unit
    assert format_stored_property(result, "quantity").startswith(amount + " ")


def test_precision_is_not_limited_to_default_decimal_context():
    assert normalize_amount("123456789012345678901234567890.123", "mass", "g") == (
        "123456789012345678901234567.890123"
    )


def test_dimension_mismatch_rejected_even_if_unit_was_allowed():
    with pytest.raises(CommandError, match="incompatible"):
        normalize_property_value({
            "value_type": "quantity", "quantity_dimension": "mass", "allowed_units": ["m"],
        }, {"amount": "1", "unit": "m"})


def test_catalog_canonical_units_and_factors():
    for dimension in CATALOG:
        assert len({unit.id for unit in dimension.units}) == len(dimension.units)
        canonical = next(unit for unit in dimension.units if unit.id == dimension.canonical_unit)
        assert Decimal(canonical.factor) == 1
        assert all(Decimal(unit.factor) > 0 for unit in dimension.units)
