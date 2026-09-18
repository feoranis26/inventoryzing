import base64
import hashlib
from io import BytesIO
from uuid import UUID, uuid4

from fastapi import APIRouter, HTTPException, Query, Request, Response
from PIL import Image, UnidentifiedImageError
from sqlalchemy import text

from inventoryzing.auth import authenticate, login, password_hasher
from inventoryzing.commands import PERMISSIONS, execute_command, resolve_identifier
from inventoryzing.contracts import (
    AccountView,
    Command,
    CommandResult,
    CreateAccount,
    CreatePrincipal,
    CreateRole,
    EffectiveTagView,
    EntityTagsView,
    HistoryEntry,
    Login,
    ObjectPage,
    ObjectView,
    PrincipalView,
    ResetAccountPassword,
    RoleView,
    SessionView,
    SiteSettingsView,
    StockPolicyView,
    TagSource,
    TagView,
    TypeView,
    UpdateAccount,
    UpdatePrincipal,
    UpdateRole,
    UpdateSiteLogo,
    UpdateSiteSettings,
)

router = APIRouter(prefix="/api")

MAX_SITE_LOGO_PIXELS = 4_000_000
MAX_SITE_LOGO_DIMENSION = 2_048


def site_settings_view(row) -> SiteSettingsView:
    logo = row["logo_png"]
    return SiteSettingsView(
        id=row["id"],
        display_name=row["display_name"],
        settings_version=row["settings_version"],
        has_logo=logo is not None,
        logo_version=row["settings_version"] if logo is not None else None,
    )


def normalized_site_logo(encoded: str) -> bytes:
    try:
        raw = base64.b64decode(encoded, validate=True)
        with Image.open(BytesIO(raw)) as source:
            source.load()
            if source.width * source.height > MAX_SITE_LOGO_PIXELS:
                raise ValueError("The logo has too many pixels.")
            if max(source.size) > MAX_SITE_LOGO_DIMENSION:
                source.thumbnail(
                    (MAX_SITE_LOGO_DIMENSION, MAX_SITE_LOGO_DIMENSION), Image.Resampling.LANCZOS
                )
            image = source.convert("RGBA")
    except (ValueError, UnidentifiedImageError, OSError) as error:
        raise HTTPException(422, "Upload a valid PNG or JPEG logo image.") from error
    output = BytesIO()
    image.save(output, format="PNG", optimize=True)
    if output.tell() > 1_500_000:
        raise HTTPException(422, "The normalized logo is too large.")
    return output.getvalue()


def record_administration_event(connection, actor_id: UUID, event_type: str, payload: str) -> None:
    site = connection.execute(text("SELECT * FROM iz.sites WHERE local")).mappings().one()
    epoch = connection.scalar(
        text("SELECT epoch FROM iz.command_epochs WHERE site_id=:site AND state='OPEN'"),
        {"site": site["id"]},
    )
    event_id = uuid4()
    connection.execute(
        text("""
        INSERT INTO iz.domain_events(id, source_site_id, source_incarnation, event_type,
            actor_id, command_id, authority_epoch, command_epoch, payload)
        VALUES (:event, :site, :incarnation, :event_type, :actor, :command, 1, :epoch,
                CAST(:payload AS jsonb))
    """),
        {
            "event": event_id,
            "site": site["id"],
            "incarnation": site["incarnation"],
            "event_type": event_type,
            "actor": actor_id,
            "command": uuid4(),
            "epoch": epoch,
            "payload": payload,
        },
    )
    connection.execute(
        text("INSERT INTO iz.replication_outbox(event_id) VALUES (:event)"), {"event": event_id}
    )


def role_view(connection, role_id: UUID) -> RoleView:
    row = (
        connection.execute(
            text("""
        SELECT role.id, role.name, role.description, role.version,
               COALESCE(array_agg(DISTINCT permission.permission)
                   FILTER (WHERE permission.permission IS NOT NULL), '{}') AS permissions,
               count(DISTINCT assignment.account_id) AS assigned_account_count
        FROM iz.roles role
        LEFT JOIN iz.role_permissions permission ON permission.role_id=role.id
        LEFT JOIN iz.account_roles assignment ON assignment.role_id=role.id
        WHERE role.id=:id
        GROUP BY role.id
    """),
            {"id": role_id},
        )
        .mappings()
        .first()
    )
    if row is None:
        raise HTTPException(404, "Role not found.")
    return RoleView(**row)


def account_view(connection, account_id: UUID) -> AccountView:
    row = (
        connection.execute(
            text("""
        SELECT account.id, account.login, account.principal_id,
               principal.display_name AS principal_name,
               account.disabled, entity.version,
               COALESCE(array_agg(DISTINCT assignment.role_id)
                   FILTER (WHERE assignment.role_id IS NOT NULL), '{}') AS roles,
               COALESCE(array_agg(DISTINCT permission.permission)
                   FILTER (WHERE permission.permission IS NOT NULL), '{}') AS permissions
        FROM iz.accounts account
        JOIN iz.entities entity ON entity.id=account.id
        LEFT JOIN iz.principals principal ON principal.id=account.principal_id
        LEFT JOIN iz.account_roles assignment ON assignment.account_id=account.id
        LEFT JOIN iz.role_permissions permission ON permission.role_id=assignment.role_id
        WHERE account.id=:id AND account.account_kind='human'
        GROUP BY account.id, entity.version, principal.display_name
    """),
            {"id": account_id},
        )
        .mappings()
        .first()
    )
    if row is None:
        raise HTTPException(404, "Human account not found.")
    return AccountView(**row)


