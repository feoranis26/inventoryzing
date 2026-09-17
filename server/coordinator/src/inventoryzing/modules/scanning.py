"""Terminal-bound live scanner workflows. Scan events themselves are never persisted."""

import hashlib
import secrets
from collections import deque
from dataclasses import dataclass, field
from datetime import UTC, datetime, timedelta
from threading import Lock
from typing import Literal
from uuid import UUID, uuid4

from fastapi import APIRouter, HTTPException, Request, Response
from pydantic import BaseModel, ConfigDict, Field
from sqlalchemy import text

from inventoryzing.auth import (
    ServiceIdentity,
    account_has_permission,
    authenticate,
    authenticate_service,
)
from inventoryzing.commands import CommandError, execute_command, resolve_identifier
from inventoryzing.contracts import Command, MoveObject, SessionView

router = APIRouter(prefix="/api")
SESSION_TTL = timedelta(seconds=15)
MAX_RESULTS = 100
MAX_SCAN_AGE = timedelta(seconds=5)
MAX_FUTURE_SKEW = timedelta(seconds=30)
TERMINAL_COOKIE = "iz_scanner_terminal"


class StrictModel(BaseModel):
    model_config = ConfigDict(extra="forbid")


class CreateScanSession(StrictModel):
    mode: Literal["lookup", "move"]
    destination_id: UUID | None = None
    controller_id: UUID
    take_over: bool = False


class ScanObservation(StrictModel):
    event_id: UUID
    terminal_id: UUID | None = None
    payload: str = Field(min_length=1, max_length=2048)
    source_id: str = Field(min_length=1, max_length=300)
    runtime_id: str = Field(min_length=1, max_length=80)
    source_type: str = Field(min_length=1, max_length=80)
    model: str | None = Field(default=None, max_length=160)
    serial_number: str | None = Field(default=None, max_length=160)
    received_at: datetime
    symbology: str | None = Field(default=None, max_length=80)


class ManualScanInput(StrictModel):
    payload: str = Field(min_length=1, max_length=2048)


class ScanResult(BaseModel):
    sequence: int
    event_id: UUID
    outcome: Literal["success", "failure", "no_action"]
    message: str
    object_id: UUID | None = None
    object_name: str | None = None
    occurred_at: datetime


class ScanSessionView(BaseModel):
    id: UUID
    terminal_id: UUID
    mode: Literal["lookup", "move"]
    destination_id: UUID | None
    destination_name: str | None
    expires_at: datetime
    results: list[ScanResult] = Field(default_factory=list)


class ScannerTerminalView(BaseModel):
    id: UUID
    display_name: str


class AgentScanResponse(BaseModel):
    outcome: Literal["success", "failure", "no_action"]
    message: str
    command_completed: bool = False


@dataclass
class LiveSession:
    id: UUID
    terminal_id: UUID
    controller_id: UUID
    account_id: UUID
    site_id: UUID
    command_epoch: int
    mode: Literal["lookup", "move"]
    destination_id: UUID | None
    destination_name: str | None
    expires_at: datetime
    sequence: int = 0
    results: deque[ScanResult] = field(default_factory=lambda: deque(maxlen=MAX_RESULTS))


