import hashlib
import json
import re
import secrets
from datetime import UTC, date, datetime
from decimal import Decimal, InvalidOperation
from uuid import UUID, uuid4

from sqlalchemy import Connection, Engine, text

from inventoryzing.contracts import (
    AllocateAlias,
    ChangeStock,
    Command,
    CommandResult,
    ConvertPropertyUnit,
    CreateObject,
    CreatePropertyDefinition,
    CreateStockHolding,
    CreateTag,
    CreateType,
    DeleteDefinition,
    EditObject,
    EditPropertyDefinition,
    EditTag,
    EditType,
    MoveObject,
    SetEntityTags,
    SetObjectType,
    SetPropertyValue,
    SetStockPolicy,
    SetTagParents,
    SetTypePropertyDeclaration,
    TransferStock,
)
from inventoryzing.units import DIMENSIONS, normalize_amount

GRAPH_LOCK = 841901
TAG_GRAPH_LOCK = 841902
TYPE_GRAPH_LOCK = 841903
PERMISSIONS = {
    "object.create": "inventory.create",
    "type.create": "inventory.create",
    "type.edit": "inventory.edit",
    "definition.delete": "inventory.edit",
    "property.definition.edit": "inventory.edit",
    "property.unit.set": "inventory.edit",
    "property.definition.create": "inventory.edit",
    "type.property.declare": "inventory.edit",
    "property.value.set": "inventory.edit",
    "tag.create": "inventory.create",
    "object.edit": "inventory.edit",
    "object.type.set": "inventory.edit",
    "object.move": "inventory.move",
    "tag.edit": "inventory.edit",
    "tag.parents.set": "inventory.edit",
    "tags.set": "inventory.edit",
    "identifier.allocate": "label.print",
    "stock.policy.set": "inventory.edit",
    "stock.holding.create": "inventory.create",
    "stock.receive": "inventory.edit",
    "stock.consume": "inventory.edit",
    "stock.adjust": "inventory.edit",
    "stock.transfer": "inventory.edit",
}


def _decimal_string(value: object) -> str:
    if not isinstance(value, str) or not re.fullmatch(r"-?(0|[1-9][0-9]*)(\.[0-9]+)?", value):
        raise CommandError(
            "INVALID_PROPERTY_VALUE", "Exact decimal values must be JSON strings.", 422
        )
    try:
        number = Decimal(value)
    except InvalidOperation as error:
        raise CommandError(
            "INVALID_PROPERTY_VALUE", "The decimal value is invalid.", 422
        ) from error
    return format(number, "f")


def _positive_decimal(value: object, code: str = "INVALID_STOCK_AMOUNT") -> Decimal:
    try:
        amount = Decimal(_decimal_string(value))
    except CommandError as error:
        raise CommandError(code, "Stock quantities must be exact decimal strings.", 422) from error
    if amount <= 0:
        raise CommandError(code, "Stock quantities must be greater than zero.", 422)
    return amount


def stock_policy_for_type(connection: Connection, type_id: UUID) -> dict:
    row = (
        connection.execute(
            text("""
        WITH RECURSIVE lineage(id, depth) AS (
            SELECT CAST(:type AS uuid), 0
            UNION ALL
            SELECT type.parent_type_id, lineage.depth + 1
            FROM iz.object_types type JOIN lineage ON type.id = lineage.id
            WHERE type.parent_type_id IS NOT NULL
        )
        SELECT policy.type_id AS policy_type_id, policy.quantity_dimension,
               policy.canonical_unit, policy.granularity, policy.allow_negative,
               policy_type.name AS policy_type_name
        FROM lineage JOIN iz.stock_policies policy ON policy.type_id = lineage.id
        JOIN iz.object_types policy_type ON policy_type.id = policy.type_id
        ORDER BY lineage.depth LIMIT 1
    """),
            {"type": type_id},
        )
        .mappings()
        .first()
    )
    if row is None:
        raise CommandError(
            "STOCK_POLICY_REQUIRED",
            "This object type does not define or inherit a stock policy.",
            409,
        )
    return dict(row)


def normalize_stock_amount(policy: dict, amount: object, unit: str) -> Decimal:
    decimal_amount = _positive_decimal(amount)
    dimension = DIMENSIONS[policy["quantity_dimension"]]
    if unit not in {entry.id for entry in dimension.units}:
        raise CommandError(
            "INVALID_STOCK_UNIT", "The stock unit is incompatible with this type.", 422
        )
    canonical = Decimal(normalize_amount(format(decimal_amount, "f"), dimension.id, unit))
    granularity = Decimal(policy["granularity"])
    if canonical % granularity != 0:
        raise CommandError(
            "STOCK_GRANULARITY",
            "Quantity must be a multiple of "
            f"{format(granularity, 'f')} {policy['canonical_unit']}.",
            422,
        )
    return canonical


def stock_holding_for_update(connection: Connection, holding_id: UUID) -> dict:
    row = (
        connection.execute(
            text("""
        SELECT holding.object_id, holding.quantity, holding.policy_type_id, object.object_type_id,
               policy.quantity_dimension, policy.canonical_unit, policy.granularity,
               policy.allow_negative, policy_type.name AS policy_type_name
        FROM iz.stock_holdings holding
        JOIN iz.objects object ON object.id = holding.object_id
        JOIN iz.stock_policies policy ON policy.type_id = holding.policy_type_id
        JOIN iz.object_types policy_type ON policy_type.id = policy.type_id
        WHERE holding.object_id=:id FOR UPDATE
    """),
            {"id": holding_id},
        )
        .mappings()
        .first()
    )
    if row is None:
        raise CommandError("NOT_A_STOCK_HOLDING", "This object is not a stock holding.", 409)
    return dict(row)