def validate_role_ids(connection, role_ids: list[UUID]) -> None:
    if len(set(role_ids)) != len(role_ids):
        raise HTTPException(422, "Roles must not be repeated.")
    found = (
        set(
            connection.scalars(
                text("SELECT id FROM iz.roles WHERE id = ANY(:ids)"),
                {
                    "ids": role_ids,
                },
            )
        )
        if role_ids
        else set()
    )
    if found != set(role_ids):
        raise HTTPException(422, "One or more roles do not exist.")


def validate_permissions(connection, permissions: list[str]) -> list[str]:
    selected = sorted(set(permissions))
    found = (
        set(
            connection.scalars(
                text("SELECT code FROM iz.permissions WHERE code = ANY(:codes)"),
                {"codes": selected},
            )
        )
        if selected
        else set()
    )
    if found != set(selected):
        raise HTTPException(422, "One or more permissions are not registered.")
    return selected


def ensure_usable_administrator(connection) -> None:
    count = connection.scalar(
        text("""
        SELECT count(*) FROM (
            SELECT account.id
            FROM iz.accounts account
            JOIN iz.account_roles assignment ON assignment.account_id=account.id
            JOIN iz.role_permissions permission ON permission.role_id=assignment.role_id
            WHERE account.account_kind='human' AND NOT account.disabled
              AND permission.permission IN ('role.manage', 'user.manage', 'system.config.edit')
            GROUP BY account.id
            HAVING count(DISTINCT permission.permission) = 3
        ) usable
    """)
    )
    if count == 0:
        raise HTTPException(
            409, "Keep at least one active account with administration permissions."
        )


def principal_view(connection, principal_id: UUID) -> PrincipalView:
    row = (
        connection.execute(
            text("""
        SELECT principal.id, principal.principal_kind, principal.display_name, entity.version,
               entity.archived_at IS NOT NULL AS archived,
               count(account.id) AS linked_account_count
        FROM iz.principals principal
        JOIN iz.entities entity ON entity.id=principal.id
        LEFT JOIN iz.accounts account ON account.principal_id=principal.id
        WHERE principal.id=:id
        GROUP BY principal.id, entity.version, entity.archived_at
    """),
            {"id": principal_id},
        )
        .mappings()
        .first()
    )
    if row is None:
        raise HTTPException(404, "Principal not found.")
    return PrincipalView(**row)


@router.get("/site/settings", response_model=SiteSettingsView)
def site_settings(request: Request):
    authenticate(request, "system.config.view")
    with request.app.state.engine.connect() as connection:
        row = connection.execute(text("SELECT * FROM iz.sites WHERE local")).mappings().one()
    return site_settings_view(row)


@router.put("/site/settings", response_model=SiteSettingsView)
def update_site_settings(payload: UpdateSiteSettings, request: Request):
    identity = authenticate(request, "system.config.edit")
    with request.app.state.engine.begin() as connection:
        row = (
            connection.execute(text("SELECT * FROM iz.sites WHERE local FOR UPDATE"))
            .mappings()
            .one()
        )
        if row["settings_version"] != payload.expected_version:
            raise HTTPException(409, "Site settings changed. Reload before saving.")
        changed = (
            connection.execute(
                text("""
            UPDATE iz.sites SET display_name=:name, settings_version=settings_version + 1
            WHERE id=:id RETURNING *
        """),
                {"name": payload.display_name, "id": row["id"]},
            )
            .mappings()
            .one()
        )
        connection.execute(
            text("""
            INSERT INTO iz.domain_events(id, source_site_id, source_incarnation, event_type,
                actor_id, command_id, authority_epoch, command_epoch, payload)
            VALUES (:event, :site, :incarnation, 'site.settings.updated', :actor,
                :command, 1, 1, CAST(:payload AS jsonb))
        """),
            {
                "event": uuid4(),
                "command": uuid4(),
                "site": row["id"],
                "incarnation": row["incarnation"],
                "actor": identity.account_id,
                "payload": '{"field":"display_name"}',
            },
        )
    return site_settings_view(changed)


@router.get("/site/logo")
def site_logo(request: Request):
    authenticate(request, "inventory.read")
    with request.app.state.engine.connect() as connection:
        logo = connection.scalar(text("SELECT logo_png FROM iz.sites WHERE local"))
    if logo is None:
        raise HTTPException(404, "No site logo is configured.")
    return Response(logo, media_type="image/png", headers={"Cache-Control": "private, max-age=300"})


@router.put("/site/logo", response_model=SiteSettingsView)
def update_site_logo(payload: UpdateSiteLogo, request: Request):
    identity = authenticate(request, "system.config.edit")
    logo = normalized_site_logo(payload.logo_base64)
    with request.app.state.engine.begin() as connection:
        row = (
            connection.execute(text("SELECT * FROM iz.sites WHERE local FOR UPDATE"))
            .mappings()
            .one()
        )
        if row["settings_version"] != payload.expected_version:
            raise HTTPException(409, "Site settings changed. Reload before saving.")
        changed = (
            connection.execute(
                text("""
            UPDATE iz.sites SET logo_png=:logo, logo_updated_at=now(),
                settings_version=settings_version + 1 WHERE id=:id RETURNING *
        """),
                {"logo": logo, "id": row["id"]},
            )
            .mappings()
            .one()
        )
        connection.execute(
            text("""
            INSERT INTO iz.domain_events(id, source_site_id, source_incarnation, event_type,
                actor_id, command_id, authority_epoch, command_epoch, payload)
            VALUES (:event, :site, :incarnation, 'site.logo.updated', :actor,
                :command, 1, 1, '{"normalized":"png"}'::jsonb)
        """),
            {
                "event": uuid4(),
                "command": uuid4(),
                "site": row["id"],
                "incarnation": row["incarnation"],
                "actor": identity.account_id,
            },
        )
    return site_settings_view(changed)


