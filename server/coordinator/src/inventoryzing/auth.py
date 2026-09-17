import hashlib
import secrets
from dataclasses import dataclass
from datetime import UTC, datetime, timedelta
from uuid import UUID, uuid4

from argon2 import PasswordHasher
from argon2.exceptions import InvalidHashError, VerificationError
from fastapi import HTTPException, Request
from sqlalchemy import Connection, Engine, text

from inventoryzing.config import Settings
from inventoryzing.contracts import Login, SessionView

password_hasher = PasswordHasher()
dummy_hash = password_hasher.hash(secrets.token_urlsafe(32))


@dataclass(frozen=True)
class ServiceIdentity:
    account_id: UUID
    login: str
    permissions: tuple[str, ...]


def session_view(connection: Connection, token: str) -> SessionView:
    session = (
        connection.execute(
            text("""
        SELECT account.id AS account_id, account.login, session.csrf_token,
               site.id AS site_id, site.display_name AS site_name, epoch.epoch AS command_epoch
        FROM iz.sessions session
        JOIN iz.accounts account ON account.id = session.account_id
            AND account.account_kind = 'human' AND NOT account.disabled
        CROSS JOIN iz.sites site
        JOIN iz.command_epochs epoch ON epoch.site_id = site.id AND epoch.state = 'OPEN'
        WHERE site.local AND session.token_hash = :hash AND session.expires_at > now()
    """),
            {"hash": hashlib.sha256(token.encode()).digest()},
        )
        .mappings()
        .first()
    )
    if session is None:
        raise HTTPException(401, "Sign in to continue.")
    permissions = list(
        connection.scalars(
            text("""
        SELECT DISTINCT permission.permission FROM iz.account_roles assignment
        JOIN iz.role_permissions permission ON permission.role_id = assignment.role_id
        WHERE assignment.account_id = :account ORDER BY permission.permission
    """),
            {"account": session["account_id"]},
        )
    )
    return SessionView(**session, permissions=permissions)


def account_has_permission(connection: Connection, account_id: UUID, permission: str) -> bool:
    """Check current access for work that began with an interactive session.

    Scanner workflows are intentionally memory-only, but their authority must not
    outlive a role change or a disabled account.
    """
    return bool(
        connection.scalar(
            text("""
                SELECT EXISTS(
                    SELECT 1
                    FROM iz.accounts account
                    JOIN iz.account_roles assignment ON assignment.account_id=account.id
                    JOIN iz.role_permissions granted ON granted.role_id=assignment.role_id
                    WHERE account.id=:account AND account.account_kind='human'
                      AND NOT account.disabled AND granted.permission=:permission
                )
            """),
            {"account": account_id, "permission": permission},
        )
    )


def authenticate(request: Request, permission: str = "inventory.read") -> SessionView:
    token = request.cookies.get("iz_session", "")
    if not token or len(token) > 128:
        raise HTTPException(401, "Sign in to continue.")
    with request.app.state.engine.connect() as connection:
        session = session_view(connection, token)
    if permission not in session.permissions:
        raise HTTPException(403, "Your account does not have this permission.")
    if request.method not in {"GET", "HEAD", "OPTIONS"}:
        supplied = request.headers.get("x-csrf-token", "")
        if not secrets.compare_digest(supplied, session.csrf_token):
            raise HTTPException(403, "CSRF validation failed. Reload and try again.")
    return session


def authenticate_service(request: Request, permission: str) -> ServiceIdentity:
    authorization = request.headers.get("authorization", "")
    scheme, separator, token = authorization.partition(" ")
    if separator != " " or scheme.casefold() != "bearer" or not token or len(token) > 256:
        raise HTTPException(401, "A device-agent bearer credential is required.")
    with request.app.state.engine.connect() as connection:
        account = connection.execute(
            text("""
                SELECT account.id, account.login
                FROM iz.service_credentials credential
                JOIN iz.accounts account ON account.id=credential.account_id
                    AND account.account_kind='service' AND NOT account.disabled
                WHERE credential.token_hash=:hash AND credential.revoked_at IS NULL
                  AND (credential.expires_at IS NULL OR credential.expires_at > now())
            """),
            {"hash": hashlib.sha256(token.encode()).digest()},
        ).mappings().first()
        if account is None:
            raise HTTPException(401, "Device-agent credential is invalid or expired.")
        permissions = tuple(connection.scalars(text("""
            SELECT DISTINCT permission.permission FROM iz.account_roles assignment
            JOIN iz.role_permissions permission ON permission.role_id=assignment.role_id
            WHERE assignment.account_id=:account ORDER BY permission.permission
        """), {"account": account["id"]}))
    if permission not in permissions:
        raise HTTPException(403, "Device agent does not have this permission.")
    return ServiceIdentity(account["id"], account["login"], permissions)