def normalize_property_value(definition: dict, value: object) -> object:
    kind = definition["value_type"]
    if kind == "text":
        if not isinstance(value, str):
            raise CommandError("INVALID_PROPERTY_VALUE", "This property requires text.", 422)
        return value
    if kind == "integer":
        if isinstance(value, bool) or not isinstance(value, int):
            raise CommandError("INVALID_PROPERTY_VALUE", "This property requires an integer.", 422)
        return value
    if kind == "decimal":
        return _decimal_string(value)
    if kind == "boolean":
        if not isinstance(value, bool):
            raise CommandError(
                "INVALID_PROPERTY_VALUE", "This property requires true or false.", 422
            )
        return value
    if kind == "date":
        if not isinstance(value, str):
            raise CommandError("INVALID_PROPERTY_VALUE", "This property requires an ISO date.", 422)
        try:
            return date.fromisoformat(value).isoformat()
        except ValueError as error:
            raise CommandError(
                "INVALID_PROPERTY_VALUE", "This property requires an ISO date.", 422
            ) from error
    if kind == "datetime":
        if not isinstance(value, str):
            raise CommandError(
                "INVALID_PROPERTY_VALUE", "This property requires an ISO datetime.", 422
            )
        try:
            instant = datetime.fromisoformat(value.replace("Z", "+00:00"))
        except ValueError as error:
            raise CommandError(
                "INVALID_PROPERTY_VALUE", "This property requires an ISO datetime.", 422
            ) from error
        if instant.tzinfo is None:
            raise CommandError("INVALID_PROPERTY_VALUE", "Datetime values require a timezone.", 422)
        return instant.astimezone(UTC).isoformat().replace("+00:00", "Z")
    if kind == "quantity":
        if not isinstance(value, dict) or set(value) != {"amount", "unit"}:
            raise CommandError(
                "INVALID_PROPERTY_VALUE", "A quantity requires amount and unit fields.", 422
            )
        amount = _decimal_string(value["amount"])
        unit = value["unit"]
        if not isinstance(unit, str) or unit not in definition["allowed_units"]:
            raise CommandError("INVALID_PROPERTY_VALUE", "The quantity unit is not allowed.", 422)
        dimension = DIMENSIONS.get(definition["quantity_dimension"])
        if dimension is None or unit not in {entry.id for entry in dimension.units}:
            raise CommandError("INVALID_PROPERTY_VALUE", "The quantity unit is incompatible.", 422)
        canonical = normalize_amount(amount, dimension.id, unit)
        return {
            "amount": canonical,
            "unit": dimension.canonical_unit,
            "display_amount": amount,
            "display_unit": unit,
        }
    raise CommandError("INVALID_PROPERTY_VALUE", "The property type is unsupported.", 422)


def ensure_property_applicable(
    connection: Connection, target_id: UUID, target_kind: str, property_id: UUID
) -> None:
    applicable = connection.scalar(
        text("""
        WITH RECURSIVE ancestors(id) AS (
            SELECT CASE WHEN :kind = 'object_type' THEN CAST(:target AS uuid)
                        ELSE (SELECT object_type_id FROM iz.objects WHERE id = :target) END
            UNION ALL
            SELECT type.parent_type_id FROM iz.object_types type
            JOIN ancestors ON type.id = ancestors.id
            WHERE type.parent_type_id IS NOT NULL
        )
        SELECT EXISTS (
            SELECT 1 FROM iz.type_property_declarations declaration
            JOIN ancestors ON ancestors.id = declaration.type_id
            WHERE declaration.property_id = :property
        )
    """),
        {"kind": target_kind, "target": target_id, "property": property_id},
    )
    if not applicable:
        raise CommandError(
            "PROPERTY_NOT_APPLICABLE", "The property is not applicable to this target.", 409
        )


def ensure_all_property_entries_applicable(connection: Connection) -> None:
    blockers = (
        connection.execute(
            text("""
        WITH RECURSIVE lineage(descendant_id, ancestor_id) AS (
            SELECT id, id FROM iz.object_types
            UNION ALL
            SELECT lineage.descendant_id, parent.parent_type_id
            FROM lineage JOIN iz.object_types parent ON parent.id = lineage.ancestor_id
            WHERE parent.parent_type_id IS NOT NULL
        ), applicable AS (
            SELECT DISTINCT lineage.descendant_id AS type_id, declaration.property_id
            FROM lineage JOIN iz.type_property_declarations declaration
              ON declaration.type_id = lineage.ancestor_id
        )
        SELECT value.target_id, definition.key
        FROM iz.property_values value
        JOIN iz.property_definitions definition ON definition.id = value.property_id
        LEFT JOIN iz.objects object
          ON value.target_kind = 'object' AND object.id = value.target_id
        LEFT JOIN applicable ON applicable.property_id = value.property_id
          AND applicable.type_id = CASE WHEN value.target_kind = 'object_type'
                                       THEN value.target_id ELSE object.object_type_id END
        WHERE applicable.property_id IS NULL
        ORDER BY definition.key, value.target_id
        LIMIT 10
    """)
        )
        .mappings()
        .all()
    )
    if blockers:
        details = ", ".join(f"{row['key']} on {row['target_id']}" for row in blockers)
        raise CommandError(
            "PROPERTY_APPLICABILITY_CONFLICT",
            f"The change would hide stored property entries: {details}. "
            "Clear them or retain applicability first.",
        )


class CommandError(Exception):
    def __init__(self, code: str, detail: str, status: int = 409):
        self.code = code
        self.detail = detail
        self.status = status
        super().__init__(detail)


def allocate_alias(connection: Connection, site_id: UUID, entity_id: UUID) -> str:
    existing = connection.scalar(
        text("""
        SELECT value FROM iz.identifiers
        WHERE issuer_site_id = :site AND entity_id = :entity
    """),
        {"site": site_id, "entity": entity_id},
    )
    if existing is not None:
        return f"I{existing}"
    for _attempt in range(32):
        candidate = f"{secrets.randbelow(1_000_000):06d}"
        inserted = connection.scalar(
            text("""
            INSERT INTO iz.identifiers(id, namespace, issuer_site_id, value, entity_id)
            VALUES (:id, 'inventoryzing.local', :site, :value, :entity)
            ON CONFLICT DO NOTHING RETURNING value
        """),
            {"id": uuid4(), "site": site_id, "value": candidate, "entity": entity_id},
        )
        if inserted is not None:
            return f"I{inserted}"
    raise CommandError("ALIAS_ALLOCATION_EXHAUSTED", "No alias was allocated; try again.")