class LiveScannerHub:
    """One renewable controller lease per terminal; no state survives process restart."""

    def __init__(self) -> None:
        self.lock = Lock()
        self.active: dict[UUID, LiveSession] = {}

    def _live(self, terminal_id: UUID, now: datetime) -> LiveSession | None:
        session = self.active.get(terminal_id)
        if session is not None and session.expires_at <= now:
            self.active.pop(terminal_id, None)
            return None
        return session

    def start(
        self,
        identity: SessionView,
        terminal_id: UUID,
        request: CreateScanSession,
        destination_name: str | None,
    ) -> LiveSession:
        now = datetime.now(UTC)
        with self.lock:
            existing = self._live(terminal_id, now)
            if (
                existing is not None
                and existing.controller_id != request.controller_id
                and not request.take_over
            ):
                raise HTTPException(409, "Another browser tab controls this scanner terminal.")
            session = LiveSession(
                uuid4(),
                terminal_id,
                request.controller_id,
                identity.account_id,
                identity.site_id,
                identity.command_epoch,
                request.mode,
                request.destination_id,
                destination_name,
                now + SESSION_TTL,
            )
            self.active[terminal_id] = session
            return session

    def get(
        self,
        session_id: UUID,
        terminal_id: UUID,
        controller_id: UUID,
        account_id: UUID,
        *,
        refresh: bool,
    ) -> LiveSession:
        now = datetime.now(UTC)
        with self.lock:
            active = self._live(terminal_id, now)
            if (
                active is None
                or active.id != session_id
                or active.controller_id != controller_id
                or active.account_id != account_id
            ):
                raise HTTPException(404, "The scanner workflow is no longer active.")
            if refresh:
                active.expires_at = now + SESSION_TTL
            return active

    def current(self, terminal_id: UUID) -> LiveSession | None:
        with self.lock:
            return self._live(terminal_id, datetime.now(UTC))

    def append(self, session_id: UUID, terminal_id: UUID, result: ScanResult) -> None:
        with self.lock:
            active = self._live(terminal_id, datetime.now(UTC))
            if active is not None and active.id == session_id:
                active.sequence += 1
                active.results.append(result.model_copy(update={"sequence": active.sequence}))

    def stop(
        self, session_id: UUID, terminal_id: UUID, controller_id: UUID, account_id: UUID
    ) -> None:
        with self.lock:
            active = self._live(terminal_id, datetime.now(UTC))
            if (
                active is not None
                and active.id == session_id
                and active.controller_id == controller_id
                and active.account_id == account_id
            ):
                self.active.pop(terminal_id, None)


def _token_hash(token: str) -> bytes:
    return hashlib.sha256(token.encode()).digest()


def terminal_for_browser(
    request: Request, response: Response, identity: SessionView
) -> ScannerTerminalView:
    token = request.cookies.get(TERMINAL_COOKIE, "")
    with request.app.state.engine.begin() as connection:
        terminal = None
        if 16 <= len(token) <= 256:
            terminal = (
                connection.execute(
                    text("""
                SELECT id, display_name FROM iz_scanner.terminals
                WHERE browser_token_hash=:token AND site_id=:site
            """),
                    {"token": _token_hash(token), "site": identity.site_id},
                )
                .mappings()
                .first()
            )
        if terminal is None:
            token = secrets.token_urlsafe(32)
            terminal = {"id": uuid4(), "display_name": "This browser terminal"}
            connection.execute(
                text("""
                INSERT INTO iz_scanner.terminals(id, site_id, browser_token_hash, display_name)
                VALUES (:id, :site, :token, :name)
            """),
                {
                    "id": terminal["id"],
                    "site": identity.site_id,
                    "token": _token_hash(token),
                    "name": terminal["display_name"],
                },
            )
            response.set_cookie(
                TERMINAL_COOKIE,
                token,
                httponly=True,
                secure=request.app.state.settings.secure_cookies,
                samesite="strict",
                max_age=365 * 24 * 60 * 60,
                path="/",
            )
        else:
            connection.execute(
                text("UPDATE iz_scanner.terminals SET last_seen_at=now() WHERE id=:id"),
                {"id": terminal["id"]},
            )
    return ScannerTerminalView(**terminal)


def _controller_id(request: Request) -> UUID:
    try:
        return UUID(request.headers.get("x-scanner-controller", ""))
    except ValueError as error:
        raise HTTPException(422, "A browser scanner controller identifier is required.") from error


def session_view(session: LiveSession, after: int = 0) -> ScanSessionView:
    return ScanSessionView(
        id=session.id,
        terminal_id=session.terminal_id,
        mode=session.mode,
        destination_id=session.destination_id,
        destination_name=session.destination_name,
        expires_at=session.expires_at,
        results=[result for result in session.results if result.sequence > after],
    )


