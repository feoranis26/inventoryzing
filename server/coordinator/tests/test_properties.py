from contextlib import nullcontext
from datetime import date
from unittest.mock import Mock
from uuid import uuid4

import pytest

from inventoryzing.modules.labeling import (
    LabelElement,
    _element_text,
    label_placeholder_definitions,
    resolve_label_text,
)
from inventoryzing.properties import PropertyDefinition, PropertyProvider, PropertyRegistry


def test_module_properties_are_structured_and_missing_values_are_safe():
    registry = PropertyRegistry()
    registry.register(PropertyProvider("maintenance", (
        PropertyDefinition(key="maintenance.next_due", label="Next maintenance due",
                           provider="maintenance", type="date"),
        PropertyDefinition(key="maintenance.notes", label="Notes", provider="maintenance"),
    ), lambda connection, object_id: {"maintenance.next_due": date(2036, 8, 1)}))
    connection = Mock()
    connection.scalar.side_effect = [True, None]
    connection.begin_nested.side_effect = lambda: nullcontext()
    due, notes = registry.resolve(connection, uuid4())
    assert due.label == "Next maintenance due"
    assert due.value == date(2036, 8, 1)
    assert due.formatted_value == "01 Aug 2036"
    assert notes.status == "missing" and notes.formatted_value == ""
    context = {due.key: due.formatted_value, "caption:" + due.key: due.label}
    element = LabelElement(id="due", kind="field", x_mm=0, y_mm=0, width_mm=20,
                           height_mm=5, content="{maintenance.next_due}", show_label=True)
    assert _element_text(element, context) == "Next maintenance due: 01 Aug 2036"
    assert _element_text(element, {}) == ""
    assert resolve_label_text("{manufacturing.gcode_filename}", {}) == ""
    assert resolve_label_text("{object.name}", {"object.name": "Box {A}"}) == "Box {A}"
    assert resolve_label_text("{maintenance.next_due[-4:]}", context) == "2036"


def test_provider_failure_is_isolated_from_other_providers():
    registry = PropertyRegistry()

    def broken(connection, object_id):
        raise RuntimeError("Module failed")

    registry.register(PropertyProvider("broken", (
        PropertyDefinition(key="broken.value", label="Broken", provider="broken"),
    ), broken))
    registry.register(PropertyProvider("working", (
        PropertyDefinition(key="working.value", label="Working", provider="working"),
    ), lambda connection, object_id: {"working.value": "OK"}))
    connection = Mock()
    connection.scalar.side_effect = [True, None]
    connection.begin_nested.side_effect = lambda: nullcontext()
    failed, working = registry.resolve(connection, uuid4())
    assert failed.status == "unavailable"
    assert working.formatted_value == "OK"
    assert connection.begin_nested.call_count == 2


def test_provider_namespace_and_duplicates_are_checked():
    registry = PropertyRegistry()
    provider = PropertyProvider("module", (
        PropertyDefinition(key="object.name", label="Wrong namespace", provider="module"),
    ), lambda connection, object_id: {})
    with pytest.raises(ValueError):
        registry.register(provider)


def test_label_placeholder_catalog_includes_providers_and_render_context():
    registry = PropertyRegistry()
    registry.register(PropertyProvider("maintenance", (
        PropertyDefinition(key="maintenance.next_due", label="Next maintenance due",
                           provider="maintenance", type="date"),
    ), lambda connection, object_id: {}))
    definitions = {field.key: field for field in label_placeholder_definitions(registry)}
    assert definitions["maintenance.next_due"].label == "Next maintenance due"
    assert definitions["labeling.printed_at"].label == "Printing time"
    assert definitions["labeling.printed_at"].provider == "labeling"
