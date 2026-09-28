from decimal import Decimal
from uuid import uuid4

import pytest

from inventoryzing.commands import CommandError, execute_command, normalize_property_value
from inventoryzing.contracts import Command
from inventoryzing.properties import format_stored_property, resolve_stored_properties

DEFINITION = {
    "value_type": "quantity_range",
    "quantity_dimension": "voltage",
    "allowed_units": ["V", "mV"],
}


def test_fixed_and_range_have_comparable_bounds_and_distinct_modes():
    fixed = normalize_property_value(DEFINITION, {"mode": "fixed", "amount": "12000", "unit": "mV"})
    adjustable = normalize_property_value(
        DEFINITION, {"mode": "range", "min": "12", "max": "12", "unit": "V"}
    )
    assert Decimal(fixed["lower"]) == Decimal(adjustable["lower"]) == 12
    assert Decimal(fixed["upper"]) == Decimal(adjustable["upper"]) == 12
    assert fixed["mode"] == "fixed" and adjustable["mode"] == "range"
    assert format_stored_property(fixed, "quantity_range") == "12000 mV"
    assert format_stored_property(adjustable, "quantity_range") == "12–12 V"


@pytest.mark.parametrize(
    "value",
    [
        {"mode": "range", "min": "24", "max": "3", "unit": "V"},
        {"mode": "range", "min": "3", "unit": "V"},
        {"mode": "fixed", "amount": "12", "min": "3", "max": "24", "unit": "V"},
        {"mode": "fixed", "amount": "NaN", "unit": "V"},
        {"mode": "fixed", "amount": "12", "unit": "A"},
        {"mode": "range", "min": "3", "max": None, "unit": "V"},
        {"mode": "unspecified"},
    ],
)
def test_invalid_ranges_rejected(value):
    with pytest.raises(CommandError):
        normalize_property_value(DEFINITION, value)


@pytest.mark.integration
def test_group_inheritance_deduplication_atomic_overrides_and_removal(database):
    engine, site, actor = database

    def command(**payload):
        return execute_command(
            engine,
            Command(
                authority_site=site,
                authority_epoch=1,
                command_epoch=1,
                command_id=uuid4(),
                payload=payload,
            ),
            actor,
        )

    field = command(
        kind="property.definition.create",
        label="Output voltage",
        value_type="quantity_range",
        quantity_dimension="voltage",
        allowed_units=["V", "mV"],
    )
    group = command(kind="property.group.create", name="DC output", property_ids=[field.entity_id])
    other = command(
        kind="property.group.create", name="Shared fields", property_ids=[field.entity_id]
    )
    parent = command(kind="type.create", name="Powered", abstract=True)
    child = command(kind="type.create", name="Converter", parent_type_id=parent.entity_id)
    command(
        kind="type.property.group.set",
        type_id=parent.entity_id,
        expected_version=1,
        group_id=group.entity_id,
        applicable=True,
    )
    command(
        kind="type.property.group.set",
        type_id=child.entity_id,
        expected_version=1,
        group_id=other.entity_id,
        applicable=True,
    )
    command(
        kind="property.value.set",
        target_id=parent.entity_id,
        target_kind="object_type",
        expected_version=2,
        property_id=field.entity_id,
        mode="value",
        value={"mode": "range", "min": "3", "max": "24", "unit": "V"},
    )
    obj = command(kind="object.create", name="Buck converter", object_type_id=child.entity_id)
    with engine.connect() as connection:
        fields = resolve_stored_properties(connection, obj.entity_id, "object")
        assert len(fields) == 1 and fields[0].formatted_value == "3–24 V"
        assert not fields[0].declared_directly
    command(
        kind="property.value.set",
        target_id=obj.entity_id,
        target_kind="object",
        expected_version=1,
        property_id=field.entity_id,
        mode="value",
        value={"mode": "fixed", "amount": "5000", "unit": "mV"},
    )
    command(
        kind="property.unit.set",
        target_id=obj.entity_id,
        target_kind="object",
        expected_version=2,
        property_id=field.entity_id,
        unit="V",
    )
    with engine.connect() as connection:
        field_value = resolve_stored_properties(connection, obj.entity_id, "object")[0]
        assert field_value.value["mode"] == "fixed"
        assert Decimal(field_value.value["lower"]) == Decimal(field_value.value["upper"]) == 5
    # Another inherited group still supplies this field, so detaching is safe.
    command(
        kind="type.property.group.set",
        type_id=child.entity_id,
        expected_version=2,
        group_id=other.entity_id,
        applicable=False,
    )
    with pytest.raises(CommandError, match="hide stored"):
        command(
            kind="property.group.edit",
            group_id=group.entity_id,
            expected_version=1,
            name="DC output",
            property_ids=[],
        )
    with pytest.raises(CommandError, match="hide stored"):
        command(
            kind="type.edit",
            type_id=child.entity_id,
            expected_version=3,
            name="Detached converter",
            parent_type_id=None,
        )
    with pytest.raises(CommandError, match="type assignments"):
        command(
            kind="definition.delete",
            entity_id=group.entity_id,
            entity_kind="property_group",
            expected_version=1,
        )
    command(
        kind="definition.delete",
        entity_id=other.entity_id,
        entity_kind="property_group",
        expected_version=1,
    )
    command(
        kind="property.value.set",
        target_id=obj.entity_id,
        target_kind="object",
        expected_version=3,
        property_id=field.entity_id,
        mode="unset",
    )
    with engine.connect() as connection:
        assert resolve_stored_properties(connection, obj.entity_id, "object")[0].value is None
    command(
        kind="property.value.set",
        target_id=obj.entity_id,
        target_kind="object",
        expected_version=4,
        property_id=field.entity_id,
        mode="inherit",
    )
    with engine.connect() as connection:
        assert (
            resolve_stored_properties(connection, obj.entity_id, "object")[0].value["mode"]
            == "range"
        )