@router.get("/scanner/terminal", response_model=ScannerTerminalView)
def current_terminal(request: Request, response: Response):
    return terminal_for_browser(request, response, authenticate(request))


@router.post("/scanner/sessions", response_model=ScanSessionView)
def create_session(payload: CreateScanSession, request: Request, response: Response):
    permission = "inventory.move" if payload.mode == "move" else "inventory.read"
    identity = authenticate(request, permission)
    terminal = terminal_for_browser(request, response, identity)
    if payload.mode == "move" and payload.destination_id is None:
        raise HTTPException(422, "Move workflows require a destination.")
    if payload.mode == "lookup" and payload.destination_id is not None:
        raise HTTPException(422, "Lookup workflows do not use a destination.")
    destination_name = None
    if payload.destination_id is not None:
        with request.app.state.engine.connect() as connection:
            destination_name = connection.scalar(
                text("""
                SELECT object.name
                FROM iz.objects object JOIN iz.entities entity ON entity.id=object.id
                WHERE object.id=:id AND entity.archived_at IS NULL
            """),
                {"id": payload.destination_id},
            )
        if destination_name is None:
            raise HTTPException(404, "The destination object is not available.")
    return session_view(
        request.app.state.scanner_hub.start(identity, terminal.id, payload, destination_name)
    )


@router.get("/scanner/sessions/{session_id}", response_model=ScanSessionView)
def read_session(session_id: UUID, request: Request, response: Response, after: int = 0):
    identity = authenticate(request)
    terminal = terminal_for_browser(request, response, identity)
    return session_view(
        request.app.state.scanner_hub.get(
            session_id, terminal.id, _controller_id(request), identity.account_id, refresh=True
        ),
        after,
    )


@router.delete("/scanner/sessions/{session_id}", status_code=204)
def delete_session(session_id: UUID, request: Request, response: Response):
    identity = authenticate(request)
    terminal = terminal_for_browser(request, response, identity)
    request.app.state.scanner_hub.stop(
        session_id, terminal.id, _controller_id(request), identity.account_id
    )
    response.status_code = 204


def object_snapshot(engine, payload: str):
    with engine.connect() as connection:
        object_id = resolve_identifier(connection, payload)
        return (
            connection.execute(
                text("""
            SELECT object.id, object.name, placement.parent_id, entity.version,
                   entity.authority_epoch
            FROM iz.objects object JOIN iz.entities entity ON entity.id=object.id
            JOIN iz.placements placement ON placement.object_id=object.id
            WHERE object.id=:id AND entity.archived_at IS NULL
        """),
                {"id": object_id},
            )
            .mappings()
            .one()
        )


def process_scan(request: Request, active: LiveSession, scan: ScanObservation) -> AgentScanResponse:
    outcome: Literal["success", "failure", "no_action"] = "success"
    object_id = None
    object_name = None
    try:
        required_permission = "inventory.move" if active.mode == "move" else "inventory.read"
        with request.app.state.engine.connect() as connection:
            authorized = account_has_permission(
                connection, active.account_id, required_permission
            )
        if not authorized:
            raise CommandError(
                "ACCESS_REVOKED", "The terminal operator is no longer authorized for this action."
            )
        item = object_snapshot(request.app.state.engine, scan.payload)
        object_id, object_name = item["id"], item["name"]
        if active.mode == "lookup":
            message = f"Found {object_name}."
        elif object_id == active.destination_id:
            outcome, message = "failure", "The destination cannot be moved into itself."
        elif item["parent_id"] == active.destination_id:
            message = f"{object_name} is already in {active.destination_name}."
        else:
            command = Command(
                authority_site=active.site_id,
                authority_epoch=item["authority_epoch"],
                command_epoch=active.command_epoch,
                command_id=uuid4(),
                payload=MoveObject(
                    kind="object.move",
                    object_id=object_id,
                    expected_version=item["version"],
                    parent_id=active.destination_id,
                    relation="contained_in",
                ),
            )
            execute_command(request.app.state.engine, command, active.account_id)
            message = f"Moved {object_name} into {active.destination_name}."
    except CommandError as error:
        outcome, message = (
            ("no_action", error.detail)
            if error.code in {"NOT_FOUND", "INVALID_IDENTIFIER"}
            else ("failure", error.detail)
        )
    except Exception:
        outcome, message = "failure", "The inventory operation failed."

    request.app.state.scanner_hub.append(
        active.id,
        active.terminal_id,
        ScanResult(
            sequence=0,
            event_id=scan.event_id,
            outcome=outcome,
            message=message,
            object_id=object_id,
            object_name=object_name,
            occurred_at=datetime.now(UTC),
        ),
    )
    return AgentScanResponse(
        outcome=outcome, message=message,
        command_completed=outcome == "success" and active.mode == "move",
    )