@router.delete("/site/logo", response_model=SiteSettingsView)
def remove_site_logo(expected_version: int, request: Request):
    identity = authenticate(request, "system.config.edit")
    with request.app.state.engine.begin() as connection:
        row = (
            connection.execute(text("SELECT * FROM iz.sites WHERE local FOR UPDATE"))
            .mappings()
            .one()
        )
        if row["settings_version"] != expected_version:
            raise HTTPException(409, "Site settings changed. Reload before saving.")
        changed = (
            connection.execute(
                text("""
            UPDATE iz.sites SET logo_png=NULL, logo_updated_at=now(),
                settings_version=settings_version + 1 WHERE id=:id RETURNING *
        """),
                {"id": row["id"]},
            )
            .mappings()
            .one()
        )
        connection.execute(
            text("""
            INSERT INTO iz.domain_events(id, source_site_id, source_incarnation, event_type,
                actor_id, command_id, authority_epoch, command_epoch, payload)
            VALUES (:event, :site, :incarnation, 'site.logo.removed', :actor,
                :command, 1, 1, '{}'::jsonb)
        """),
            {
                "event": uuid4(),
                "command": uuid4(),
                "site": row["id"],
                "incarnation": row["incarnation"],
                "actor": identity.account_id,
            },
        )
    return site_settings_view(changed)


OBJECT_SELECT = """
    SELECT object.id, object.name, object.description, object.object_type_id,
           type.name AS type_name, placement.parent_id, parent.name AS parent_name,
           placement.relation,
           CASE WHEN identifier.value IS NOT NULL THEN 'I' || identifier.value END AS alias,
           entity.authority_epoch, entity.version, entity.updated_at,
           CASE WHEN holding.object_id IS NULL THEN NULL ELSE jsonb_build_object(
               'quantity', holding.quantity::text,
               'quantity_dimension', policy.quantity_dimension,
               'canonical_unit', policy.canonical_unit,
               'granularity', policy.granularity::text,
               'allow_negative', policy.allow_negative,
               'policy_type_id', policy.type_id,
               'policy_type_name', policy_type.name
           ) END AS stock,
           (SELECT count(*) FROM iz.placements child
               JOIN iz.entities child_entity ON child_entity.id = child.object_id
               WHERE child.parent_id = object.id AND child_entity.archived_at IS NULL
           ) AS child_count,
           (WITH RECURSIVE lineage(id, depth) AS (
               SELECT placement.parent_id, 1 WHERE placement.parent_id IS NOT NULL
               UNION ALL
               SELECT ancestor.parent_id, lineage.depth + 1 FROM iz.placements ancestor
               JOIN lineage ON ancestor.object_id = lineage.id
               WHERE ancestor.parent_id IS NOT NULL
           ) SELECT coalesce(jsonb_agg(jsonb_build_object('id', ancestor.id, 'name', ancestor.name)
                      ORDER BY lineage.depth DESC), '[]'::jsonb)
               FROM lineage JOIN iz.objects ancestor ON ancestor.id = lineage.id
           ) AS location_path
    FROM iz.objects object
    JOIN iz.entities entity ON entity.id = object.id
    JOIN iz.placements placement ON placement.object_id = object.id
    LEFT JOIN iz.object_types type ON type.id = object.object_type_id
    LEFT JOIN iz.objects parent ON parent.id = placement.parent_id
    LEFT JOIN iz.identifiers identifier ON identifier.entity_id = object.id
        AND identifier.issuer_site_id = (SELECT id FROM iz.sites WHERE local)
    LEFT JOIN iz.stock_holdings holding ON holding.object_id = object.id
    LEFT JOIN iz.stock_policies policy ON policy.type_id = holding.policy_type_id
    LEFT JOIN iz.object_types policy_type ON policy_type.id = policy.type_id
"""


@router.post("/auth/login", response_model=SessionView)
def sign_in(credentials: Login, request: Request, response: Response):
    settings = request.app.state.settings
    token, session = login(request.app.state.engine, settings, credentials)
    response.set_cookie(
        "iz_session",
        token,
        httponly=True,
        secure=settings.secure_cookies,
        samesite="strict",
        max_age=settings.session_hours * 3600,
        path="/",
    )
    response.headers["Cache-Control"] = "no-store"
    return session


@router.get("/auth/session", response_model=SessionView)
def current_session(request: Request, response: Response):
    response.headers["Cache-Control"] = "no-store"
    return authenticate(request)


@router.post("/auth/logout", status_code=204)
def sign_out(request: Request, response: Response):
    authenticate(request)
    token = request.cookies.get("iz_session", "")
    with request.app.state.engine.begin() as connection:
        connection.execute(
            text("DELETE FROM iz.sessions WHERE token_hash = :hash"),
            {"hash": hashlib.sha256(token.encode()).digest()},
        )
    response.delete_cookie("iz_session", path="/")