def resolve_identifier(connection: Connection, value: str) -> UUID:
    if re.fullmatch(r"I[0-9]{6}", value):
        found = connection.scalar(
            text("""
            SELECT identifier.entity_id FROM iz.identifiers identifier
            JOIN iz.sites site ON site.id = identifier.issuer_site_id AND site.local
            WHERE identifier.value = :value
        """),
            {"value": value[1:]},
        )
    else:
        try:
            identity = UUID(value)
        except ValueError as error:
            raise CommandError(
                "INVALID_IDENTIFIER", "Scan an I###### label or an object UUID.", 422
            ) from error
        found = connection.scalar(
            text("SELECT id FROM iz.objects WHERE id = :id"), {"id": identity}
        )
    if found is None:
        raise CommandError("NOT_FOUND", "No local object matches this identifier.", 404)
    return found


def lock_entity(connection: Connection, entity_id: UUID, command: Command, kind: str) -> dict:
    row = (
        connection.execute(
            text("""
        SELECT * FROM iz.entities WHERE id = :id FOR UPDATE
    """),
            {"id": entity_id},
        )
        .mappings()
        .first()
    )
    if row is None or row["kind"] != kind or row["archived_at"] is not None:
        raise CommandError("NOT_FOUND", "The requested entity is not available.", 404)
    if (
        row["write_site_id"] != command.authority_site
        or row["authority_epoch"] != command.authority_epoch
    ):
        raise CommandError("AUTHORITY_MISMATCH", "This command belongs to a different authority.")
    return dict(row)


