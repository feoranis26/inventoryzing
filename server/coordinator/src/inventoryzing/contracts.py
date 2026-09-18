from datetime import datetime
from typing import Annotated, Literal
from uuid import UUID

from pydantic import (
    AfterValidator,
    BaseModel,
    ConfigDict,
    Field,
    JsonValue,
    StringConstraints,
    model_validator,
)

Name = Annotated[str, StringConstraints(strip_whitespace=True, min_length=1, max_length=160)]


def unique_ids(values: list[UUID]) -> list[UUID]:
    if len(values) != len(set(values)):
        raise ValueError("IDs must be unique")
    return sorted(values)


UniqueIds = Annotated[list[UUID], AfterValidator(unique_ids)]


class StrictModel(BaseModel):
    model_config = ConfigDict(extra="forbid")


class PropertyCopySource(StrictModel):
    id: UUID
    expected_version: int = Field(gt=0)


class CreateObject(StrictModel):
    kind: Literal["object.create"]
    name: Name
    description: str = Field(default="", max_length=10000)
    object_type_id: UUID | None = None
    parent_id: UUID | None = None
    relation: Literal["contained_in", "installed_in", "mounted_in", "located_in"] = "contained_in"
    tag_ids: UniqueIds = Field(default_factory=list, max_length=100)
    property_values: list["CreateObjectPropertyValue"] = Field(default_factory=list, max_length=100)
    allocate_alias: bool = True
    copy_properties_from: PropertyCopySource | None = None
    stock_amount: str | None = Field(default=None, min_length=1, max_length=100)
    stock_unit: str | None = Field(default=None, min_length=1, max_length=40)

    @model_validator(mode="after")
    def property_ids_are_unique(self):
        if len({entry.property_id for entry in self.property_values}) != len(self.property_values):
            raise ValueError("Object property IDs must be unique.")
        if (self.stock_amount is None) != (self.stock_unit is None):
            raise ValueError("Initial stock amount and unit must be supplied together.")
        return self


class CreateObjectPropertyValue(StrictModel):
    property_id: UUID
    mode: Literal["unset", "value"]
    value: JsonValue | None = None

    @model_validator(mode="after")
    def value_matches_mode(self):
        if (self.mode == "value") != (self.value is not None):
            raise ValueError("Value mode requires a value; unset does not accept one.")
        return self


class EditObject(StrictModel):
    kind: Literal["object.edit"]
    object_id: UUID
    expected_version: int = Field(gt=0)
    name: Name
    description: str = Field(default="", max_length=10000)


class SetObjectType(StrictModel):
    kind: Literal["object.type.set"]
    object_id: UUID
    expected_version: int = Field(gt=0)
    object_type_id: UUID | None


class MoveObject(StrictModel):
    kind: Literal["object.move"]
    object_id: UUID
    expected_version: int = Field(gt=0)
    parent_id: UUID | None
    relation: Literal["contained_in", "installed_in", "mounted_in", "located_in"] = "contained_in"


class AllocateAlias(StrictModel):
    kind: Literal["identifier.allocate"]
    object_id: UUID
    expected_version: int = Field(gt=0)


class CreateType(StrictModel):
    kind: Literal["type.create"]
    name: Name
    description: str = Field(default="", max_length=10000)
    parent_type_id: UUID | None = None
    abstract: bool = False
    tag_ids: UniqueIds = Field(default_factory=list, max_length=100)
    copy_properties_from: PropertyCopySource | None = None


class EditType(StrictModel):
    kind: Literal["type.edit"]
    type_id: UUID
    expected_version: int = Field(gt=0)
    name: Name
    description: str = Field(default="", max_length=10000)
    parent_type_id: UUID | None = None
    abstract: bool = False


class CreatePropertyDefinition(StrictModel):
    kind: Literal["property.definition.create"]
    key: str | None = Field(
        default=None, pattern=r"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$", max_length=200
    )
    type_id: UUID | None = None
    expected_version: int | None = Field(default=None, gt=0)
    label: Name
    description: str = Field(default="", max_length=10000)
    value_type: Literal["text", "integer", "decimal", "boolean", "date", "datetime", "quantity"]
    quantity_dimension: Literal["count", "volume", "length", "mass", "area"] | None = None
    allowed_units: list[str] = Field(default_factory=list, max_length=32)

    @model_validator(mode="after")
    def quantity_schema_is_complete(self):
        if (self.type_id is None) != (self.expected_version is None):
            raise ValueError("Type attachment requires both type ID and expected version.")
        if self.value_type == "quantity" and (
            self.quantity_dimension is None or not self.allowed_units
        ):
            raise ValueError("Quantity properties require a dimension and allowed units.")
        if self.value_type != "quantity" and (
            self.quantity_dimension is not None or self.allowed_units
        ):
            raise ValueError("Only quantity properties may define dimensions or units.")
        return self