@router.get("/admin/permissions", response_model=list[str])
def list_permissions(request: Request):
    authenticate(request, "role.manage")
    with request.app.state.engine.connect() as connection:
        return list(connection.scalars(text("SELECT code FROM iz.permissions ORDER BY code")))


@router.get("/admin/roles", response_model=list[RoleView])
def list_roles(request: Request):
    try:
        authenticate(request, "role.manage")
    except HTTPException as error:
        if error.status_code != 403:
            raise
        authenticate(request, "role.assign")
    with request.app.state.engine.connect() as connection:
        role_ids = list(
            connection.scalars(text("SELECT id FROM iz.roles ORDER BY lower(name), id"))
        )
        return [role_view(connection, role_id) for role_id in role_ids]


@router.post("/admin/roles", response_model=RoleView)
def create_role(payload: CreateRole, request: Request):
    identity = authenticate(request, "role.manage")
    with request.app.state.engine.begin() as connection:
        permissions = validate_permissions(connection, payload.permissions)
        role_id = uuid4()
        try:
            connection.execute(
                text("""
                INSERT INTO iz.roles(id, name, description)
                VALUES (:id, :name, :description)
            """),
                {"id": role_id, "name": payload.name, "description": payload.description},
            )
        except Exception as error:
            if "unique" in str(error).lower():
                raise HTTPException(409, "A role with this name already exists.") from error
            raise
        if permissions:
            connection.execute(
                text("""
                INSERT INTO iz.role_permissions(role_id, permission)
                VALUES (:role, :permission)
            """),
                [{"role": role_id, "permission": permission} for permission in permissions],
            )
        record_administration_event(connection, identity.account_id, "role.created", "{}")
        return role_view(connection, role_id)


@router.put("/admin/roles/{role_id}", response_model=RoleView)
def update_role(role_id: UUID, payload: UpdateRole, request: Request):
    identity = authenticate(request, "role.manage")
    with request.app.state.engine.begin() as connection:
        current = role_view(connection, role_id)
        if current.version != payload.expected_version:
            raise HTTPException(409, "Role changed. Reload before saving.")
        permissions = validate_permissions(connection, payload.permissions)
        try:
            connection.execute(
                text("""
                UPDATE iz.roles SET name=:name, description=:description, version=version + 1
                WHERE id=:id
            """),
                {"id": role_id, "name": payload.name, "description": payload.description},
            )
        except Exception as error:
            if "unique" in str(error).lower():
                raise HTTPException(409, "A role with this name already exists.") from error
            raise
        connection.execute(
            text("DELETE FROM iz.role_permissions WHERE role_id=:id"), {"id": role_id}
        )
        if permissions:
            connection.execute(
                text("""
                INSERT INTO iz.role_permissions(role_id, permission)
                VALUES (:role, :permission)
            """),
                [{"role": role_id, "permission": permission} for permission in permissions],
            )
        connection.execute(
            text("""
            DELETE FROM iz.sessions WHERE account_id IN (
                SELECT account_id FROM iz.account_roles WHERE role_id=:role
            )
        """),
            {"role": role_id},
        )
        ensure_usable_administrator(connection)
        record_administration_event(connection, identity.account_id, "role.updated", "{}")
        return role_view(connection, role_id)


@router.get("/admin/accounts", response_model=list[AccountView])
def list_accounts(request: Request):
    authenticate(request, "user.manage")
    with request.app.state.engine.connect() as connection:
        account_ids = list(
            connection.scalars(
                text("""
            SELECT id FROM iz.accounts WHERE account_kind='human' ORDER BY lower(login), id
        """)
            )
        )
        return [account_view(connection, account_id) for account_id in account_ids]


@router.post("/admin/accounts", response_model=AccountView)
def create_account(payload: CreateAccount, request: Request):
    identity = authenticate(request, "user.manage")
    authenticate(request, "role.assign")
    login = payload.login.casefold()
    with request.app.state.engine.begin() as connection:
        validate_role_ids(connection, payload.role_ids)
        if payload.principal_id is not None:
            valid_principal = connection.scalar(
                text("""
                SELECT EXISTS(
                    SELECT 1 FROM iz.principals principal
                    JOIN iz.entities entity ON entity.id=principal.id
                    WHERE principal.id=:id AND principal.principal_kind='person'
                      AND entity.archived_at IS NULL)
            """),
                {"id": payload.principal_id},
            )
            if not valid_principal:
                raise HTTPException(422, "The selected principal is not available.")
        account_id = uuid4()
        site_id = connection.scalar(text("SELECT id FROM iz.sites WHERE local"))
        try:
            connection.execute(
                text("""
                INSERT INTO iz.entities(id, kind, home_site_id, write_site_id)
                VALUES (:id, 'account', :site, :site)
            """),
                {"id": account_id, "site": site_id},
            )
            connection.execute(
                text("""
                INSERT INTO iz.accounts(id, principal_id, login, password_hash, account_kind)
                VALUES (:id, :principal, :login, :hash, 'human')
            """),
                {
                    "id": account_id,
                    "principal": payload.principal_id,
                    "login": login,
                    "hash": password_hasher.hash(payload.password),
                },
            )
        except Exception as error:
            if "unique" in str(error).lower():
                raise HTTPException(409, "That login is already in use.") from error
            raise
        if payload.role_ids:
            connection.execute(
                text("""
                INSERT INTO iz.account_roles(account_id, role_id) VALUES (:account, :role)
            """),
                [{"account": account_id, "role": role_id} for role_id in payload.role_ids],
            )
        record_administration_event(connection, identity.account_id, "account.created", "{}")
        return account_view(connection, account_id)


