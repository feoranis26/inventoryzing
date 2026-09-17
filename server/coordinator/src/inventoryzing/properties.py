"""Shared object properties. Trusted modules register providers during startup."""

import logging
import re
from collections.abc import Callable, Mapping
from dataclasses import dataclass
from datetime import UTC, date, datetime
from typing import Literal
from uuid import UUID

from fastapi import APIRouter, HTTPException, Request
from pydantic import BaseModel, Field, JsonValue
from sqlalchemy import Connection, text

from inventoryzing.auth import authenticate
from inventoryzing.units import CATALOG, UNIT_LABELS, UnitDimension

PropertyType = Literal[
    "text", "date", "datetime", "number", "integer", "decimal", "boolean", "quantity"
]
PropertyScalar = str | int | float | bool | date | datetime
logger = logging.getLogger(__name__)
router = APIRouter(prefix="/api")


class PropertyDefinition(BaseModel):
    id: UUID | None = None
    key: str
    label: str
    type: PropertyType = "text"
    description: str = ""
    example: str = ""
    provider: str
    quantity_dimension: str | None = None
    canonical_unit: str | None = None
    allowed_units: list[str] = Field(default_factory=list)
    schema_revision: int = 1
    editable: bool = False
    version: int = 1
    declared_on: list[str] = Field(default_factory=list)


class PropertyValue(PropertyDefinition):
    value: PropertyScalar | JsonValue | None = None
    formatted_value: str = ""
    status: Literal["available", "missing", "unavailable"] = "missing"
    effective_state: Literal["value", "unset", "missing"] = "missing"
    local_state: Literal["inherit", "value", "unset"] = "inherit"
    source_kind: Literal["object", "object_type"] | None = None
    source_id: UUID | None = None
    source_name: str | None = None
    applicability_source_id: UUID | None = None
    applicability_source_name: str | None = None
    declared_directly: bool = False


def format_property(value: PropertyScalar, kind: PropertyType) -> str:
    if kind == "datetime":
        instant = value if isinstance(value, datetime) else datetime.fromisoformat(str(value))
        return instant.astimezone(UTC).strftime("%d %b %Y %H:%M UTC")
    if kind == "date":
        day = value if isinstance(value, date) else date.fromisoformat(str(value))
        return day.strftime("%d %b %Y")
    if kind == "boolean":
        if not isinstance(value, bool):
            raise ValueError("Boolean properties must return a boolean.")
        return "Yes" if value else "No"
    return str(value)




def format_stored_property(value: JsonValue, kind: PropertyType) -> str:
    if kind == "quantity" and isinstance(value, dict):
        amount = value.get("display_amount", value.get("amount", ""))
        unit = str(value.get("display_unit", value.get("unit", "")))
        return f"{amount} {UNIT_LABELS.get(unit, unit)}".strip()
    if kind == "boolean" and isinstance(value, bool):
        return "Yes" if value else "No"
    if kind == "datetime" and isinstance(value, str):
        return format_property(value, "datetime")
    if kind == "date" and isinstance(value, str):
        return format_property(value, "date")
    return str(value)