class SetTypePropertyDeclaration(StrictModel):
    kind: Literal["type.property.declare"]
    type_id: UUID
    expected_version: int = Field(gt=0)
    property_id: UUID
    applicable: bool


class SetPropertyValue(StrictModel):
    kind: Literal["property.value.set"]
    target_id: UUID
    target_kind: Literal["object", "object_type"]
    expected_version: int = Field(gt=0)
    property_id: UUID
    mode: Literal["inherit", "unset", "value"]
    value: JsonValue | None = None

    @model_validator(mode="after")
    def value_matches_mode(self):
        if (self.mode == "value") != (self.value is not None):
            raise ValueError("Value mode requires a value; inherit and unset do not accept one.")
        return self


class EditPropertyDefinition(StrictModel):
    kind: Literal["property.definition.edit"]
    property_id: UUID
    expected_version: int = Field(gt=0)
    label: Name
    description: str = Field(default="", max_length=10000)


class DeleteDefinition(StrictModel):
    kind: Literal["definition.delete"]
    entity_id: UUID
    entity_kind: Literal["tag", "object_type", "property_definition"]
    expected_version: int = Field(gt=0)


class ConvertPropertyUnit(StrictModel):
    kind: Literal["property.unit.set"]
    target_id: UUID
    target_kind: Literal["object", "object_type"]
    expected_version: int = Field(gt=0)
    property_id: UUID
    unit: str = Field(max_length=40)


class CreateTag(StrictModel):
    kind: Literal["tag.create"]
    name: Name
    description: str = Field(default="", max_length=10000)
    parent_ids: UniqueIds = Field(default_factory=list, max_length=100)


class EditTag(StrictModel):
    kind: Literal["tag.edit"]
    tag_id: UUID
    expected_version: int = Field(gt=0)
    name: Name
    description: str = Field(default="", max_length=10000)


class SetTagParents(StrictModel):
    kind: Literal["tag.parents.set"]
    tag_id: UUID
    expected_version: int = Field(gt=0)
    parent_ids: UniqueIds = Field(default_factory=list, max_length=100)


class SetEntityTags(StrictModel):
    kind: Literal["tags.set"]
    entity_id: UUID
    entity_kind: Literal["object", "object_type"]
    expected_version: int = Field(gt=0)
    tag_ids: UniqueIds = Field(default_factory=list, max_length=100)


class SetStockPolicy(StrictModel):
    kind: Literal["stock.policy.set"]
    type_id: UUID
    expected_version: int = Field(gt=0)
    quantity_dimension: Literal["count", "volume", "length", "mass", "area"]
    unit: str = Field(min_length=1, max_length=40)
    granularity: str = Field(default="1", max_length=100)
    allow_negative: bool = False


class CreateStockHolding(StrictModel):
    kind: Literal["stock.holding.create"]
    name: Name
    description: str = Field(default="", max_length=10000)
    object_type_id: UUID
    parent_id: UUID | None = None
    relation: Literal["contained_in", "installed_in", "mounted_in", "located_in"] = "contained_in"
    amount: str = Field(min_length=1, max_length=100)
    unit: str = Field(min_length=1, max_length=40)
    allocate_alias: bool = True


class ChangeStock(StrictModel):
    kind: Literal["stock.receive", "stock.consume", "stock.adjust"]
    holding_id: UUID
    expected_version: int = Field(gt=0)
    amount: str = Field(min_length=1, max_length=100)
    unit: str = Field(min_length=1, max_length=40)
    reason: str = Field(default="", max_length=1000)


class TransferStock(StrictModel):
    kind: Literal["stock.transfer"]
    source_holding_id: UUID
    source_expected_version: int = Field(gt=0)
    destination_holding_id: UUID
    destination_expected_version: int = Field(gt=0)
    amount: str = Field(min_length=1, max_length=100)
    unit: str = Field(min_length=1, max_length=40)
    reason: str = Field(default="", max_length=1000)


class SplitStock(StrictModel):
    kind: Literal["stock.split"]
    source_holding_id: UUID
    source_expected_version: int = Field(gt=0)
    name: Name
    description: str = Field(default="", max_length=10000)
    parent_id: UUID | None = None
    relation: Literal["contained_in", "installed_in", "mounted_in", "located_in"] | None = None
    amount: str = Field(min_length=1, max_length=100)
    unit: str = Field(min_length=1, max_length=40)
    reason: str = Field(default="", max_length=1000)
    allocate_alias: bool = True


Payload = Annotated[
    CreateObject
    | EditObject
    | SetObjectType
    | MoveObject
    | AllocateAlias
    | CreateType
    | EditType
    | CreatePropertyDefinition
    | SetTypePropertyDeclaration
    | SetPropertyValue
    | EditPropertyDefinition
    | DeleteDefinition
    | ConvertPropertyUnit
    | CreateTag
    | EditTag
    | SetTagParents
    | SetEntityTags
    | SetStockPolicy
    | CreateStockHolding
    | ChangeStock
    | TransferStock
    | SplitStock,
    Field(discriminator="kind"),
]