@router.put("/admin/accounts/{account_id}", response_model=AccountView)
def update_account(account_id: UUID, payload: UpdateAccount, request: Request):
    identity = authenticate(request, "user.manage")
    authenticate(request, "role.assign")
    with request.app.state.engine.begin() as connection:
        current = account_view(connection, account_id)
        if current.version != payload.expected_version:
            raise HTTPException(409, "Account changed. Reload before saving.")
        validate_role_ids(connection, payload.role_ids)
        if payload.principal_id is not None:
            principal_exists = connection.scalar(
                text("""
                    SELECT EXISTS(
                        SELECT 1 FROM iz.principals principal
                        JOIN iz.entities entity ON entity.id=principal.id
                        WHERE principal.id=:id AND principal.principal_kind='person'
                          AND entity.archived_at IS NULL)
                """),
                {"id": payload.principal_id},
            )
            if not principal_exists:
                raise HTTPException(422, "The selected principal is not available.")
        connection.execute(
            text("""
            UPDATE iz.accounts SET principal_id=:principal, disabled=:disabled WHERE id=:id
        """),
            {
                "id": account_id,
                "principal": payload.principal_id,
                "disabled": payload.disabled,
            },
        )
        connection.execute(
            text("DELETE FROM iz.account_roles WHERE account_id=:id"), {"id": account_id}
        )
        if payload.role_ids:
            connection.execute(
                text("""
                INSERT INTO iz.account_roles(account_id, role_id) VALUES (:account, :role)
            """),
                [{"account": account_id, "role": role_id} for role_id in payload.role_ids],
            )
        connection.execute(
            text("""
            UPDATE iz.entities SET version=version + 1, updated_at=now() WHERE id=:id
        """),
            {"id": account_id},
        )
        connection.execute(text("DELETE FROM iz.sessions WHERE account_id=:id"), {"id": account_id})
        ensure_usable_administrator(connection)
        record_administration_event(connection, identity.account_id, "account.updated", "{}")
        return account_view(connection, account_id)


@router.post("/admin/accounts/{account_id}/password", response_model=AccountView)
def reset_account_password(account_id: UUID, payload: ResetAccountPassword, request: Request):
    identity = authenticate(request, "user.manage")
    with request.app.state.engine.begin() as connection:
        current = account_view(connection, account_id)
        if current.version != payload.expected_version:
            raise HTTPException(409, "Account changed. Reload before saving.")
        connection.execute(
            text("UPDATE iz.accounts SET password_hash=:hash WHERE id=:id"),
            {
                "id": account_id,
                "hash": password_hasher.hash(payload.password),
            },
        )
        connection.execute(text("DELETE FROM iz.sessions WHERE account_id=:id"), {"id": account_id})
        connection.execute(
            text("""
            UPDATE iz.entities SET version=version + 1, updated_at=now() WHERE id=:id
        """),
            {"id": account_id},
        )
        record_administration_event(connection, identity.account_id, "account.password_reset", "{}")
        return account_view(connection, account_id)


@router.get("/principals", response_model=list[PrincipalView])
def list_principals(request: Request, include_archived: bool = False):
    authenticate(request, "principal.manage")
    with request.app.state.engine.connect() as connection:
        ids = list(
            connection.scalars(
                text("""
            SELECT principal.id FROM iz.principals principal
            JOIN iz.entities entity ON entity.id=principal.id
            WHERE :include_archived OR entity.archived_at IS NULL
            ORDER BY lower(principal.display_name), principal.id
        """),
                {"include_archived": include_archived},
            )
        )
        return [principal_view(connection, principal_id) for principal_id in ids]


@router.post("/principals", response_model=PrincipalView)
def create_principal(payload: CreatePrincipal, request: Request):
    identity = authenticate(request, "principal.manage")
    with request.app.state.engine.begin() as connection:
        principal_id = uuid4()
        site_id = connection.scalar(text("SELECT id FROM iz.sites WHERE local"))
        connection.execute(
            text("""
            INSERT INTO iz.entities(id, kind, home_site_id, write_site_id)
            VALUES (:id, 'principal', :site, :site)
        """),
            {"id": principal_id, "site": site_id},
        )
        connection.execute(
            text("""
            INSERT INTO iz.principals(id, principal_kind, display_name)
            VALUES (:id, :kind, :name)
        """),
            {"id": principal_id, "kind": payload.principal_kind, "name": payload.display_name},
        )
        record_administration_event(connection, identity.account_id, "principal.created", "{}")
        return principal_view(connection, principal_id)


@router.put("/principals/{principal_id}", response_model=PrincipalView)
def update_principal(principal_id: UUID, payload: UpdatePrincipal, request: Request):
    identity = authenticate(request, "principal.manage")
    with request.app.state.engine.begin() as connection:
        current = principal_view(connection, principal_id)
        if current.archived:
            raise HTTPException(409, "Archived principals cannot be edited.")
        if current.version != payload.expected_version:
            raise HTTPException(409, "Principal changed. Reload before saving.")
        if payload.principal_kind != "person" and current.linked_account_count:
            raise HTTPException(422, "An account can only be linked to a person principal.")
        connection.execute(
            text("""
            UPDATE iz.principals SET principal_kind=:kind, display_name=:name WHERE id=:id
        """),
            {"id": principal_id, "kind": payload.principal_kind, "name": payload.display_name},
        )
        connection.execute(
            text("""
            UPDATE iz.entities SET version=version + 1, updated_at=now() WHERE id=:id
        """),
            {"id": principal_id},
        )
        record_administration_event(connection, identity.account_id, "principal.updated", "{}")
        return principal_view(connection, principal_id)