def resolve_stored_properties(connection: Connection, target_id: UUID,
                              target_kind: Literal["object", "object_type"]
                              ) -> list[PropertyValue]:
    if target_kind == "object":
        type_id = connection.scalar(
            text("SELECT object_type_id FROM iz.objects WHERE id = :id"), {"id": target_id}
        )
    else:
        type_id = target_id
    if type_id is None:
        return []
    lineage = connection.execute(text("""
        WITH RECURSIVE ancestors(id, depth) AS (
            SELECT CAST(:id AS uuid), 0
            UNION ALL
            SELECT type.parent_type_id, ancestors.depth + 1
            FROM iz.object_types type JOIN ancestors ON type.id = ancestors.id
            WHERE type.parent_type_id IS NOT NULL
        )
        SELECT ancestors.id, ancestors.depth, type.name
        FROM ancestors JOIN iz.object_types type ON type.id = ancestors.id
        ORDER BY ancestors.depth
    """), {"id": type_id}).mappings().all()
    type_ids = [row["id"] for row in lineage]
    declarations = connection.execute(text("""
        SELECT declaration.type_id, declaration.property_id
        FROM iz.type_property_declarations declaration
        WHERE declaration.type_id = ANY(CAST(:types AS uuid[]))
    """), {"types": type_ids}).mappings().all()
    if not declarations:
        return []
    applicable_ids = {row["property_id"] for row in declarations}
    definitions = connection.execute(text("""
        SELECT * FROM iz.property_definitions
        WHERE id = ANY(CAST(:ids AS uuid[])) ORDER BY lower(label), id
    """), {"ids": list(applicable_ids)}).mappings().all()
    target_ids = [target_id] if target_kind == "object" else []
    target_ids.extend(type_ids)
    entries = connection.execute(text("""
        SELECT target_id, property_id, state, value
        FROM iz.property_values
        WHERE target_id = ANY(CAST(:targets AS uuid[]))
    """), {"targets": target_ids}).mappings().all()
    entry_map = {(row["target_id"], row["property_id"]): row for row in entries}
    result = []
    for definition_row in definitions:
        definition = dict(definition_row)
        property_id = definition["id"]
        applicability = next(
            row for row in lineage
            if any(item["type_id"] == row["id"] and item["property_id"] == property_id
                   for item in declarations)
        )
        local_entry = entry_map.get((target_id, property_id))
        candidates = []
        if target_kind == "object" and local_entry:
            candidates.append((target_id, "object", None, local_entry))
        for type_row in lineage:
            entry = entry_map.get((type_row["id"], property_id))
            if entry:
                candidates.append((type_row["id"], "object_type", type_row["name"], entry))
        selected = candidates[0] if candidates else None
        state = selected[3]["state"] if selected else "missing"
        value = selected[3]["value"] if selected and state == "value" else None
        result.append(PropertyValue(
            id=property_id, key=definition["key"], label=definition["label"],
            type=definition["value_type"], description=definition["description"],
            provider=definition["namespace"], quantity_dimension=definition["quantity_dimension"],
            canonical_unit=definition["canonical_unit"], allowed_units=definition["allowed_units"],
            schema_revision=definition["schema_revision"], editable=True,
            value=value, formatted_value=(format_stored_property(value, definition["value_type"])
                                           if value is not None else ""),
            status="available" if value is not None else "missing", effective_state=state,
            local_state=local_entry["state"] if local_entry else "inherit",
            source_kind=selected[1] if selected else None,
            source_id=selected[0] if selected else None,
            source_name=selected[2] if selected else None,
            applicability_source_id=applicability["id"],
            applicability_source_name=applicability["name"],
            declared_directly=any(item["type_id"] == type_id and item["property_id"] == property_id
                                  for item in declarations),
        ))
    return result


@dataclass(frozen=True)
class PropertyProvider:
    namespace: str
    fields: tuple[PropertyDefinition, ...]
    resolve: Callable[[Connection, UUID], Mapping[str, PropertyScalar | None]]


class PropertyRegistry:
    def __init__(self) -> None:
        self._providers: dict[str, PropertyProvider] = {}

    def register(self, provider: PropertyProvider) -> None:
        if provider.namespace in self._providers:
            raise ValueError(f"Duplicate property provider: {provider.namespace}")
        keys = set()
        for field in provider.fields:
            if (not re.fullmatch(r"[a-z][a-z0-9_]*\.[a-z][a-z0-9_]*", field.key)
                    or not field.key.startswith(provider.namespace + ".")
                    or field.provider != provider.namespace or field.key in keys):
                raise ValueError(f"Invalid or duplicate property key: {field.key}")
            keys.add(field.key)
        self._providers[provider.namespace] = provider

    def definitions(self) -> list[PropertyDefinition]:
        return [field for provider in self._providers.values() for field in provider.fields]

    def resolve(self, connection: Connection, object_id: UUID) -> list[PropertyValue]:
        if not connection.scalar(text("""
            SELECT EXISTS(SELECT 1 FROM iz.objects object JOIN iz.entities entity
                ON entity.id=object.id WHERE object.id=:id AND entity.archived_at IS NULL)
        """), {"id": object_id}):
            raise HTTPException(404, "Object not found.")
        result = []
        for provider in self._providers.values():
            try:
                # Roll back a failing provider's SQL without poisoning the other providers.
                with connection.begin_nested():
                    values = dict(provider.resolve(connection, object_id))
            except Exception:
                logger.exception("Property provider %s failed", provider.namespace)
                result.extend(PropertyValue(**field.model_dump(), status="unavailable")
                              for field in provider.fields)
                continue
            for field in provider.fields:
                value = values.get(field.key)
                try:
                    result.append(PropertyValue(
                        **field.model_dump(), value=value,
                        formatted_value=(format_property(value, field.type)
                                         if value is not None else ""),
                        status="available" if value is not None else "missing",
                        effective_state="value" if value is not None else "missing",
                    ))
                except Exception:
                    logger.exception("Property formatting failed: %s", field.key)
                    result.append(PropertyValue(**field.model_dump(), status="unavailable"))
        result.extend(resolve_stored_properties(connection, object_id, "object"))
        return result