def login(engine: Engine, settings: Settings, credentials: Login) -> tuple[str, SessionView]:
    normalized = credentials.login.strip().casefold()
    view: SessionView | None = None
    token = secrets.token_urlsafe(32)
    with engine.begin() as connection:
        connection.execute(
            text("""
            INSERT INTO iz.login_attempts(login) VALUES (:login) ON CONFLICT DO NOTHING
        """),
            {"login": normalized},
        )
        attempts = (
            connection.execute(
                text("""
            SELECT * FROM iz.login_attempts WHERE login = :login FOR UPDATE
        """),
                {"login": normalized},
            )
            .mappings()
            .one()
        )
        expired = attempts["window_started_at"] < datetime.now(UTC) - timedelta(minutes=15)
        if attempts["failures"] >= 10 and not expired:
            raise HTTPException(429, "Too many sign-in attempts. Try again in 15 minutes.")
        account = (
            connection.execute(
            text("SELECT * FROM iz.accounts WHERE login = :login AND account_kind='human'"),
            {"login": normalized},
            )
            .mappings()
            .first()
        )
        try:
            verified = password_hasher.verify(
                account["password_hash"] if account else dummy_hash, credentials.password
            )
        except (VerificationError, InvalidHashError):
            verified = False
        if not verified or account is None or account["disabled"]:
            connection.execute(
                text("""
                UPDATE iz.login_attempts
                SET failures = CASE WHEN :expired THEN 1 ELSE failures + 1 END,
                    window_started_at = CASE WHEN :expired THEN now() ELSE window_started_at END
                WHERE login = :login
            """),
                {"expired": expired, "login": normalized},
            )
        else:
            connection.execute(
                text("DELETE FROM iz.login_attempts WHERE login = :login"), {"login": normalized}
            )
            connection.execute(text("DELETE FROM iz.sessions WHERE expires_at <= now()"))
            connection.execute(
                text("""
                INSERT INTO iz.sessions(token_hash, account_id, csrf_token, expires_at)
                VALUES (:hash, :account, :csrf, :expires)
            """),
                {
                    "hash": hashlib.sha256(token.encode()).digest(),
                    "account": account["id"],
                    "csrf": secrets.token_urlsafe(32),
                    "expires": datetime.now(UTC) + timedelta(hours=settings.session_hours),
                },
            )
            view = session_view(connection, token)
    if view is None:
        raise HTTPException(401, "Incorrect login or password.")
    return token, view


def bootstrap(engine: Engine, site_name: str, username: str, password: str) -> UUID:
    username = username.strip().casefold()
    if not username or not site_name.strip() or len(password) < 12:
        raise ValueError("A site name, login, and password of at least 12 characters are required.")
    encoded_password = password_hasher.hash(password)
    site, account, principal, role, event, command = [uuid4() for _index in range(6)]
    with engine.begin() as connection:
        connection.execute(text("SELECT pg_advisory_xact_lock(841900)"))
        if connection.scalar(text("SELECT count(*) FROM iz.sites")):
            raise ValueError("This database is already initialized.")
        connection.execute(
            text("INSERT INTO iz.sites(id, display_name, local) VALUES (:id, :name, true)"),
            {"id": site, "name": site_name.strip()},
        )
        for identity, kind in ((principal, "principal"), (account, "account")):
            connection.execute(
                text("""
                INSERT INTO iz.entities(id, kind, home_site_id, write_site_id)
                VALUES (:id, :kind, :site, :site)
            """),
                {"id": identity, "kind": kind, "site": site},
            )
        connection.execute(
            text("""
            INSERT INTO iz.principals(id, principal_kind, display_name)
            VALUES (:id, 'person', :name)
        """),
            {"id": principal, "name": username},
        )
        connection.execute(
            text("""
            INSERT INTO iz.accounts(id, principal_id, login, password_hash)
            VALUES (:id, :principal, :login, :hash)
        """),
            {"id": account, "principal": principal, "login": username, "hash": encoded_password},
        )
        connection.execute(
            text("INSERT INTO iz.roles(id, name) VALUES (:id, 'Administrator')"), {"id": role}
        )
        connection.execute(
            text("INSERT INTO iz.role_permissions SELECT :role, code FROM iz.permissions"),
            {"role": role},
        )
        connection.execute(
            text("INSERT INTO iz.account_roles VALUES (:account, :role)"),
            {"account": account, "role": role},
        )
        connection.execute(
            text("INSERT INTO iz.command_epochs(site_id, epoch) VALUES (:site, 1)"), {"site": site}
        )
        connection.execute(
            text("""
            INSERT INTO iz.command_receipts(authority_site, authority_epoch, command_epoch,
                command_id, actor_id, fingerprint, result)
            VALUES (:site, 1, 1, :command, :account, :fingerprint,
                jsonb_build_object('site_id', CAST(:site AS text)))
        """),
            {
                "site": site,
                "command": command,
                "account": account,
                "fingerprint": hashlib.sha256(f"bootstrap:{site}:{username}".encode()).digest(),
            },
        )
        connection.execute(
            text("""
            INSERT INTO iz.domain_events(id, source_site_id, source_incarnation, event_type,
                actor_id, command_id, authority_epoch, command_epoch, payload)
            VALUES (:id, :site, 1, 'site.bootstrapped', :account, :command, 1, 1,
                jsonb_build_object('site_name', CAST(:name AS text)))
        """),
            {"id": event, "site": site, "account": account, "command": command, "name": site_name},
        )
        for identity in (principal, account):
            connection.execute(
                text("INSERT INTO iz.event_subjects VALUES (:event, :entity, 1)"),
                {"event": event, "entity": identity},
            )
        connection.execute(
            text("INSERT INTO iz.replication_outbox(event_id) VALUES (:event)"), {"event": event}
        )
    return site