@router.post("/principals/{principal_id}/archive", response_model=PrincipalView)
def archive_principal(principal_id: UUID, expected_version: int, request: Request):
    identity = authenticate(request, "principal.manage")
    with request.app.state.engine.begin() as connection:
        current = principal_view(connection, principal_id)
        if current.archived:
            return current
        if current.version != expected_version:
            raise HTTPException(409, "Principal changed. Reload before archiving.")
        if current.linked_account_count:
            raise HTTPException(409, "Unlink associated accounts before archiving this principal.")
        connection.execute(
            text("""
            UPDATE iz.entities SET archived_at=now(), version=version + 1, updated_at=now()
            WHERE id=:id
        """),
            {"id": principal_id},
        )
        record_administration_event(connection, identity.account_id, "principal.archived", "{}")
        return principal_view(connection, principal_id)


@router.post("/commands", response_model=CommandResult)
def commands(command: Command, request: Request):
    session = authenticate(request, PERMISSIONS[command.payload.kind])
    return execute_command(request.app.state.engine, command, session.account_id)


@router.get("/objects", response_model=ObjectPage)
def list_objects(
    request: Request,
    query: str = Query(default="", max_length=160),
    parent_id: UUID | None = None,
    subtree_id: UUID | None = None,
    tag_id: UUID | None = None,
    type_id: UUID | None = None,
    exact_type: bool = False,
    roots: bool = False,
    limit: int = Query(default=50, ge=1, le=200),
    offset: int = Query(default=0, ge=0),
):
    authenticate(request)
    escaped_query = query.replace("%", "\\%").replace("_", "\\_")
    predicate = """ WHERE entity.archived_at IS NULL
                     AND (:query = '' OR object.name ILIKE :pattern OR type.name ILIKE :pattern
                             OR identifier.value = :query
               OR CAST(object.id AS text) = :query
             OR 'I' || identifier.value = :query)
        AND (CAST(:parent AS uuid) IS NULL OR placement.parent_id = :parent)
                AND (CAST(:type AS uuid) IS NULL OR (:exact_type AND object.object_type_id = :type)
                  OR (NOT :exact_type AND object.object_type_id IN (
                    WITH RECURSIVE type_descendants(id) AS (
                        SELECT CAST(:type AS uuid)
                        UNION ALL
                        SELECT child.id FROM iz.object_types child
                        JOIN type_descendants ON child.parent_type_id = type_descendants.id
                    ) SELECT id FROM type_descendants
                )))
        AND (NOT :roots OR placement.parent_id IS NULL)
        AND (CAST(:subtree AS uuid) IS NULL OR object.id IN (
            WITH RECURSIVE descendants(id) AS (
                SELECT id FROM iz.objects WHERE id = :subtree
                UNION ALL
                SELECT child.object_id FROM iz.placements child
                JOIN descendants ON child.parent_id = descendants.id
            ) SELECT id FROM descendants
        ))
        AND (CAST(:tag AS uuid) IS NULL OR EXISTS (
            WITH RECURSIVE tag_descendants(id) AS (
                SELECT CAST(:tag AS uuid)
                UNION
                SELECT edge.child_id FROM iz.tag_edges edge
                JOIN tag_descendants ON edge.parent_id = tag_descendants.id
            )
            SELECT 1 FROM iz.entity_tags assignment
            JOIN tag_descendants ON tag_descendants.id = assignment.tag_id
            WHERE assignment.entity_id = object.id
               OR assignment.entity_id IN (
                    WITH RECURSIVE type_ancestors(id) AS (
                        SELECT object.object_type_id
                        UNION ALL
                        SELECT type.parent_type_id FROM iz.object_types type
                        JOIN type_ancestors ON type.id = type_ancestors.id
                        WHERE type.parent_type_id IS NOT NULL
                    ) SELECT id FROM type_ancestors
               )
        ))
    """
    parameters = {
        "query": query,
        "pattern": f"%{escaped_query}%",
        "parent": parent_id,
        "subtree": subtree_id,
        "tag": tag_id,
        "type": type_id,
        "exact_type": exact_type,
        "roots": roots,
        "limit": limit,
        "offset": offset,
    }
    with request.app.state.engine.connect() as connection:
        total = connection.scalar(
            text("SELECT count(*) FROM (" + OBJECT_SELECT + predicate + ") matches"), parameters
        )
        rows = (
            connection.execute(
                text(
                    OBJECT_SELECT
                    + predicate
                    + " ORDER BY lower(object.name), object.id LIMIT :limit OFFSET :offset"
                ),
                parameters,
            )
            .mappings()
            .all()
        )
    return ObjectPage(items=[ObjectView(**row) for row in rows], total=total)