def core_values(connection: Connection, object_id: UUID) -> Mapping[str, PropertyScalar | None]:
    row = connection.execute(text("""
        SELECT object.name, object.id::text AS uuid, object.description,
               object_type.name AS type_name, entity.created_at, entity.updated_at,
               (SELECT 'I' || identifier.value FROM iz.identifiers identifier
                JOIN iz.sites site ON site.id=identifier.issuer_site_id AND site.local
                WHERE identifier.entity_id=object.id LIMIT 1) AS local_id
        FROM iz.objects object JOIN iz.entities entity ON entity.id=object.id
        LEFT JOIN iz.object_types object_type ON object_type.id=object.object_type_id
        WHERE object.id=:id
    """), {"id": object_id}).mappings().one()
    return {f"object.{key}": value for key, value in row.items()}


def stored_definitions(connection: Connection) -> list[PropertyDefinition]:
    rows = connection.execute(text("""
        SELECT definition.id, key, label, description, value_type AS type, namespace AS provider,
               quantity_dimension, canonical_unit, allowed_units, schema_revision, entity.version,
               ARRAY(SELECT type.name FROM iz.type_property_declarations declaration
                     JOIN iz.object_types type ON type.id=declaration.type_id
                     WHERE declaration.property_id=definition.id ORDER BY type.name) AS declared_on
        FROM iz.property_definitions definition JOIN iz.entities entity ON entity.id=definition.id
        WHERE entity.archived_at IS NULL ORDER BY lower(label), definition.id
    """)).mappings().all()
    return [PropertyDefinition(**row, editable=True) for row in rows]


def register(app) -> None:
    registry = PropertyRegistry()
    registry.register(PropertyProvider("object", tuple(
        PropertyDefinition(key=f"object.{key}", label=label, type=kind,
                           provider="object", example=example)
        for key, label, kind, example in (
            ("name", "Name", "text", "Example inventory object"),
            ("uuid", "UUID", "text", "01234567-89ab-cdef-0123-456789abcdef"),
            ("local_id", "Local identifier", "text", "I123456"),
            ("description", "Description", "text", "Example description"),
            ("type_name", "Object type", "text", "Equipment"),
            ("created_at", "Created", "datetime", "01 Aug 2036 12:00 UTC"),
            ("updated_at", "Updated", "datetime", "01 Aug 2036 12:00 UTC"),
        )
    ), core_values))
    app.state.property_registry = registry
    app.include_router(router)


@router.get("/properties", response_model=list[PropertyDefinition])
def list_properties(request: Request):
    authenticate(request)
    definitions = request.app.state.property_registry.definitions()
    with request.app.state.engine.connect() as connection:
        definitions.extend(stored_definitions(connection))
    return definitions


@router.get("/objects/{object_id}/properties", response_model=list[PropertyValue])
def object_properties(object_id: UUID, request: Request):
    authenticate(request)
    with request.app.state.engine.connect() as connection:
        return request.app.state.property_registry.resolve(connection, object_id)


@router.get("/types/{type_id}/properties", response_model=list[PropertyValue])
def type_properties(type_id: UUID, request: Request):
    authenticate(request)
    with request.app.state.engine.connect() as connection:
        if not connection.scalar(text("""
            SELECT EXISTS(SELECT 1 FROM iz.object_types type JOIN iz.entities entity
                ON entity.id=type.id WHERE type.id=:id AND entity.archived_at IS NULL)
        """), {"id": type_id}):
            raise HTTPException(404, "Object type not found.")
        return resolve_stored_properties(connection, type_id, "object_type")


@router.get("/property-units", response_model=list[UnitDimension])
def property_units(request: Request):
    authenticate(request)
    return CATALOG