def execute_command(engine: Engine, command: Command, actor_id: UUID) -> CommandResult:
    fingerprint = hashlib.sha256(
        json.dumps(
            {"actor": str(actor_id), **command.model_dump(mode="json")},
            sort_keys=True,
            separators=(",", ":"),
            ensure_ascii=True,
        ).encode()
    ).digest()
    identity = command.model_dump(exclude={"payload"})
    with engine.begin() as connection:
        site = connection.execute(text("SELECT * FROM iz.sites WHERE local")).mappings().one()
        if site["id"] != command.authority_site:
            raise CommandError("AUTHORITY_MISMATCH", "This is not the command's authority site.")
        epoch = connection.scalar(
            text("""
            SELECT iz.admit_epoch(:authority_site, :command_epoch)
        """),
            identity,
        )
        if epoch != "OPEN":
            raise CommandError(
                "COMMAND_EPOCH_EXPIRED", "This command epoch is no longer executable."
            )
        admitted = connection.scalar(
            text("""
            INSERT INTO iz.command_receipts(
                authority_site, authority_epoch, command_epoch, command_id, actor_id, fingerprint
            ) VALUES (
                :authority_site, :authority_epoch, :command_epoch, :command_id, :actor, :fingerprint
            ) ON CONFLICT DO NOTHING RETURNING command_id
        """),
            {**identity, "actor": actor_id, "fingerprint": fingerprint},
        )
        if admitted is None:
            receipt = (
                connection.execute(
                    text("""
                SELECT fingerprint, result FROM iz.command_receipts
                WHERE authority_site = :authority_site AND authority_epoch = :authority_epoch
                  AND command_epoch = :command_epoch AND command_id = :command_id
            """),
                    identity,
                )
                .mappings()
                .one()
            )
            if receipt["fingerprint"] != fingerprint:
                raise CommandError(
                    "IDEMPOTENCY_CONFLICT", "This command ID was used with different input."
                )
            return CommandResult.model_validate(receipt["result"])

        payload = command.payload
        if isinstance(payload, (CreateObject, CreateStockHolding, MoveObject, TransferStock)):
            connection.execute(text("SELECT pg_advisory_xact_lock(:key)"), {"key": GRAPH_LOCK})
        if isinstance(payload, (CreateTag, SetTagParents, DeleteDefinition)):
            connection.execute(text("SELECT pg_advisory_xact_lock(:key)"), {"key": TAG_GRAPH_LOCK})
        if isinstance(
            payload,
            (
                CreateObject,
                CreateStockHolding,
                CreateType,
                EditType,
                SetObjectType,
                SetTypePropertyDeclaration,
                SetPropertyValue,
                CreatePropertyDefinition,
                DeleteDefinition,
                EditPropertyDefinition,
                ConvertPropertyUnit,
                SetStockPolicy,
                ChangeStock,
                TransferStock,
            ),
        ):
            connection.execute(text("SELECT pg_advisory_xact_lock(:key)"), {"key": TYPE_GRAPH_LOCK})
        references: dict[UUID, str] = {}
        if isinstance(payload, (CreateObject, CreateType)) and payload.copy_properties_from:
            references[payload.copy_properties_from.id] = (
                "object" if isinstance(payload, CreateObject) else "object_type"
            )
        if isinstance(payload, DeleteDefinition):
            references[payload.entity_id] = payload.entity_kind
        if isinstance(payload, EditPropertyDefinition):
            references[payload.property_id] = "property_definition"
        if isinstance(payload, (EditObject, SetObjectType, MoveObject, AllocateAlias)):
            references[payload.object_id] = "object"
        if isinstance(payload, ChangeStock):
            references[payload.holding_id] = "object"
        if isinstance(payload, TransferStock):
            references[payload.source_holding_id] = "object"
            references[payload.destination_holding_id] = "object"
        if isinstance(payload, (EditTag, SetTagParents)):
            references[payload.tag_id] = "tag"
        if isinstance(payload, EditType):
            references[payload.type_id] = "object_type"
        if isinstance(payload, CreatePropertyDefinition) and payload.type_id:
            references[payload.type_id] = "object_type"
        if isinstance(payload, SetStockPolicy):
            references[payload.type_id] = "object_type"
        if isinstance(payload, SetTypePropertyDeclaration):
            references[payload.type_id] = "object_type"
            references[payload.property_id] = "property_definition"
        if isinstance(payload, (SetPropertyValue, ConvertPropertyUnit)):
            references[payload.target_id] = payload.target_kind
            references[payload.property_id] = "property_definition"
        if isinstance(payload, CreateObject):
            for entry in payload.property_values:
                references[entry.property_id] = "property_definition"
        if isinstance(payload, SetEntityTags):
            references[payload.entity_id] = payload.entity_kind
        if (
            isinstance(payload, (CreateObject, CreateStockHolding, MoveObject))
            and payload.parent_id
        ):
            references[payload.parent_id] = "object"
        if isinstance(payload, SetObjectType) and payload.object_type_id == payload.object_id:
            raise CommandError(
                "NOT_FOUND", "An object and its type must be different entities.", 404
            )
        if (
            isinstance(payload, (CreateObject, CreateStockHolding, SetObjectType))
            and payload.object_type_id
        ):
            references[payload.object_type_id] = "object_type"
        if isinstance(payload, (CreateType, EditType)) and payload.parent_type_id:
            references[payload.parent_type_id] = "object_type"
        if isinstance(payload, (CreateObject, CreateType, SetEntityTags)):
            for tag_id in payload.tag_ids:
                references[tag_id] = "tag"
        if isinstance(payload, (CreateTag, SetTagParents)):
            for parent_id in payload.parent_ids:
                references[parent_id] = "tag"
        locked = {
            entity_id: lock_entity(connection, entity_id, command, references[entity_id])
            for entity_id in sorted(references, key=str)
        }
        versioned_target = None
        expected_version = None
        if isinstance(payload, (EditObject, SetObjectType, MoveObject, AllocateAlias)):
            versioned_target = payload.object_id
            expected_version = payload.expected_version
        elif isinstance(payload, (EditTag, SetTagParents)):
            versioned_target = payload.tag_id
            expected_version = payload.expected_version
        elif isinstance(payload, EditType):
            versioned_target = payload.type_id
            expected_version = payload.expected_version
        elif isinstance(payload, CreatePropertyDefinition) and payload.type_id:
            versioned_target = payload.type_id
            expected_version = payload.expected_version
        elif isinstance(payload, SetTypePropertyDeclaration):
            versioned_target = payload.type_id
            expected_version = payload.expected_version
        elif isinstance(payload, (SetPropertyValue, ConvertPropertyUnit)):
            versioned_target = payload.target_id
            expected_version = payload.expected_version
        elif isinstance(payload, SetEntityTags):
            versioned_target = payload.entity_id
            expected_version = payload.expected_version
        if isinstance(payload, DeleteDefinition):
            versioned_target = payload.entity_id
            expected_version = payload.expected_version
        if isinstance(payload, EditPropertyDefinition):
            versioned_target = payload.property_id
            expected_version = payload.expected_version
        if isinstance(payload, SetStockPolicy):
            versioned_target = payload.type_id
            expected_version = payload.expected_version
        if isinstance(payload, ChangeStock):
            versioned_target = payload.holding_id
            expected_version = payload.expected_version
        if isinstance(payload, TransferStock):
            if locked[payload.source_holding_id]["version"] != payload.source_expected_version:
                raise CommandError(
                    "VERSION_CONFLICT", "The source holding changed. Refresh before transferring."
                )
            if (
                locked[payload.destination_holding_id]["version"]
                != payload.destination_expected_version
            ):
                raise CommandError(
                    "VERSION_CONFLICT",
                    "The destination holding changed. Refresh before transferring.",
                )
        if versioned_target is not None and expected_version is not None:
            if locked[versioned_target]["version"] != expected_version:
                raise CommandError(
                    "VERSION_CONFLICT", "The entity changed. Refresh before issuing a new command."
                )

        alias = None
        attached_type_version = None
        generated_key = None
        stock_movements: list[dict] = []
        additional_subjects: list[tuple[UUID, int]] = []
        if isinstance(
            payload,
            (CreateObject, CreateStockHolding, CreateType, CreateTag, CreatePropertyDefinition),
        ):
            if command.authority_epoch != 1:
                raise CommandError(
                    "AUTHORITY_MISMATCH", "New local entities start in authority epoch 1."
                )
            entity_id = uuid4()
            version = 1
            connection.execute(
                text("""
                INSERT INTO iz.entities(id, kind, home_site_id, write_site_id)
                VALUES (:id, :kind, :site, :site)
            """),
                {
                    "id": entity_id,
                    "kind": (
                        "object"
                        if isinstance(payload, (CreateObject, CreateStockHolding))
                        else "object_type"
                        if isinstance(payload, CreateType)
                        else "property_definition"
                        if isinstance(payload, CreatePropertyDefinition)
                        else "tag"
                    ),
                    "site": command.authority_site,
                },
            )
            if isinstance(payload, (CreateObject, CreateStockHolding)):
                if payload.object_type_id and connection.scalar(
                    text("SELECT abstract FROM iz.object_types WHERE id = :id"),
                    {"id": payload.object_type_id},
                ):
                    raise CommandError(
                        "ABSTRACT_TYPE", "Abstract object types cannot be assigned to objects."
                    )
                connection.execute(
                    text("""
                    INSERT INTO iz.objects(id, name, description, object_type_id)
                    VALUES (:id, :name, :description, :type)
                """),
                    {
                        "id": entity_id,
                        "name": payload.name,
                        "description": payload.description,
                        "type": payload.object_type_id,
                    },
                )
                connection.execute(
                    text("""
                    INSERT INTO iz.placements(object_id, parent_id, relation)
                    VALUES (:id, :parent, :relation)
                """),
                    {"id": entity_id, "parent": payload.parent_id, "relation": payload.relation},
                )
                if payload.allocate_alias:
                    alias = allocate_alias(connection, command.authority_site, entity_id)
                if isinstance(payload, CreateStockHolding):
                    policy = stock_policy_for_type(connection, payload.object_type_id)
                    amount = normalize_stock_amount(policy, payload.amount, payload.unit)
                    connection.execute(
                        text("""
                        INSERT INTO iz.stock_holdings(object_id, quantity, policy_type_id)
                        VALUES (:id, :quantity, :policy_type)
                    """),
                        {
                            "id": entity_id,
                            "quantity": amount,
                            "policy_type": policy["policy_type_id"],
                        },
                    )
                    stock_movements.append(
                        {
                            "holding": entity_id,
                            "operation": "initial",
                            "delta": amount,
                            "balance": amount,
                            "reason": "",
                            "counterparty": None,
                        }
                    )
            elif isinstance(payload, CreateType):
                connection.execute(
                    text("""
                    INSERT INTO iz.object_types(id, name, description, parent_type_id, abstract)
                    VALUES (:id, :name, :description, :parent, :abstract)
                """),
                    {
                        "id": entity_id,
                        "name": payload.name,
                        "description": payload.description,
                        "parent": payload.parent_type_id,
                        "abstract": payload.abstract,
                    },
                )
            elif isinstance(payload, CreateTag):
                connection.execute(
                    text("""
                    INSERT INTO iz.tags(id, name, description)
                    VALUES (:id, :name, :description)
                """),
                    {"id": entity_id, "name": payload.name, "description": payload.description},
                )
                if payload.parent_ids:
                    connection.execute(
                        text("""
                        INSERT INTO iz.tag_edges(child_id, parent_id) VALUES (:child, :parent)
                    """),
                        [
                            {"child": entity_id, "parent": parent_id}
                            for parent_id in payload.parent_ids
                        ],
                    )
            else:
                assert isinstance(payload, CreatePropertyDefinition)
                generated_key = payload.key or f"site.p_{entity_id.hex}"
                if connection.scalar(
                    text("SELECT EXISTS(SELECT 1 FROM iz.property_definitions WHERE key = :key)"),
                    {"key": generated_key},
                ):
                    raise CommandError(
                        "PROPERTY_KEY_EXISTS", "A property with this key already exists."
                    )
                if generated_key.split(".", 1)[0] in {"object", "labeling"}:
                    raise CommandError(
                        "PROPERTY_NAMESPACE_RESERVED",
                        "This namespace is reserved by an installed module.",
                        422,
                    )
                allowed_units = list(dict.fromkeys(payload.allowed_units))
                canonical_unit = None
                if payload.value_type == "quantity":
                    dimension = DIMENSIONS[payload.quantity_dimension]
                    if any(
                        unit not in {entry.id for entry in dimension.units}
                        for unit in allowed_units
                    ):
                        raise CommandError(
                            "INVALID_PROPERTY_SCHEMA",
                            "The property contains a unit incompatible with its dimension.",
                            422,
                        )
                    canonical_unit = dimension.canonical_unit
                    if canonical_unit not in allowed_units:
                        allowed_units.insert(0, canonical_unit)
                connection.execute(
                    text("""
                    INSERT INTO iz.property_definitions(
                        id, key, namespace, label, description, value_type,
                        quantity_dimension, canonical_unit, allowed_units)
                    VALUES (:id, :key, :namespace, :label, :description, :value_type,
                            :dimension, :canonical_unit, :allowed_units)
                """),
                    {
                        "id": entity_id,
                        "key": generated_key,
                        "namespace": generated_key.split(".", 1)[0],
                        "label": payload.label,
                        "description": payload.description,
                        "value_type": payload.value_type,
                        "dimension": payload.quantity_dimension,
                        "canonical_unit": canonical_unit,
                        "allowed_units": allowed_units,
                    },
                )
                if payload.type_id:
                    connection.execute(
                        text("""
                        INSERT INTO iz.type_property_declarations(type_id, property_id)
                        VALUES (:type, :property)
                    """),
                        {"type": payload.type_id, "property": entity_id},
                    )
                    attached_type_version = connection.scalar(
                        text("""
                        UPDATE iz.entities SET version = version + 1, updated_at = now()
                        WHERE id = :id RETURNING version
                    """),
                        {"id": payload.type_id},
                    )
            if isinstance(payload, (CreateObject, CreateType)) and payload.tag_ids:
                target_kind = "object" if isinstance(payload, CreateObject) else "object_type"
                connection.execute(
                    text("""
                    INSERT INTO iz.entity_tags(entity_id, target_kind, tag_id)
                    VALUES (:entity, :kind, :tag)
                """),
                    [
                        {"entity": entity_id, "kind": target_kind, "tag": tag_id}
                        for tag_id in payload.tag_ids
                    ],
                )
            if isinstance(payload, (CreateObject, CreateType)) and payload.copy_properties_from:
                source = payload.copy_properties_from
                if locked[source.id]["version"] != source.expected_version:
                    raise CommandError(
                        "VERSION_CONFLICT",
                        "The source changed. Reopen Duplicate to use its latest values.",
                    )
                target_kind = "object" if isinstance(payload, CreateObject) else "object_type"
                if isinstance(payload, CreateType):
                    connection.execute(
                        text("""
                        INSERT INTO iz.type_property_declarations(type_id, property_id)
                        SELECT :target, property_id FROM iz.type_property_declarations
                        WHERE type_id = :source
                    """),
                        {"target": entity_id, "source": source.id},
                    )
                copied = (
                    connection.execute(
                        text("""
                    SELECT property_id, state, value FROM iz.property_values
                    WHERE target_id = :source AND target_kind = :kind
                """),
                        {"source": source.id, "kind": target_kind},
                    )
                    .mappings()
                    .all()
                )
                for entry in copied:
                    ensure_property_applicable(
                        connection, entity_id, target_kind, entry["property_id"]
                    )
                    connection.execute(
                        text("""
                        INSERT INTO iz.property_values(
                            target_id, target_kind, property_id, state, value)
                        VALUES (:target, :kind, :property, :state, CAST(:value AS jsonb))
                    """),
                        {
                            "target": entity_id,
                            "kind": target_kind,
                            "property": entry["property_id"],
                            "state": entry["state"],
                            "value": json.dumps(entry["value"])
                            if entry["state"] == "value"
                            else None,
                        },
                    )
            if isinstance(payload, CreateObject):
                for entry in payload.property_values:
                    ensure_property_applicable(connection, entity_id, "object", entry.property_id)
                    definition = dict(
                        connection.execute(
                            text("""
                        SELECT * FROM iz.property_definitions WHERE id = :id
                    """),
                            {"id": entry.property_id},
                        )
                        .mappings()
                        .one()
                    )
                    normalized = (
                        normalize_property_value(definition, entry.value)
                        if entry.mode == "value"
                        else None
                    )
                    connection.execute(
                        text("""
                        INSERT INTO iz.property_values(
                            target_id, target_kind, property_id, state, value)
                        VALUES (:target, 'object', :property, :state, CAST(:value AS jsonb))
                        ON CONFLICT (target_id, property_id) DO UPDATE
                        SET state = EXCLUDED.state, value = EXCLUDED.value
                    """),
                        {
                            "target": entity_id,
                            "property": entry.property_id,
                            "state": entry.mode,
                            "value": json.dumps(normalized) if normalized is not None else None,
                        },
                    )
        else:
            if isinstance(payload, TransferStock):
                entity_id = payload.source_holding_id
            else:
                assert versioned_target is not None
                entity_id = versioned_target
            if isinstance(payload, DeleteDefinition):
                dependencies = {
                    "tag": [
                        ("SELECT count(*) FROM iz.entity_tags WHERE tag_id=:id", "assignments"),
                        ("SELECT count(*) FROM iz.tag_edges WHERE parent_id=:id", "child tags"),
                    ],
                    "object_type": [
                        ("SELECT count(*) FROM iz.objects WHERE object_type_id=:id", "objects"),
                        ("SELECT count(*) FROM iz.stock_policies WHERE type_id=:id",
                         "stock policy"),
                        (
                            "SELECT count(*) FROM iz.object_types t "
                            "JOIN iz.entities e ON e.id=t.id "
                            "WHERE t.parent_type_id=:id AND e.archived_at IS NULL",
                            "child types",
                        ),
                    ],
                    "property_definition": [
                        (
                            "SELECT count(*) FROM iz.type_property_declarations "
                            "WHERE property_id=:id",
                            "type declarations (remove these from their types first)",
                        ),
                        (
                            "SELECT count(*) FROM iz.property_values WHERE property_id=:id",
                            "stored values or explicit-unset entries",
                        ),
                    ],
                }
                for query, label in dependencies[payload.entity_kind]:
                    count = connection.scalar(text(query), {"id": entity_id})
                    if count:
                        raise CommandError("DEFINITION_IN_USE", f"Cannot delete: {count} {label}.")
                if payload.entity_kind == "tag":
                    connection.execute(
                        text("DELETE FROM iz.tag_edges WHERE child_id=:id"), {"id": entity_id}
                    )
                if payload.entity_kind == "object_type":
                    for table, column in (
                        ("entity_tags", "entity_id"),
                        ("type_property_declarations", "type_id"),
                        ("property_values", "target_id"),
                    ):
                        connection.execute(
                            text(f"DELETE FROM iz.{table} WHERE {column}=:id"), {"id": entity_id}
                        )
                connection.execute(
                    text("UPDATE iz.entities SET archived_at=now() WHERE id=:id"), {"id": entity_id}
                )
            elif isinstance(payload, EditPropertyDefinition):
                connection.execute(
                    text("""
                    UPDATE iz.property_definitions SET label=:label, description=:description
                    WHERE id=:id
                """),
                    {"id": entity_id, "label": payload.label, "description": payload.description},
                )
            elif isinstance(payload, ConvertPropertyUnit):
                from inventoryzing.properties import resolve_stored_properties
                from inventoryzing.units import display_amount

                fields = resolve_stored_properties(connection, entity_id, payload.target_kind)
                field = next((field for field in fields if field.id == payload.property_id), None)
                if field is None or field.type != "quantity" or field.local_state != "value":
                    raise CommandError(
                        "NO_LOCAL_QUANTITY", "Set a local quantity before converting."
                    )
                if payload.unit not in field.allowed_units:
                    raise CommandError("INVALID_PROPERTY_VALUE", "The unit is not allowed.", 422)
                value = dict(field.value)
                value["display_amount"] = display_amount(
                    value["amount"], field.quantity_dimension, payload.unit
                )
                value["display_unit"] = payload.unit
                connection.execute(
                    text("""
                    UPDATE iz.property_values SET value=CAST(:value AS jsonb), updated_at=now()
                    WHERE target_id=:id AND property_id=:property
                """),
                    {
                        "id": entity_id,
                        "property": payload.property_id,
                        "value": json.dumps(value),
                    },
                )
            elif isinstance(payload, EditObject):
                connection.execute(
                    text("""
                    UPDATE iz.objects SET name = :name, description = :description WHERE id = :id
                """),
                    {"id": entity_id, "name": payload.name, "description": payload.description},
                )
            elif isinstance(payload, SetObjectType):
                if payload.object_type_id and connection.scalar(
                    text("SELECT abstract FROM iz.object_types WHERE id = :id"),
                    {"id": payload.object_type_id},
                ):
                    raise CommandError(
                        "ABSTRACT_TYPE", "Abstract object types cannot be assigned to objects."
                    )
                connection.execute(
                    text("UPDATE iz.objects SET object_type_id = :type WHERE id = :id"),
                    {"id": entity_id, "type": payload.object_type_id},
                )
                ensure_all_property_entries_applicable(connection)
            elif isinstance(payload, EditType):
                if payload.parent_type_id:
                    cycle = connection.scalar(
                        text("""
                        WITH RECURSIVE ancestors(id) AS (
                            SELECT CAST(:parent AS uuid)
                            UNION
                            SELECT type.parent_type_id FROM iz.object_types type
                            JOIN ancestors ON type.id = ancestors.id
                            WHERE type.parent_type_id IS NOT NULL
                        ) SELECT EXISTS (SELECT 1 FROM ancestors WHERE id = :id)
                    """),
                        {"parent": payload.parent_type_id, "id": entity_id},
                    )
                    if cycle:
                        raise CommandError(
                            "TYPE_CYCLE",
                            "An object type cannot inherit from itself or one of its descendants.",
                        )
                if payload.abstract and connection.scalar(
                    text("SELECT EXISTS (SELECT 1 FROM iz.objects WHERE object_type_id = :id)"),
                    {"id": entity_id},
                ):
                    raise CommandError(
                        "TYPE_IN_USE",
                        "A type assigned to objects cannot be made abstract. "
                        "Reassign those objects first.",
                    )
                connection.execute(
                    text("""
                    UPDATE iz.object_types
                    SET name = :name, description = :description,
                        parent_type_id = :parent, abstract = :abstract
                    WHERE id = :id
                """),
                    {
                        "id": entity_id,
                        "name": payload.name,
                        "description": payload.description,
                        "parent": payload.parent_type_id,
                        "abstract": payload.abstract,
                    },
                )
                ensure_all_property_entries_applicable(connection)
            elif isinstance(payload, SetTypePropertyDeclaration):
                if payload.applicable:
                    connection.execute(
                        text("""
                        INSERT INTO iz.type_property_declarations(type_id, property_id)
                        VALUES (:type, :property) ON CONFLICT DO NOTHING
                    """),
                        {"type": entity_id, "property": payload.property_id},
                    )
                else:
                    connection.execute(
                        text("""
                        DELETE FROM iz.type_property_declarations
                        WHERE type_id = :type AND property_id = :property
                    """),
                        {"type": entity_id, "property": payload.property_id},
                    )
                    ensure_all_property_entries_applicable(connection)
            elif isinstance(payload, SetPropertyValue):
                ensure_property_applicable(
                    connection, entity_id, payload.target_kind, payload.property_id
                )
                if payload.mode == "inherit":
                    connection.execute(
                        text("""
                        DELETE FROM iz.property_values
                        WHERE target_id = :target AND property_id = :property
                    """),
                        {"target": entity_id, "property": payload.property_id},
                    )
                else:
                    definition = dict(
                        connection.execute(
                            text("""
                        SELECT * FROM iz.property_definitions WHERE id = :id
                    """),
                            {"id": payload.property_id},
                        )
                        .mappings()
                        .one()
                    )
                    normalized = (
                        normalize_property_value(definition, payload.value)
                        if payload.mode == "value"
                        else None
                    )
                    connection.execute(
                        text("""
                        INSERT INTO iz.property_values(
                            target_id, target_kind, property_id, state, value)
                        VALUES (:target, :kind, :property, :state, CAST(:value AS jsonb))
                        ON CONFLICT (target_id, property_id) DO UPDATE
                        SET state = excluded.state, value = excluded.value, updated_at = now()
                    """),
                        {
                            "target": entity_id,
                            "kind": payload.target_kind,
                            "property": payload.property_id,
                            "state": payload.mode,
                            "value": json.dumps(normalized) if normalized is not None else None,
                        },
                    )
            elif isinstance(payload, MoveObject):
                cycle = connection.scalar(
                    text("""
                    WITH RECURSIVE ancestors(id) AS (
                        SELECT CAST(:parent AS uuid)
                        UNION
                        SELECT placement.parent_id FROM iz.placements placement
                        JOIN ancestors ON placement.object_id = ancestors.id
                        WHERE placement.parent_id IS NOT NULL
                    ) SELECT EXISTS (SELECT 1 FROM ancestors WHERE id = :id)
                """),
                    {"parent": payload.parent_id, "id": entity_id},
                )
                if cycle:
                    raise CommandError(
                        "PLACEMENT_CYCLE",
                        "An object cannot be placed inside itself or its descendants.",
                    )
                connection.execute(
                    text("""
                    UPDATE iz.placements SET parent_id = :parent, relation = :relation
                    WHERE object_id = :id
                """),
                    {"id": entity_id, "parent": payload.parent_id, "relation": payload.relation},
                )
            elif isinstance(payload, AllocateAlias):
                alias = allocate_alias(connection, command.authority_site, entity_id)
            elif isinstance(payload, EditTag):
                connection.execute(
                    text("""
                    UPDATE iz.tags SET name = :name, description = :description WHERE id = :id
                """),
                    {"id": entity_id, "name": payload.name, "description": payload.description},
                )
            elif isinstance(payload, SetTagParents):
                if payload.parent_ids:
                    cycle = connection.scalar(
                        text("""
                        WITH RECURSIVE ancestors(id) AS (
                            SELECT unnest(CAST(:parents AS uuid[]))
                            UNION
                            SELECT edge.parent_id FROM iz.tag_edges edge
                            JOIN ancestors ON edge.child_id = ancestors.id
                        )
                        SELECT EXISTS (SELECT 1 FROM ancestors WHERE id = :id)
                    """),
                        {"parents": payload.parent_ids, "id": entity_id},
                    )
                    if cycle:
                        raise CommandError(
                            "TAG_CYCLE",
                            "A tag cannot inherit from itself or one of its descendants.",
                        )
                connection.execute(
                    text("DELETE FROM iz.tag_edges WHERE child_id = :id"), {"id": entity_id}
                )
                if payload.parent_ids:
                    connection.execute(
                        text("""
                        INSERT INTO iz.tag_edges(child_id, parent_id) VALUES (:child, :parent)
                    """),
                        [
                            {"child": entity_id, "parent": parent_id}
                            for parent_id in payload.parent_ids
                        ],
                    )
            elif isinstance(payload, SetEntityTags):
                connection.execute(
                    text("DELETE FROM iz.entity_tags WHERE entity_id = :id"), {"id": entity_id}
                )
                if payload.tag_ids:
                    connection.execute(
                        text("""
                        INSERT INTO iz.entity_tags(entity_id, target_kind, tag_id)
                        VALUES (:entity, :kind, :tag)
                    """),
                        [
                            {"entity": entity_id, "kind": payload.entity_kind, "tag": tag_id}
                            for tag_id in payload.tag_ids
                        ],
                    )
            elif isinstance(payload, SetStockPolicy):
                if connection.scalar(
                    text("SELECT EXISTS(SELECT 1 FROM iz.stock_holdings WHERE policy_type_id=:id)"),
                    {"id": entity_id},
                ):
                    raise CommandError(
                        "STOCK_POLICY_IN_USE",
                        "This stock policy already has holdings and cannot be changed.",
                    )
                dimension = DIMENSIONS[payload.quantity_dimension]
                if payload.unit not in {entry.id for entry in dimension.units}:
                    raise CommandError(
                        "INVALID_STOCK_UNIT",
                        "The stock unit is incompatible with its dimension.",
                        422,
                    )
                granularity = _positive_decimal(payload.granularity, "INVALID_STOCK_GRANULARITY")
                canonical_granularity = Decimal(
                    normalize_amount(format(granularity, "f"), dimension.id, payload.unit)
                )
                connection.execute(
                    text("""
                    INSERT INTO iz.stock_policies(type_id, quantity_dimension, canonical_unit,
                                                  granularity, allow_negative)
                    VALUES (:type, :dimension, :unit, :granularity, :allow_negative)
                    ON CONFLICT (type_id) DO UPDATE SET
                      quantity_dimension=excluded.quantity_dimension,
                      canonical_unit=excluded.canonical_unit,
                      granularity=excluded.granularity,
                      allow_negative=excluded.allow_negative
                """),
                    {
                        "type": entity_id,
                        "dimension": dimension.id,
                        "unit": dimension.canonical_unit,
                        "granularity": canonical_granularity,
                        "allow_negative": payload.allow_negative,
                    },
                )
            elif isinstance(payload, ChangeStock):
                holding = stock_holding_for_update(connection, entity_id)
                amount = (
                    normalize_stock_amount(holding, payload.amount, payload.unit)
                    if payload.kind != "stock.adjust"
                    else None
                )
                delta = (
                    amount
                    if payload.kind == "stock.receive"
                    else -amount
                    if payload.kind == "stock.consume"
                    else Decimal(_decimal_string(payload.amount))
                )
                if payload.kind == "stock.adjust":
                    if delta == 0:
                        raise CommandError(
                            "INVALID_STOCK_AMOUNT", "An adjustment cannot be zero.", 422
                        )
                    signed_unit = payload.unit
                    magnitude = normalize_stock_amount(
                        holding, format(abs(delta), "f"), signed_unit
                    )
                    delta = magnitude if delta > 0 else -magnitude
                next_balance = Decimal(holding["quantity"]) + delta
                if not holding["allow_negative"] and next_balance < 0:
                    raise CommandError(
                        "INSUFFICIENT_STOCK", "This operation would make stock negative.", 409
                    )
                connection.execute(
                    text("UPDATE iz.stock_holdings SET quantity=:quantity WHERE object_id=:id"),
                    {"id": entity_id, "quantity": next_balance},
                )
                stock_movements.append(
                    {
                        "holding": entity_id,
                        "operation": {
                            "stock.receive": "receive",
                            "stock.consume": "consume",
                            "stock.adjust": "adjust",
                        }[payload.kind],
                        "delta": delta,
                        "balance": next_balance,
                        "reason": payload.reason,
                        "counterparty": None,
                    }
                )
            elif isinstance(payload, TransferStock):
                if payload.source_holding_id == payload.destination_holding_id:
                    raise CommandError(
                        "INCOMPATIBLE_STOCK_HOLDINGS",
                        "A holding cannot transfer stock to itself.",
                        422,
                    )
                source = stock_holding_for_update(connection, payload.source_holding_id)
                destination = stock_holding_for_update(connection, payload.destination_holding_id)
                if source["policy_type_id"] != destination["policy_type_id"]:
                    raise CommandError(
                        "INCOMPATIBLE_STOCK_HOLDINGS",
                        "Stock may only transfer between holdings with the same policy.",
                        409,
                    )
                amount = normalize_stock_amount(source, payload.amount, payload.unit)
                source_balance = Decimal(source["quantity"]) - amount
                if not source["allow_negative"] and source_balance < 0:
                    raise CommandError(
                        "INSUFFICIENT_STOCK",
                        "This transfer would make the source stock negative.",
                        409,
                    )
                destination_balance = Decimal(destination["quantity"]) + amount
                connection.execute(
                    text("UPDATE iz.stock_holdings SET quantity=:quantity WHERE object_id=:id"),
                    [
                        {"id": payload.source_holding_id, "quantity": source_balance},
                        {"id": payload.destination_holding_id, "quantity": destination_balance},
                    ],
                )
                destination_version = connection.scalar(
                    text("""
                    UPDATE iz.entities SET version=version + 1, updated_at=now()
                    WHERE id=:id RETURNING version
                """),
                    {"id": payload.destination_holding_id},
                )
                additional_subjects.append((payload.destination_holding_id, destination_version))
                stock_movements.extend(
                    [
                        {
                            "holding": payload.source_holding_id,
                            "operation": "transfer_out",
                            "delta": -amount,
                            "balance": source_balance,
                            "reason": payload.reason,
                            "counterparty": payload.destination_holding_id,
                        },
                        {
                            "holding": payload.destination_holding_id,
                            "operation": "transfer_in",
                            "delta": amount,
                            "balance": destination_balance,
                            "reason": payload.reason,
                            "counterparty": payload.source_holding_id,
                        },
                    ]
                )
            version = connection.scalar(
                text("""
                UPDATE iz.entities SET version = version + 1, updated_at = now()
                WHERE id = :id RETURNING version
            """),
                {"id": entity_id},
            )

        event_id = uuid4()
        event_payload = {
            **payload.model_dump(mode="json"),
            "entity_id": str(entity_id),
            "alias": alias,
        }
        if generated_key is not None:
            event_payload["key"] = generated_key
        connection.execute(
            text("""
            INSERT INTO iz.domain_events(id, source_site_id, source_incarnation, event_type,
                actor_id, command_id, authority_epoch, command_epoch, payload)
            VALUES (:id, :authority_site, :incarnation, :kind, :actor, :command_id,
                :authority_epoch, :command_epoch, CAST(:payload AS jsonb))
        """),
            {
                **identity,
                "id": event_id,
                "incarnation": site["incarnation"],
                "kind": payload.kind,
                "actor": actor_id,
                "payload": json.dumps(event_payload),
            },
        )
        connection.execute(
            text("""
            INSERT INTO iz.event_subjects(event_id, entity_id, version)
            VALUES (:event, :entity, :version)
        """),
            {"event": event_id, "entity": entity_id, "version": version},
        )
        if attached_type_version is not None:
            connection.execute(
                text("""
                INSERT INTO iz.event_subjects(event_id, entity_id, version)
                VALUES (:event, :entity, :version)
            """),
                {"event": event_id, "entity": payload.type_id, "version": attached_type_version},
            )
        for subject_id, subject_version in additional_subjects:
            connection.execute(
                text("""
                INSERT INTO iz.event_subjects(event_id, entity_id, version)
                VALUES (:event, :entity, :version)
            """),
                {"event": event_id, "entity": subject_id, "version": subject_version},
            )
        for movement in stock_movements:
            connection.execute(
                text("""
                INSERT INTO iz.stock_movements(
                    id, holding_id, event_id, operation, delta, balance_after, reason,
                    counterparty_holding_id)
                VALUES (:id, :holding, :event, :operation, :delta, :balance, :reason,
                        :counterparty)
            """),
                {"id": uuid4(), "event": event_id, **movement},
            )
        connection.execute(
            text("INSERT INTO iz.replication_outbox(event_id) VALUES (:event)"), {"event": event_id}
        )
        result = CommandResult(
            command_id=command.command_id,
            entity_id=entity_id,
            version=version,
            event_id=event_id,
            alias=alias,
        )
        connection.execute(
            text("""
            UPDATE iz.command_receipts SET result = CAST(:result AS jsonb)
            WHERE authority_site = :authority_site AND authority_epoch = :authority_epoch
              AND command_epoch = :command_epoch AND command_id = :command_id
        """),
            {**identity, "result": result.model_dump_json()},
        )
    return result