@router.get("/objects/{object_id}", response_model=ObjectView)
def get_object(object_id: UUID, request: Request):
    authenticate(request)
    with request.app.state.engine.connect() as connection:
        row = (
            connection.execute(
                text(OBJECT_SELECT + " WHERE object.id = :id AND entity.archived_at IS NULL"),
                {"id": object_id},
            )
            .mappings()
            .first()
        )
    if row is None:
        raise HTTPException(404, "Object not found.")
    return ObjectView(**row)


@router.get("/objects/{object_id}/ancestors", response_model=list[ObjectView])
def ancestors(object_id: UUID, request: Request):
    authenticate(request)
    with request.app.state.engine.connect() as connection:
        rows = (
            connection.execute(
                text(
                    """
            WITH RECURSIVE path(id, depth) AS (
                SELECT parent_id, 1 FROM iz.placements WHERE object_id = :id
                UNION ALL
                SELECT placement.parent_id, path.depth + 1 FROM iz.placements placement
                JOIN path ON placement.object_id = path.id WHERE placement.parent_id IS NOT NULL
            )
        """
                    + OBJECT_SELECT
                    + " JOIN path ON path.id = object.id ORDER BY path.depth DESC"
                ),
                {"id": object_id},
            )
            .mappings()
            .all()
        )
    return [ObjectView(**row) for row in rows]


@router.get("/objects/{object_id}/history", response_model=list[HistoryEntry])
def history(
    object_id: UUID,
    request: Request,
    limit: int = Query(default=50, ge=1, le=200),
    offset: int = Query(default=0, ge=0),
):
    authenticate(request)
    with request.app.state.engine.connect() as connection:
        rows = (
            connection.execute(
                text("""
            SELECT event.id, event.event_type, event.occurred_at, account.login AS actor,
                   subject.version, event.payload
            FROM iz.event_subjects subject
            JOIN iz.domain_events event ON event.id = subject.event_id
            LEFT JOIN iz.accounts account ON account.id = event.actor_id
            WHERE subject.entity_id = :id ORDER BY subject.version DESC LIMIT :limit OFFSET :offset
        """),
                {"id": object_id, "limit": limit, "offset": offset},
            )
            .mappings()
            .all()
        )
    return [HistoryEntry(**row) for row in rows]


@router.get("/types", response_model=list[TypeView])
def list_types(request: Request):
    authenticate(request)
    with request.app.state.engine.connect() as connection:
        rows = (
            connection.execute(
                text("""
            SELECT type.id, type.name, type.description, type.parent_type_id,
                   parent.name AS parent_name, type.abstract, entity.version,
                   CASE WHEN stock_policy.type_id IS NULL THEN NULL ELSE jsonb_build_object(
                       'type_id', type.id,
                       'policy_type_id', stock_policy.type_id,
                       'policy_type_name', policy_type.name,
                       'quantity_dimension', stock_policy.quantity_dimension,
                       'canonical_unit', stock_policy.canonical_unit,
                       'granularity', stock_policy.granularity::text,
                       'allow_negative', stock_policy.allow_negative
                   ) END AS stock_policy
            FROM iz.object_types type JOIN iz.entities entity ON entity.id = type.id
            LEFT JOIN iz.object_types parent ON parent.id = type.parent_type_id
            LEFT JOIN LATERAL (
                WITH RECURSIVE lineage(id, depth) AS (
                    SELECT type.id, 0
                    UNION ALL
                    SELECT ancestor.parent_type_id, lineage.depth + 1
                    FROM iz.object_types ancestor JOIN lineage ON ancestor.id = lineage.id
                    WHERE ancestor.parent_type_id IS NOT NULL
                )
                SELECT policy.* FROM lineage
                JOIN iz.stock_policies policy ON policy.type_id=lineage.id
                ORDER BY lineage.depth LIMIT 1
            ) stock_policy ON true
            LEFT JOIN iz.object_types policy_type ON policy_type.id=stock_policy.type_id
            WHERE entity.archived_at IS NULL ORDER BY lower(type.name), type.id
        """)
            )
            .mappings()
            .all()
        )
    return [TypeView(**row) for row in rows]


@router.get("/types/{type_id}/stock-policy", response_model=StockPolicyView)
def type_stock_policy(type_id: UUID, request: Request):
    authenticate(request)
    with request.app.state.engine.connect() as connection:
        row = (
            connection.execute(
                text("""
            WITH RECURSIVE lineage(id, depth) AS (
                SELECT CAST(:type AS uuid), 0
                UNION ALL
                SELECT type.parent_type_id, lineage.depth + 1
                FROM iz.object_types type JOIN lineage ON type.id=lineage.id
                WHERE type.parent_type_id IS NOT NULL
            )
            SELECT CAST(:type AS uuid) AS type_id, policy.type_id AS policy_type_id,
                   policy_type.name AS policy_type_name, policy.quantity_dimension,
                   policy.canonical_unit, policy.granularity::text AS granularity,
                   policy.allow_negative
            FROM lineage JOIN iz.stock_policies policy ON policy.type_id=lineage.id
            JOIN iz.object_types policy_type ON policy_type.id=policy.type_id
            JOIN iz.entities entity ON entity.id=policy_type.id
            WHERE entity.archived_at IS NULL
            ORDER BY lineage.depth LIMIT 1
        """),
                {"type": type_id},
            )
            .mappings()
            .first()
        )
    if row is None:
        raise HTTPException(404, "This type does not define or inherit a stock policy.")
    return StockPolicyView(**row)