class Command(StrictModel):
    authority_site: UUID
    authority_epoch: int = Field(gt=0)
    command_epoch: int = Field(gt=0)
    command_id: UUID
    payload: Payload


class CommandResult(StrictModel):
    command_id: UUID
    entity_id: UUID
    version: int
    event_id: UUID
    alias: str | None = None


class LocationSegment(BaseModel):
    id: UUID
    name: str


class ObjectView(BaseModel):
    id: UUID
    name: str
    description: str
    object_type_id: UUID | None
    type_name: str | None
    parent_id: UUID | None
    parent_name: str | None
    location_path: list[LocationSegment]
    child_count: int
    relation: str
    alias: str | None
    authority_epoch: int
    version: int
    updated_at: datetime
    stock: "StockHoldingView | None" = None


class StockHoldingView(BaseModel):
    quantity: str
    quantity_dimension: Literal["count", "volume", "length", "mass", "area"]
    canonical_unit: str
    granularity: str
    allow_negative: bool
    policy_type_id: UUID
    policy_type_name: str


class StockPolicyView(BaseModel):
    type_id: UUID
    policy_type_id: UUID
    policy_type_name: str
    quantity_dimension: Literal["count", "volume", "length", "mass", "area"]
    canonical_unit: str
    granularity: str
    allow_negative: bool


class ObjectPage(BaseModel):
    items: list[ObjectView]
    total: int


class TypeView(BaseModel):
    id: UUID
    name: str
    description: str
    parent_type_id: UUID | None
    parent_name: str | None
    abstract: bool
    version: int
    stock_policy: "StockPolicyView | None" = None


class TagView(BaseModel):
    id: UUID
    name: str
    description: str
    parent_ids: list[UUID]
    direct_object_count: int
    direct_type_count: int
    version: int


class TagSource(BaseModel):
    source_kind: Literal["object", "object_type"]
    tag_id: UUID
    tag_name: str


class EffectiveTagView(BaseModel):
    id: UUID
    name: str
    sources: list[TagSource]


class EntityTagsView(BaseModel):
    explicit_tag_ids: list[UUID]
    effective_tags: list[EffectiveTagView]


class HistoryEntry(BaseModel):
    id: UUID
    event_type: str
    occurred_at: datetime
    actor: str | None
    version: int
    payload: dict


class SessionView(BaseModel):
    account_id: UUID
    login: str
    csrf_token: str
    site_id: UUID
    site_name: str
    command_epoch: int
    permissions: list[str]


class SiteSettingsView(BaseModel):
    id: UUID
    display_name: str
    settings_version: int
    has_logo: bool
    logo_version: int | None = None


class UpdateSiteSettings(StrictModel):
    display_name: Name
    expected_version: int = Field(gt=0)


class UpdateSiteLogo(StrictModel):
    logo_base64: str = Field(min_length=1, max_length=3_000_000)
    expected_version: int = Field(gt=0)


class RoleView(BaseModel):
    id: UUID
    name: str
    description: str
    version: int
    permissions: list[str]
    assigned_account_count: int


class AccountView(BaseModel):
    id: UUID
    login: str
    principal_id: UUID | None
    principal_name: str | None
    disabled: bool
    roles: list[UUID]
    permissions: list[str]
    version: int


class CreateRole(StrictModel):
    name: Name
    description: str = Field(default="", max_length=10_000)
    permissions: list[str] = Field(default_factory=list, max_length=200)


class UpdateRole(StrictModel):
    name: Name
    description: str = Field(default="", max_length=10_000)
    permissions: list[str] = Field(default_factory=list, max_length=200)
    expected_version: int = Field(gt=0)


class CreateAccount(StrictModel):
    login: Name
    password: str = Field(min_length=12, max_length=1024)
    principal_id: UUID | None = None
    role_ids: list[UUID] = Field(default_factory=list, max_length=100)


class UpdateAccount(StrictModel):
    principal_id: UUID | None = None
    role_ids: list[UUID] = Field(default_factory=list, max_length=100)
    disabled: bool
    expected_version: int = Field(gt=0)


class ResetAccountPassword(StrictModel):
    password: str = Field(min_length=12, max_length=1024)
    expected_version: int = Field(gt=0)


class PrincipalView(BaseModel):
    id: UUID
    principal_kind: Literal["person", "team", "organization", "project", "site"]
    display_name: str
    version: int
    linked_account_count: int
    archived: bool


class CreatePrincipal(StrictModel):
    principal_kind: Literal["person", "team", "organization", "project", "site"]
    display_name: Name


class UpdatePrincipal(StrictModel):
    principal_kind: Literal["person", "team", "organization", "project", "site"]
    display_name: Name
    expected_version: int = Field(gt=0)


class Login(StrictModel):
    login: str = Field(min_length=1, max_length=160)
    password: str = Field(min_length=1, max_length=1024)