def bound_terminal(request: Request, service: ServiceIdentity, terminal_id: UUID) -> bool:
    """A scanner service can bind exactly one terminal on its first configured delivery."""
    with request.app.state.engine.begin() as connection:
        exists = connection.scalar(
            text("SELECT 1 FROM iz_scanner.terminals WHERE id=:id"), {"id": terminal_id}
        )
        if not exists:
            return False
        binding = connection.scalar(
            text("""
            SELECT terminal_id FROM iz_scanner.agent_terminal_bindings
            WHERE service_account_id=:account
        """),
            {"account": service.account_id},
        )
        if binding is None:
            connection.execute(
                text("""
                INSERT INTO iz_scanner.agent_terminal_bindings(service_account_id, terminal_id)
                VALUES (:account, :terminal)
            """),
                {"account": service.account_id, "terminal": terminal_id},
            )
            return True
        return binding == terminal_id


@router.post("/agent/scans", response_model=AgentScanResponse)
def receive_scan(scan: ScanObservation, request: Request):
    service = authenticate_service(request, "scanner.agent")
    if scan.terminal_id is None:
        return AgentScanResponse(
            outcome="no_action",
            message="This scanner needs a configured terminal identifier.",
        )
    if not bound_terminal(request, service, scan.terminal_id):
        return AgentScanResponse(
            outcome="no_action", message="This scanner is not paired with the requested terminal."
        )
    received = (
        scan.received_at.astimezone(UTC)
        if scan.received_at.tzinfo
        else scan.received_at.replace(tzinfo=UTC)
    )
    now = datetime.now(UTC)
    if received < now - MAX_SCAN_AGE or received > now + MAX_FUTURE_SKEW:
        return AgentScanResponse(
            outcome="no_action", message="The scan arrived too late for the active workflow."
        )
    active = request.app.state.scanner_hub.current(scan.terminal_id)
    if active is None:
        return AgentScanResponse(
            outcome="no_action", message="No scanner workflow is active for this terminal."
        )
    return process_scan(request, active, scan)


@router.post("/scanner/sessions/{session_id}/input", response_model=AgentScanResponse)
def manual_scan(session_id: UUID, payload: ManualScanInput, request: Request, response: Response):
    identity = authenticate(request)
    terminal = terminal_for_browser(request, response, identity)
    active = request.app.state.scanner_hub.get(
        session_id, terminal.id, _controller_id(request), identity.account_id, refresh=True
    )
    return process_scan(
        request,
        active,
        ScanObservation(
            event_id=uuid4(),
            terminal_id=terminal.id,
            payload=payload.payload,
            source_id="browser:manual",
            runtime_id="manual",
            source_type="Manual",
            received_at=datetime.now(UTC),
        ),
    )


def register(app) -> None:
    app.state.scanner_hub = LiveScannerHub()
    app.include_router(router)