@router.get("/tags", response_model=list[TagView])
def list_tags(request: Request):
    authenticate(request)
    with request.app.state.engine.connect() as connection:
        rows = (
            connection.execute(
                text("""
                SELECT tag.id, tag.name, tag.description, entity.version,
                       coalesce((SELECT array_agg(edge.parent_id ORDER BY edge.parent_id)
                           FROM iz.tag_edges edge WHERE edge.child_id = tag.id),
                           ARRAY[]::uuid[]) AS parent_ids,
                       (SELECT count(*) FROM iz.entity_tags assignment
                           WHERE assignment.tag_id = tag.id
                             AND assignment.target_kind = 'object') AS direct_object_count,
                       (SELECT count(*) FROM iz.entity_tags assignment
                           WHERE assignment.tag_id = tag.id
                             AND assignment.target_kind = 'object_type') AS direct_type_count
                FROM iz.tags tag JOIN iz.entities entity ON entity.id = tag.id
                WHERE entity.archived_at IS NULL
                ORDER BY lower(tag.name), tag.id
            """)
            )
            .mappings()
            .all()
        )
    return [TagView(**row) for row in rows]


def effective_tags(connection, entity_id: UUID, entity_kind: str) -> EntityTagsView:
    available = connection.scalar(
        text("""
        SELECT EXISTS (
            SELECT 1 FROM iz.entities
            WHERE id = :id AND kind = :kind AND archived_at IS NULL
        )
    """),
        {"id": entity_id, "kind": entity_kind},
    )
    if not available:
        raise HTTPException(404, "Entity not found.")
    explicit_tag_ids = list(
        connection.scalars(
            text("""
            SELECT tag_id FROM iz.entity_tags
            WHERE entity_id = :id ORDER BY tag_id
        """),
            {"id": entity_id},
        )
    )
    type_seed = (
        """
        UNION ALL
        SELECT assignment.tag_id, assignment.target_kind
        FROM (
            WITH RECURSIVE ancestors(id) AS (
                SELECT object.object_type_id FROM iz.objects object WHERE object.id = :id
                UNION ALL
                SELECT type.parent_type_id FROM iz.object_types type
                JOIN ancestors ON type.id = ancestors.id
                WHERE type.parent_type_id IS NOT NULL
            ) SELECT id FROM ancestors
        ) inherited_type
        JOIN iz.entity_tags assignment ON assignment.entity_id = inherited_type.id
    """
        if entity_kind == "object"
        else """
        UNION ALL
        SELECT assignment.tag_id, assignment.target_kind
        FROM (
            WITH RECURSIVE ancestors(id) AS (
                SELECT parent_type_id FROM iz.object_types WHERE id = :id
                UNION ALL
                SELECT type.parent_type_id FROM iz.object_types type
                JOIN ancestors ON type.id = ancestors.id
                WHERE type.parent_type_id IS NOT NULL
            ) SELECT id FROM ancestors WHERE id IS NOT NULL
        ) inherited_type
        JOIN iz.entity_tags assignment ON assignment.entity_id = inherited_type.id
    """
    )
    rows = (
        connection.execute(
            text(
                """
            WITH RECURSIVE seeds(tag_id, source_kind) AS (
                SELECT assignment.tag_id, assignment.target_kind
                FROM iz.entity_tags assignment WHERE assignment.entity_id = :id
            """
                + type_seed
                + """
            ), closure(tag_id, seed_id, source_kind) AS (
                SELECT tag_id, tag_id, source_kind FROM seeds
                UNION
                SELECT edge.parent_id, closure.seed_id, closure.source_kind
                FROM iz.tag_edges edge JOIN closure ON edge.child_id = closure.tag_id
            )
            SELECT closure.tag_id AS id, tag.name, closure.source_kind,
                   seed.id AS seed_id, seed.name AS seed_name
            FROM closure
            JOIN iz.tags tag ON tag.id = closure.tag_id
            JOIN iz.tags seed ON seed.id = closure.seed_id
            ORDER BY lower(tag.name), tag.id, closure.source_kind, lower(seed.name), seed.id
        """
            ),
            {"id": entity_id},
        )
        .mappings()
        .all()
    )
    grouped: dict[UUID, EffectiveTagView] = {}
    for row in rows:
        if row["id"] not in grouped:
            grouped[row["id"]] = EffectiveTagView(id=row["id"], name=row["name"], sources=[])
        grouped[row["id"]].sources.append(
            TagSource(
                source_kind=row["source_kind"],
                tag_id=row["seed_id"],
                tag_name=row["seed_name"],
            )
        )
    return EntityTagsView(
        explicit_tag_ids=explicit_tag_ids,
        effective_tags=list(grouped.values()),
    )


@router.get("/objects/{object_id}/tags", response_model=EntityTagsView)
def object_tags(object_id: UUID, request: Request):
    authenticate(request)
    with request.app.state.engine.connect() as connection:
        return effective_tags(connection, object_id, "object")


@router.get("/types/{type_id}/tags", response_model=EntityTagsView)
def type_tags(type_id: UUID, request: Request):
    authenticate(request)
    with request.app.state.engine.connect() as connection:
        return effective_tags(connection, type_id, "object_type")


@router.get("/resolve", response_model=ObjectView)
def resolve(request: Request, value: str = Query(min_length=1, max_length=128)):
    authenticate(request)
    with request.app.state.engine.connect() as connection:
        identity = resolve_identifier(connection, value)
    return get_object(identity, request)