def create_printer_service(
    engine: Engine, name: str, *, token: str | None = None
) -> tuple[UUID, str]:
    return create_device_service(engine, "printer", name, "print.agent", token=token)


def create_scanner_service(
    engine: Engine, name: str, *, token: str | None = None
) -> tuple[UUID, str]:
    return create_device_service(engine, "scanner", name, "scanner.agent", token=token)


def create_device_service(
    engine: Engine,
    service_kind: str,
    name: str,
    permission: str,
    *,
    token: str | None = None,
) -> tuple[UUID, str]:
    normalized = name.strip().casefold()
    if not normalized or len(normalized) > 120:
        raise ValueError(f"A {service_kind} agent name of at most 120 characters is required.")
    account_id, credential_id = uuid4(), uuid4()
    token = token or secrets.token_urlsafe(48)
    if len(token) < 32:
        raise ValueError(
            f"{service_kind.title()} agent tokens must contain at least 32 characters."
        )
    login_name = f"service:{service_kind}:{normalized}"
    with engine.begin() as connection:
        site_id = connection.scalar(text("SELECT id FROM iz.sites WHERE local"))
        if site_id is None:
            raise ValueError(
                f"Bootstrap the local site before provisioning a {service_kind} agent."
            )
        if connection.scalar(
            text("SELECT EXISTS(SELECT 1 FROM iz.accounts WHERE login=:login)"),
            {"login": login_name},
        ):
            raise ValueError(f"A {service_kind} agent with this name already exists.")
        role_name = f"{service_kind.title()} Agent"
        role_id = connection.scalar(
            text("SELECT id FROM iz.roles WHERE name=:name"), {"name": role_name}
        )
        if role_id is None:
            role_id = uuid4()
            connection.execute(
                text("INSERT INTO iz.roles(id, name) VALUES (:id, :name)"),
                {"id": role_id, "name": role_name},
            )
            connection.execute(text("""
                INSERT INTO iz.role_permissions(role_id, permission)
                VALUES (:role, :permission)
            """), {"role": role_id, "permission": permission})
        connection.execute(text("""
            INSERT INTO iz.entities(id, kind, home_site_id, write_site_id)
            VALUES (:id, 'account', :site, :site)
        """), {"id": account_id, "site": site_id})
        connection.execute(text("""
            INSERT INTO iz.accounts(id, login, password_hash, account_kind)
            VALUES (:id, :login, :password, 'service')
        """), {"id": account_id, "login": login_name,
                "password": password_hasher.hash(secrets.token_urlsafe(32))})
        connection.execute(
            text("INSERT INTO iz.account_roles(account_id, role_id) VALUES (:account, :role)"),
            {"account": account_id, "role": role_id},
        )
        connection.execute(text("""
            INSERT INTO iz.service_credentials(id, account_id, token_hash, description)
            VALUES (:id, :account, :hash, :description)
        """), {"id": credential_id, "account": account_id,
                "hash": hashlib.sha256(token.encode()).digest(),
                "description": f"{service_kind.title()} agent {name.strip()}"})
    return account_id, token
