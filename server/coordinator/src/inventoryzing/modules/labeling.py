import hashlib
import html
import json
import re
import struct
import zlib
from base64 import b64encode
from dataclasses import dataclass
from datetime import UTC, datetime, timedelta
from io import BytesIO
from pathlib import Path
from threading import Lock
from typing import Any, Literal
from uuid import UUID, uuid4

import segno
from fastapi import APIRouter, HTTPException, Request, Response
from PIL import Image, ImageDraw, ImageFont, UnidentifiedImageError
from pydantic import BaseModel, ConfigDict, Field, model_validator
from sqlalchemy import text

from inventoryzing.auth import authenticate, authenticate_service
from inventoryzing.properties import (
    PropertyDefinition,
    PropertyRegistry,
    format_property,
    stored_definitions,
)

DEFAULT_PRINTER_SLOT = "brother-ql820nwb"
BRANDING_DIR = next(
    candidate
    for candidate in (
        Path.cwd() / "assets/branding",
        Path("/app/assets/branding"),
        Path(__file__).resolve().parents[5] / "assets/branding",
    )
    if candidate.exists()
)
INVENTORYZING_LOGO_SVG = (BRANDING_DIR / "inventoryzing.svg").read_bytes()
INVENTORYZING_LOGO_DATA_URI = (
    "data:image/svg+xml;base64," + b64encode(INVENTORYZING_LOGO_SVG).decode("ascii")
)

router = APIRouter(prefix="/api")
# Retained temporarily only as source for an explicit migration, never registered.
# No public route may create or mutate durable print records after the in-memory cutover.
legacy_router = APIRouter(prefix="/api")

PROFILE_ID = "brother-ql820nwb-29x90-mono-300"
WIDTH_MM = 29
HEIGHT_MM = 90
PIXEL_WIDTH = 343
PIXEL_HEIGHT = 1063
BROTHER_RASTER_LINE_BYTES = 90
BROTHER_RASTER_WIDTH = BROTHER_RASTER_LINE_BYTES * 8
# Raster rows cover only the printable die-cut region. The printer applies the
# documented physical feed offsets itself; including them moves the cutter.
BROTHER_RASTER_HEIGHT = 991
BROTHER_PRINTABLE_WIDTH = 306
BROTHER_PRINTABLE_HEIGHT = 991
BROTHER_LEFT_MARGIN = 408
BROTHER_TOP_MARGIN = 35
LEASE_SECONDS = 60
PRINT_RESULT_SECONDS = 10
AGENT_AVAILABILITY_SECONDS = 2
QUEUE_SECONDS = 5
CLAIM_SECONDS = 45
TERMINAL_STATES = {"Completed", "Rejected", "Failed", "Unknown"}
REPORT_TRANSITIONS = {
    "Claimed": {"Staged", "Prepared", "Rejected", "Unknown"},
    "Staged": {"Prepared", "Rejected", "Unknown"},
    "Prepared": {"Dispatching", "Rejected", "Unknown"},
    "Dispatching": {
        "DriverAccepted", "SpoolerQueued", "Printing", "Blocked", "Completed", "Unknown"
    },
    "DriverAccepted": {"SpoolerQueued", "Printing", "Blocked", "Completed", "Unknown"},
    "SpoolerQueued": {"Printing", "Blocked", "Completed", "Failed", "Unknown"},
    "Printing": {"Blocked", "Completed", "Failed", "Unknown"},
    "Blocked": {"SpoolerQueued", "Printing", "Completed", "Failed", "Unknown"},
}


class StrictModel(BaseModel):
    model_config = ConfigDict(extra="forbid")


class CreatePrintJob(StrictModel):
    command_id: UUID
    object_id: UUID
    payload: Literal["local", "uuid"] = "local"
    copies: int = Field(default=1, ge=1, le=100)


class CreatePrintRequest(StrictModel):
    object_id: UUID
    template_id: UUID | None = None
    payload: Literal["local", "uuid"] = "local"
    copies: int = Field(default=1, ge=1, le=1)


class LabelElement(StrictModel):
    id: str = Field(min_length=1, max_length=80, pattern=r"^[A-Za-z0-9_-]+$")
    kind: Literal["qr", "text", "field", "site_branding", "inventoryzing_branding"]
    x_mm: float = Field(ge=0, le=1000)
    y_mm: float = Field(ge=0, le=1000)
    width_mm: float = Field(gt=0, le=1000)
    height_mm: float = Field(gt=0, le=1000)
    content: str = Field(default="", max_length=500)
    show_label: bool = False
    font_size_mm: float = Field(default=3, ge=1, le=30)
    wrap_text: bool = False
    auto_fit: bool = False
    min_font_size_mm: float = Field(default=1, ge=0.8, le=30)
    align: Literal["left", "center", "right"] = "center"
    vertical_align: Literal["top", "center", "bottom"] = "center"

    @model_validator(mode="after")
    def minimum_font_fits_range(self):
        if self.min_font_size_mm > self.font_size_mm:
            raise ValueError("Minimum font size cannot exceed the preferred font size.")
        return self


class LabelTemplateDefinition(StrictModel):
    elements: list[LabelElement] = Field(default_factory=list, max_length=40)
    orientation: Literal["normal", "rotated"] = "normal"


class SaveLabelTemplate(StrictModel):
    name: str = Field(min_length=1, max_length=160)
    width_mm: float = Field(gt=0, le=300)
    height_mm: float = Field(gt=0, le=1000)
    media_kind: Literal["die_cut", "continuous"] = "die_cut"
    definition: LabelTemplateDefinition
    is_default: bool = False

    @model_validator(mode="after")
    def elements_fit_media(self):
        if self.definition.orientation == "rotated" and (
            self.media_kind != "die_cut" or (self.width_mm, self.height_mm) != (23, 23)
        ):
            raise ValueError("Rotated layout is currently supported only for the DK-1221 preset.")
        for element in self.definition.elements:
            if (
                element.x_mm + element.width_mm > self.width_mm
                or element.y_mm + element.height_mm > self.height_mm
            ):
                raise ValueError(f"Element {element.id} extends beyond the label.")
            if element.kind in {"qr", "text", "field"} and not element.content:
                raise ValueError(f"Element {element.id} requires content.")
        if len({element.id for element in self.definition.elements}) != len(
            self.definition.elements
        ):
            raise ValueError("Element IDs must be unique within a template.")
        return self


class LabelTemplateView(BaseModel):
    id: UUID
    name: str
    revision: int
    width_mm: float
    height_mm: float
    media_kind: Literal["die_cut", "continuous"]
    definition: LabelTemplateDefinition
    is_default: bool
    has_unpublished_changes: bool = False
    updated_at: datetime


class MediaPreset(BaseModel):
    id: str
    name: str
    width_mm: float
    height_mm: float
    media_kind: Literal["die_cut", "continuous"]
    margin_x_mm: float = 0
    margin_y_mm: float = 0


class PrinterMediaReport(StrictModel):
    width_mm: int = Field(ge=0, le=255)
    height_mm: int = Field(ge=0, le=255)
    media_type: int = Field(ge=0, le=255)
    state: Literal["Ready", "Printing", "Completed", "Blocked", "Invalid", "Unavailable"]


class PrinterMediaView(BaseModel):
    available: bool
    width_mm: int | None = None
    height_mm: int | None = None
    media_kind: Literal["die_cut", "continuous", "unknown"] | None = None
    state: str | None = None
    observed_at: datetime | None = None


LABELING_PLACEHOLDERS = (
    PropertyDefinition(
        key="labeling.printed_at",
        label="Printing time",
        type="datetime",
        description="Time this label request is rendered. Previews use their preview-render time.",
        example="01 Aug 2036 12:00 UTC",
        provider="labeling",
    ),
)


MEDIA_PRESETS = (
    MediaPreset(id="brother-dk-11201", name="Brother DK-11201 · 29 × 90 mm", width_mm=29,
                height_mm=90, media_kind="die_cut"),
    MediaPreset(id="brother-dk-11209", name="Brother DK-11209 · 62 × 29 mm", width_mm=62,
                height_mm=29, media_kind="die_cut"),
    MediaPreset(id="brother-dk-11202", name="Brother DK-11202 · 62 × 100 mm", width_mm=62,
                height_mm=100, media_kind="die_cut"),
    MediaPreset(id="brother-dk-11204", name="Brother DK-11204 · 17 × 54 mm", width_mm=17,
                height_mm=54, media_kind="die_cut"),
    MediaPreset(id="brother-dk-1221", name="Brother DK-1221 · 23 × 23 mm", width_mm=23,
                height_mm=23, media_kind="die_cut"),
    MediaPreset(id="brother-dk-2212", name="Brother DK-2212 · 62 mm continuous",
                width_mm=62, height_mm=50, media_kind="continuous"),
    MediaPreset(id="brother-dk-2214", name="Brother DK-2214 · 12 mm continuous",
                width_mm=12, height_mm=50, media_kind="continuous"),
)


@dataclass(frozen=True)
class RasterMediaProfile:
    profile_id: str
    media_kind: Literal["die_cut", "continuous"]
    width_mm: int
    height_mm: float
    printable_width: int
    raster_lines: int
    left_margin: int
    feed_margin_dots: int

    @property
    def id(self) -> str:
        return self.profile_id

    @property
    def media_length_mm(self) -> int:
        return 0 if self.media_kind == "continuous" else round(self.height_mm)


# Brother QL-800 raster reference, page-size and raster-line tables (300 dpi).
# https://download.brother.com/welcome/docp100278/cv_ql800_eng_raster_101.pdf
# Feed margins belong to the printer; never add them as extra raster rows.
RASTER_MEDIA_PROFILES = (
    # Cross-feed widths below are hardware-tested or deliberately maximized
    # within the 720-pin head. Feed lengths remain the documented values.
    RasterMediaProfile("brother-ql820nwb-29x90-mono-300", "die_cut",
                       29, 90, 342, 991, 372, 0),
    RasterMediaProfile("brother-ql820nwb-62x29-mono-300", "die_cut",
                       62, 29, 720, 271, 0, 0),
    RasterMediaProfile("brother-ql820nwb-62x100-mono-300", "die_cut",
                       62, 100, 720, 1109, 0, 0),
    RasterMediaProfile("brother-ql820nwb-17x54-mono-300", "die_cut",
                       17, 54, 201, 566, 519, 0),
    # Hardware-verified on QL-820NWB with DK-1221 (2026-09-17): full
    # 272-dot physical width at pin offset 424 prints edge-to-edge.
    # Keep 202 feed lines: sending all 272 disturbed cutter registration.
    RasterMediaProfile("brother-ql820nwb-23x23-mono-300", "die_cut",
                       23, 23, 272, 202, 424, 0),
)

# Continuous media has a fixed cross-feed geometry and a template-defined
# feed length. This is the only catalog the transport needs; the agent receives
# the resolved protocol values with each request.
CONTINUOUS_RASTER_GEOMETRY = {
    12: (142, 549),
    62: (720, 0),
}


def raster_media_profile(template: LabelTemplateView) -> RasterMediaProfile:
    for profile in RASTER_MEDIA_PROFILES:
        if (template.width_mm, template.height_mm, template.media_kind) == (
            profile.width_mm, profile.height_mm, profile.media_kind
        ):
            return profile
    if template.media_kind == "continuous" and template.width_mm in CONTINUOUS_RASTER_GEOMETRY:
        total_length_dots = round(template.height_mm * 300 / 25.4)
        if not 150 <= total_length_dots <= 11811:
            raise ValueError("Continuous labels must be between 12.7 and 1000 mm long.")
        feed_margin_dots = 35
        raster_lines = total_length_dots - 2 * feed_margin_dots
        printable_width, left_margin = CONTINUOUS_RASTER_GEOMETRY[round(template.width_mm)]
        length = f"{template.height_mm:g}"
        return RasterMediaProfile(
            f"brother-ql820nwb-continuous-{round(template.width_mm)}x{length}-mono-300",
            "continuous", round(template.width_mm), template.height_mm,
            printable_width, raster_lines, left_margin, feed_margin_dots,
        )
    raise ValueError(
        "Direct printing requires a supported roll profile: 17 × 54, 23 × 23, "
        "29 × 90, 62 × 29, or 62 × 100 mm die-cut; or 12 or 62 mm continuous. "
        "Choose the matching size in the template editor."
    )


class PrintRequestView(BaseModel):
    id: UUID
    state: Literal["Queued", "Claimed", "Unknown", "Completed", "Rejected", "Reset"]
    detail: str | None = None


class PrintRequestClaim(BaseModel):
    id: UUID
    profile_id: str
    media_type: Literal["application/vnd.inventoryzing.brother-raster"]
    width_mm: float
    height_mm: float
    media_kind: Literal["die_cut", "continuous"]
    feed_margin_dots: int
    raster_line_bytes: int
    raster_lines: int
    raster_base64: str


class PrintRequestReport(StrictModel):
    state: Literal["Completed", "Rejected", "Unknown"]
    detail: str | None = Field(default=None, max_length=10000)


@dataclass
class _PrintRequest:
    id: UUID
    raster: bytes
    state: Literal["Queued", "Claimed", "Unknown", "Completed", "Rejected", "Reset"]
    detail: str | None = None
    expires_at: datetime | None = None
    profile: RasterMediaProfile = RASTER_MEDIA_PROFILES[0]


class PrintRequestMailbox:
    """One in-memory handoff slot. Restart intentionally drops its contents."""

    def __init__(self) -> None:
        self._lock = Lock()
        self._active: _PrintRequest | None = None
        self._last: _PrintRequest | None = None
        self._agent_available_at: datetime | None = None

    def submit(
        self, raster: bytes, profile: RasterMediaProfile = RASTER_MEDIA_PROFILES[0]
    ) -> _PrintRequest:
        with self._lock:
            now = datetime.now(UTC)
            self._expire_active(now)
            if self._active is not None:
                raise HTTPException(
                    409,
                    {"code": "printer_busy", "message": "The printer is already handling a label."},
                )
            if self._agent_available_at is None or (
                now - self._agent_available_at
            ).total_seconds() > AGENT_AVAILABILITY_SECONDS:
                raise HTTPException(
                    503,
                    {
                        "code": "printer_unavailable",
                        "message": "The printer service is offline.",
                    },
                )
            item = _PrintRequest(
                uuid4(), raster, "Queued", expires_at=now + timedelta(seconds=QUEUE_SECONDS),
                profile=profile,
            )
            self._active = item
            return item

    def claim(self) -> _PrintRequest | None:
        with self._lock:
            now = datetime.now(UTC)
            self._agent_available_at = now
            self._expire_active(now)
            if self._active is None or self._active.state != "Queued":
                return None
            self._active.state = "Claimed"
            self._active.expires_at = now + timedelta(seconds=CLAIM_SECONDS)
            return self._active

    def report(self, request_id: UUID, report: PrintRequestReport) -> _PrintRequest:
        with self._lock:
            if self._active is None or self._active.id != request_id:
                raise HTTPException(404, "Print status is no longer available.")
            if self._active.state not in {"Claimed", "Unknown"}:
                raise HTTPException(409, "Print request is not claimed by an agent.")
            self._active.state = report.state
            self._active.detail = report.detail
            if report.state == "Unknown":
                self._active.raster = b""
                self._active.expires_at = None
                return self._active
            # Preserve only the live browser outcome. The raster ceases to exist as
            # soon as the agent reports a terminal state.
            self._last = _PrintRequest(
                self._active.id,
                b"",
                self._active.state,
                self._active.detail,
                datetime.now(UTC) + timedelta(seconds=PRINT_RESULT_SECONDS),
            )
            self._active = None
            return self._last

    def view(self, request_id: UUID) -> _PrintRequest:
        with self._lock:
            self._expire_active(datetime.now(UTC))
            if self._active is not None and self._active.id == request_id:
                return self._active
            if self._last is not None and self._last.expires_at is not None:
                if self._last.expires_at <= datetime.now(UTC):
                    self._last = None
                elif self._last.id == request_id:
                    return self._last
        raise HTTPException(404, "Print status is no longer available.")

    def force_reset(self) -> UUID | None:
        with self._lock:
            request_id = None if self._active is None else self._active.id
            self._active = None
            self._last = None
            return request_id

    def _expire_active(self, now: datetime) -> None:
        if self._active is None or self._active.expires_at is None or self._active.expires_at > now:
            return
        if self._active.state == "Queued":
            detail = "The printer did not receive the request."
        else:
            # Lost feedback does not prove physical completion. Keep this slot
            # occupied until the agent reports or the user explicitly resets it.
            self._active.state = "Unknown"
            self._active.detail = (
                "The printer did not confirm whether the label printed. "
                "Clear the printer status before trying again."
            )
            self._active.raster = b""
            self._active.expires_at = None
            return
        self._last = _PrintRequest(
            self._active.id,
            b"",
            "Rejected",
            detail,
            now + timedelta(seconds=PRINT_RESULT_SECONDS),
        )
        self._active = None


class PrinterStatusMailbox:
    """Latest transient device observation; printer state is not inventory data."""

    def __init__(self) -> None:
        self._lock = Lock()
        self._report: PrinterMediaReport | None = None
        self._observed_at: datetime | None = None

    def update(self, report: PrinterMediaReport) -> PrinterMediaView:
        with self._lock:
            self._report = report
            self._observed_at = datetime.now(UTC)
        return self.view()

    def view(self) -> PrinterMediaView:
        with self._lock:
            if (
                self._report is None
                or self._observed_at is None
                or datetime.now(UTC) - self._observed_at > timedelta(seconds=10)
            ):
                return PrinterMediaView(available=False)
            kind: Literal["die_cut", "continuous", "unknown"] = (
                "die_cut" if self._report.media_type == 0x0B
                else "continuous" if self._report.media_type == 0x0A
                else "unknown"
            )
            return PrinterMediaView(
                available=self._report.state != "Unavailable",
                width_mm=self._report.width_mm or None,
                height_mm=self._report.height_mm or None,
                media_kind=kind,
                state=self._report.state,
                observed_at=self._observed_at,
            )


class PrintObservationView(BaseModel):
    observation_id: UUID
    source: str
    code: str
    detail: str | None
    raw_job_status: int | None
    raw_printer_status: int | None
    raw_provider_status: int | None
    observed_at: datetime


class PrintAttemptView(BaseModel):
    id: UUID
    state: str
    version: int
    completion_evidence: str | None
    updated_at: datetime
    observations: list[PrintObservationView]


class PrintJobView(BaseModel):
    id: UUID
    object_id: UUID
    profile_id: str
    copies: int
    payload: str
    created_at: datetime
    attempt: PrintAttemptView


class ClaimRequest(StrictModel):
    agent_id: str = Field(min_length=1, max_length=160)


class PrintClaim(BaseModel):
    attempt_id: UUID
    lease_id: UUID
    lease_expires_at: datetime
    artifact_id: UUID
    artifact_sha256: str = Field(min_length=64, max_length=64)
    media_type: str = Field(min_length=1)
    width_mm: float = Field(gt=0)
    height_mm: float = Field(gt=0)
    pixel_width: int = Field(gt=0)
    pixel_height: int = Field(gt=0)
    dpi_x: int = Field(gt=0)
    dpi_y: int = Field(gt=0)
    palette: str = Field(min_length=1)
    profile_id: str = Field(min_length=1)
    copies: int = Field(gt=0, le=100)
    document_name: str = Field(min_length=1)


class LeaseRequest(StrictModel):
    lease_id: UUID


class DispatchAuthorizationView(BaseModel):
    acknowledgement_id: UUID
    status: Literal["Granted"]
    detail: str


class AgentObservation(StrictModel):
    observation_id: UUID
    source: str = Field(min_length=1, max_length=80)
    code: str = Field(min_length=1, max_length=160)
    detail: str | None = Field(default=None, max_length=10000)
    raw_job_status: int | None = None
    raw_printer_status: int | None = None
    raw_provider_status: int | None = None
    observed_at: datetime


class AttemptReport(StrictModel):
    lease_id: UUID | None = None
    state: Literal[
        "Claimed",
        "Staged",
        "Prepared",
        "Dispatching",
        "DriverAccepted",
        "SpoolerQueued",
        "Printing",
        "Blocked",
        "Completed",
        "Rejected",
        "Failed",
        "Unknown",
    ]
    completion_evidence: Literal[
        "SpoolerConfirmed", "BrotherMonitorConfirmed", "DeviceConfirmed"
    ] | None = None
    observations: list[AgentObservation] = Field(default_factory=list, max_length=1000)


_PLACEHOLDER = re.compile(
    r"\{([a-z][a-z0-9_]*\.[a-z][a-z0-9_]*)"
    r"(?:\[(-?\d*):(-?\d*)\]|\[(-?\d+)\])?\}"
)


def resolve_label_text(value: str, context: dict[str, str]) -> str:
    """Resolve the deliberately small label expression language."""

    def replace(match: re.Match[str]) -> str:
        key = match.group(1)
        resolved = context.get(key, context.get(key.removeprefix("object."), "")
                               if key.startswith("object.") else "")
        if match.group(4) is not None:
            try:
                return resolved[int(match.group(4))]
            except IndexError:
                return ""
        if match.group(2) is not None or match.group(3) is not None:
            start = int(match.group(2)) if match.group(2) not in {None, ""} else None
            stop = int(match.group(3)) if match.group(3) not in {None, ""} else None
            return resolved[slice(start, stop)]
        return resolved

    # Validate template syntax, not substituted values (names may contain braces).
    remainder = _PLACEHOLDER.sub("", value)
    if "{" in remainder or "}" in remainder:
        raise ValueError(
            "Unknown label placeholder syntax. Use a namespaced property such as "
            "object.name or maintenance.next_due; string indexes and slices are supported."
        )
    return _PLACEHOLDER.sub(replace, value)


def _element_text(element: LabelElement, context: dict[str, Any]) -> str:
    if element.kind == "site_branding":
        return context["site_name"]
    if element.kind == "inventoryzing_branding":
        return "inventoryzing"
    value = resolve_label_text(element.content, context)
    if value and element.kind == "field" and element.show_label:
        match = _PLACEHOLDER.fullmatch(element.content)
        caption = context.get("caption:" + match.group(1), "") if match else ""
        if caption:
            return f"{caption}: {value}"
    return value


@dataclass(frozen=True)
class _TextLayout:
    lines: tuple[str, ...]
    font: ImageFont.FreeTypeFont | ImageFont.ImageFont
    font_size_mm: float
    line_height_px: int


def _text_width(draw: ImageDraw.ImageDraw, value: str, font) -> int:
    box = draw.textbbox((0, 0), value, font=font)
    return box[2] - box[0]


def _ellipsize(draw: ImageDraw.ImageDraw, value: str, font, max_width: int) -> str:
    ellipsis = "…"
    if _text_width(draw, ellipsis, font) > max_width:
        return ""
    if _text_width(draw, value, font) <= max_width:
        return value
    low, high = 0, len(value)
    while low < high:
        middle = (low + high + 1) // 2
        if _text_width(draw, value[:middle].rstrip() + ellipsis, font) <= max_width:
            low = middle
        else:
            high = middle - 1
    return value[:low].rstrip() + ellipsis


def _wrap_line(draw: ImageDraw.ImageDraw, value: str, font, max_width: int) -> list[str]:
    words = value.split()
    if not words:
        return [""]
    lines: list[str] = []
    current = ""
    for word in words:
        candidate = f"{current} {word}".strip()
        if current and _text_width(draw, candidate, font) > max_width:
            lines.append(current)
            current = ""
        while _text_width(draw, word, font) > max_width:
            split = len(word)
            while split > 1 and _text_width(draw, word[:split], font) > max_width:
                split -= 1
            if current:
                lines.append(current)
                current = ""
            lines.append(word[:split])
            word = word[split:]
        current = f"{current} {word}".strip()
    if current:
        lines.append(current)
    return lines


def _layout_text(
    element: LabelElement, value: str, box_width: int, box_height: int, pixels_per_mm: float
) -> _TextLayout:
    measure = ImageDraw.Draw(Image.new("1", (1, 1), 1))
    preferred = element.font_size_mm
    minimum = min(element.min_font_size_mm, preferred)
    sizes = [preferred]
    if element.auto_fit:
        steps = round((preferred - minimum) * 10)
        sizes = [preferred - step / 10 for step in range(steps + 1)]
        if sizes[-1] > minimum:
            sizes.append(minimum)

    selected = None
    for size in sizes:
        font = ImageFont.load_default(size=max(8, round(size * pixels_per_mm)))
        line_box = measure.textbbox((0, 0), "Ag", font=font)
        line_height = max(1, line_box[3] - line_box[1])
        lines = _wrap_line(measure, value, font, box_width) if element.wrap_text else [value]
        max_lines = max(1, box_height // line_height)
        selected = (size, font, line_height, lines, max_lines)
        if not element.auto_fit or (
            len(lines) <= max_lines
            and all(_text_width(measure, line, font) <= box_width for line in lines)
        ):
            break

    assert selected is not None
    size, font, line_height, lines, max_lines = selected
    overflowed = len(lines) > max_lines
    visible = lines[:max_lines]
    if visible and (overflowed or any(
        _text_width(measure, line, font) > box_width for line in visible
    )):
        final_line = visible[-1] + "…" if overflowed else visible[-1]
        visible[-1] = _ellipsize(measure, final_line, font, box_width)
    return _TextLayout(tuple(visible), font, size, line_height)


def printable_bounds(template) -> tuple[int, int, int, int]:
    """Printable rectangle in full-label 300-DPI coordinates; never a scale factor."""
    width = max(1, round(template.width_mm * 300 / 25.4))
    height = max(1, round(template.height_mm * 300 / 25.4))
    try:
        profile = raster_media_profile(template)
    except ValueError:
        return (0, 0, width, height)
    x = (width - profile.printable_width) // 2
    y = (height - profile.raster_lines) // 2
    return (x, y, x + profile.printable_width, y + profile.raster_lines)


def label_is_rotated(template: LabelTemplateView) -> bool:
    return template.definition.orientation == "rotated"


def oriented_printable_bounds(template: LabelTemplateView) -> tuple[int, int, int, int]:
    """The usable rectangle as seen by an editor using the template orientation."""
    left, top, right, bottom = printable_bounds(template)
    if not label_is_rotated(template):
        return (left, top, right, bottom)
    width = round(template.width_mm * 300 / 25.4)
    height = round(template.height_mm * 300 / 25.4)
    if width == height:
        return (top, width - right, bottom, width - left)
    return (left, top, right, bottom)


def render_template_svg(template: LabelTemplateView, context: dict[str, Any]) -> bytes:
    clips: list[str] = []
    body: list[str] = [
        f'<rect width="{template.width_mm:g}" height="{template.height_mm:g}" fill="white"/>'
    ]
    for element in template.definition.elements:
        clip_id = f"clip-{element.id}"
        clips.append(
            f'<clipPath id="{clip_id}"><rect x="{element.x_mm:g}" y="{element.y_mm:g}" '
            f'width="{element.width_mm:g}" height="{element.height_mm:g}"/></clipPath>'
        )
        if element.kind == "qr":
            value = _element_text(element, context)
            if not value:
                continue  # Missing properties omit the QR rather than encoding an empty payload.
            matrix = tuple(tuple(bool(cell) for cell in row) for row in segno.make(
                value, micro=False, error="m"
            ).matrix)
            total = len(matrix) + 8
            scale = min(element.width_mm, element.height_mm) / total
            left = element.x_mm + (element.width_mm - total * scale) / 2
            top = element.y_mm + (element.height_mm - total * scale) / 2
            for row_index, row in enumerate(matrix):
                for column_index, dark in enumerate(row):
                    if dark:
                        body.append(
                            f'<rect x="{left + (column_index + 4) * scale:g}" '
                            f'y="{top + (row_index + 4) * scale:g}" width="{scale:g}" '
                            f'height="{scale:g}" fill="black"/>'
                        )
            continue
        if element.kind == "inventoryzing_branding":
            body.append(
                f'<image x="{element.x_mm:g}" y="{element.y_mm:g}" '
                f'width="{element.width_mm:g}" height="{element.height_mm:g}" '
                f'href="{INVENTORYZING_LOGO_DATA_URI}" preserveAspectRatio="xMidYMid meet" '
                f'clip-path="url(#{clip_id})"/>'
            )
            continue
        if element.kind == "site_branding" and isinstance(context.get("site_logo_png"), bytes):
            logo_data = b64encode(context["site_logo_png"]).decode("ascii")
            body.append(
                f'<image x="{element.x_mm:g}" y="{element.y_mm:g}" '
                f'width="{element.width_mm:g}" height="{element.height_mm:g}" '
                f'href="data:image/png;base64,{logo_data}" preserveAspectRatio="xMidYMid meet" '
                f'clip-path="url(#{clip_id})"/>'
            )
            continue
        value = _element_text(element, context)
        anchor = {"left": "start", "center": "middle", "right": "end"}[element.align]
        x = {
            "left": element.x_mm,
            "center": element.x_mm + element.width_mm / 2,
            "right": element.x_mm + element.width_mm,
        }[element.align]
        pixels_per_mm = 300 / 25.4
        layout = _layout_text(
            element, value, max(1, round(element.width_mm * pixels_per_mm)),
            max(1, round(element.height_mm * pixels_per_mm)), pixels_per_mm,
        )
        line_height_mm = layout.line_height_px / pixels_per_mm
        total_height_mm = line_height_mm * len(layout.lines)
        block_top = {
            "top": element.y_mm,
            "center": element.y_mm + (element.height_mm - total_height_mm) / 2,
            "bottom": element.y_mm + element.height_mm - total_height_mm,
        }[element.vertical_align]
        first_y = block_top + line_height_mm / 2
        weight = "600" if element.kind in {"site_branding", "inventoryzing_branding"} else "400"
        for index, line in enumerate(layout.lines):
            y = first_y + index * line_height_mm
            body.append(
                f'<text x="{x:g}" y="{y:g}" font-family="IBM Plex Sans, sans-serif" '
                f'font-size="{layout.font_size_mm:g}" font-weight="{weight}" '
                f'text-anchor="{anchor}" dominant-baseline="middle" '
                f'clip-path="url(#{clip_id})">{html.escape(line)}</text>'
            )
    left, top, right, bottom = printable_bounds(template)
    mm = 25.4 / 300
    clips.append(
        f'<clipPath id="printable-area"><rect x="{left * mm:g}" y="{top * mm:g}" '
        f'width="{(right - left) * mm:g}" height="{(bottom - top) * mm:g}"/></clipPath>'
    )
    rotation = (
        f' transform="rotate(90 {template.width_mm / 2:g} {template.height_mm / 2:g})"'
        if label_is_rotated(template)
        else ""
    )
    document = (
        f'<svg xmlns="http://www.w3.org/2000/svg" width="{template.width_mm:g}mm" '
        f'height="{template.height_mm:g}mm" viewBox="0 0 {template.width_mm:g} '
        f'{template.height_mm:g}"><defs>{"".join(clips)}</defs>'
        f'<rect width="100%" height="100%" fill="white"/>'
        f'<g clip-path="url(#printable-area)"{rotation}'
        f'>{"".join(body)}</g></svg>'
    )
    return document.encode("utf-8")


def _render_template_bitmap(
    template: LabelTemplateView, context: dict[str, Any], width: int, height: int
) -> Image.Image:
    image = Image.new("1", (width, height), 1)
    draw = ImageDraw.Draw(image)
    scale_x = scale_y = 300 / 25.4
    for element in template.definition.elements:
        left = round(element.x_mm * scale_x)
        top = round(element.y_mm * scale_y)
        box_width = max(1, round(element.width_mm * scale_x))
        box_height = max(1, round(element.height_mm * scale_y))
        if element.kind == "qr":
            value = _element_text(element, context)
            if not value:
                continue  # Missing properties omit the QR rather than encoding an empty payload.
            matrix = tuple(tuple(bool(cell) for cell in row) for row in segno.make(
                value, micro=False, error="m"
            ).matrix)
            total = len(matrix) + 8
            module = max(1, min(box_width, box_height) // total)
            qr_size = total * module
            qr_left = left + (box_width - qr_size) // 2
            qr_top = top + (box_height - qr_size) // 2
            for row_index, row in enumerate(matrix):
                for column_index, dark in enumerate(row):
                    if dark:
                        x = qr_left + (column_index + 4) * module
                        y = qr_top + (row_index + 4) * module
                        draw.rectangle((x, y, x + module - 1, y + module - 1), fill=0)
            continue
        if element.kind == "inventoryzing_branding":
            logo = Image.open(BRANDING_DIR / "inventoryzing-label.png").convert("RGBA")
            logo.thumbnail((box_width, box_height), Image.Resampling.LANCZOS)
            logo_box = Image.new("RGBA", (box_width, box_height), "white")
            logo_box.alpha_composite(
                logo, ((box_width - logo.width) // 2, (box_height - logo.height) // 2)
            )
            monochrome = logo_box.convert("L").point(
                lambda value: 255 if value >= 128 else 0, mode="1"
            )
            image.paste(monochrome, (left, top))
            continue
        if element.kind == "site_branding" and isinstance(context.get("site_logo_png"), bytes):
            try:
                logo = Image.open(BytesIO(context["site_logo_png"])).convert("RGBA")
                logo.thumbnail((box_width, box_height), Image.Resampling.LANCZOS)
                logo_box = Image.new("RGBA", (box_width, box_height), "white")
                logo_box.alpha_composite(
                    logo, ((box_width - logo.width) // 2, (box_height - logo.height) // 2)
                )
                monochrome = logo_box.convert("L").point(
                    lambda value: 255 if value >= 128 else 0, mode="1"
                )
                image.paste(monochrome, (left, top))
                continue
            except (UnidentifiedImageError, OSError):
                pass
        value = _element_text(element, context)
        layout = _layout_text(element, value, box_width, box_height, scale_y)
        layer = Image.new("1", (box_width, box_height), 1)
        layer_draw = ImageDraw.Draw(layer)
        block_height = layout.line_height_px * len(layout.lines)
        y = {
            "top": 0,
            "center": (box_height - block_height) // 2,
            "bottom": box_height - block_height,
        }[element.vertical_align]
        for line in layout.lines:
            text_box = layer_draw.textbbox((0, 0), line, font=layout.font)
            text_width = text_box[2] - text_box[0]
            x = {
                "left": 0,
                "center": (box_width - text_width) // 2,
                "right": box_width - text_width,
            }[element.align]
            layer_draw.text((x, y - text_box[1]), line, font=layout.font, fill=0)
            y += layout.line_height_px
        image.paste(layer, (left, top))
    return image.rotate(90, expand=False) if label_is_rotated(template) else image


def render_template_png(template: LabelTemplateView, context: dict[str, str]) -> bytes:
    width = max(1, round(template.width_mm * 300 / 25.4))
    height = max(1, round(template.height_mm * 300 / 25.4))
    output = BytesIO()
    bitmap = _render_template_bitmap(template, context, width, height)
    bounds = printable_bounds(template)
    preview = Image.new("1", (width, height), 1)
    preview.paste(bitmap.crop(bounds), bounds[:2])
    preview.save(
        output, format="PNG", optimize=True, dpi=(300, 300)
    )
    return output.getvalue()


def render_template_brother_raster(
    template: LabelTemplateView, context: dict[str, str]
) -> bytes:
    profile = raster_media_profile(template)
    bitmap = _render_template_bitmap(
        template, context, round(template.width_mm * 300 / 25.4),
        round(template.height_mm * 300 / 25.4),
    ).crop(printable_bounds(template))
    raster = bytearray(BROTHER_RASTER_LINE_BYTES * profile.raster_lines)
    pixels = bitmap.load()
    for y in range(profile.raster_lines):
        for x in range(profile.printable_width):
            if pixels[x, y] == 0:
                pin = BROTHER_RASTER_WIDTH - 1 - (profile.left_margin + x)
                raster[y * BROTHER_RASTER_LINE_BYTES + pin // 8] |= 0x80 >> (pin % 8)
    return bytes(raster)


def render_tag_png(value: str) -> bytes:
    qr = segno.make(value, micro=False, error="m")
    matrix = tuple(tuple(bool(cell) for cell in row) for row in qr.matrix)
    modules = len(matrix)
    scale = max(1, (PIXEL_WIDTH - 32) // (modules + 8))
    qr_size = (modules + 8) * scale
    left = (PIXEL_WIDTH - qr_size) // 2
    top = 24
    pixels = [bytearray([255]) * PIXEL_WIDTH for _ in range(PIXEL_HEIGHT)]
    for row_index, row in enumerate(matrix):
        for column_index, dark in enumerate(row):
            if not dark:
                continue
            x = left + (column_index + 4) * scale
            y = top + (row_index + 4) * scale
            for output_row in range(y, y + scale):
                pixels[output_row][x : x + scale] = b"\x00" * scale
    _draw_identifier(pixels, value, top + qr_size + 30)
    raw = b"".join(b"\x00" + bytes(row) for row in pixels)
    return b"\x89PNG\r\n\x1a\n" + _chunk(
        b"IHDR", struct.pack(">IIBBBBB", PIXEL_WIDTH, PIXEL_HEIGHT, 8, 0, 0, 0, 0)
    ) + _chunk(b"IDAT", zlib.compress(raw, 9)) + _chunk(b"IEND", b"")


def render_brother_raster(value: str) -> bytes:
    """Render one immutable 300 dpi, 29 x 90 mm Brother monochrome raster page.

    The QL-820NWB accepts 90 bytes (720 head dots) for every monochrome line. The
    29 mm die-cut label exposes 306 printable dots at the documented 408-dot left
    offset; the remaining dots are explicit white padding, never driver scaling.
    """
    raster = bytearray(BROTHER_RASTER_LINE_BYTES * BROTHER_RASTER_HEIGHT)
    qr = segno.make(value, micro=False, error="m")
    matrix = tuple(tuple(bool(cell) for cell in row) for row in qr.matrix)
    modules = len(matrix)
    scale = max(1, (BROTHER_PRINTABLE_WIDTH - 32) // (modules + 8))
    qr_size = (modules + 8) * scale
    left = (BROTHER_PRINTABLE_WIDTH - qr_size) // 2
    top = 24
    for row_index, row in enumerate(matrix):
        for column_index, dark in enumerate(row):
            if not dark:
                continue
            x = left + (column_index + 4) * scale
            y = top + (row_index + 4) * scale
            for output_row in range(y, y + scale):
                for output_column in range(x, x + scale):
                    _set_brother_black(raster, output_column, output_row)
    _draw_brother_identifier(raster, value, top + qr_size + 30)
    return bytes(raster)


def _set_brother_black(raster: bytearray, printable_x: int, printable_y: int) -> None:
    if not (
        0 <= printable_x < BROTHER_PRINTABLE_WIDTH
        and 0 <= printable_y < BROTHER_PRINTABLE_HEIGHT
    ):
        return
    # The QL printhead's byte stream is right-to-left. Its documented 408-dot
    # margin is on the visual left, so reverse the physical pin location while
    # retaining the visual coordinate system used by the renderer.
    x = BROTHER_RASTER_WIDTH - 1 - (BROTHER_LEFT_MARGIN + printable_x)
    y = printable_y
    offset = y * BROTHER_RASTER_LINE_BYTES + x // 8
    raster[offset] |= 0x80 >> (x % 8)


def _draw_brother_identifier(raster: bytearray, value: str, top: int) -> None:
    text = value.upper()
    scale = max(1, min(6, BROTHER_PRINTABLE_WIDTH // max(1, len(text) * 6)))
    width = len(text) * 6 * scale - scale
    left = max(0, (BROTHER_PRINTABLE_WIDTH - width) // 2)
    for character_index, character in enumerate(text):
        glyph = GLYPHS.get(character)
        if glyph is None:
            continue
        for row_index, row in enumerate(glyph):
            for column_index, dark in enumerate(row):
                if dark != "1":
                    continue
                for output_row in range(row_index * scale, (row_index + 1) * scale):
                    for output_column in range(column_index * scale, (column_index + 1) * scale):
                        _set_brother_black(
                            raster,
                            left + (character_index * 6 * scale) + output_column,
                            top + output_row,
                        )


def _chunk(kind: bytes, payload: bytes) -> bytes:
    body = kind + payload
    return struct.pack(">I", len(payload)) + body + struct.pack(">I", zlib.crc32(body))


GLYPHS = {
    "I": ("11111", "00100", "00100", "00100", "00100", "00100", "11111"),
    "0": ("01110", "10001", "10011", "10101", "11001", "10001", "01110"),
    "1": ("00100", "01100", "00100", "00100", "00100", "00100", "01110"),
    "2": ("01110", "10001", "00001", "00010", "00100", "01000", "11111"),
    "3": ("11110", "00001", "00001", "01110", "00001", "00001", "11110"),
    "4": ("00010", "00110", "01010", "10010", "11111", "00010", "00010"),
    "5": ("11111", "10000", "10000", "11110", "00001", "00001", "11110"),
    "6": ("01110", "10000", "10000", "11110", "10001", "10001", "01110"),
    "7": ("11111", "00001", "00010", "00100", "01000", "01000", "01000"),
    "8": ("01110", "10001", "10001", "01110", "10001", "10001", "01110"),
    "9": ("01110", "10001", "10001", "01111", "00001", "00001", "01110"),
}


def _draw_identifier(pixels: list[bytearray], value: str, top: int) -> None:
    text = value.upper()
    scale = 6
    width = len(text) * 6 * scale - scale
    left = max(0, (PIXEL_WIDTH - width) // 2)
    for character_index, character in enumerate(text):
        glyph = GLYPHS.get(character)
        if glyph is None:
            continue
        for row_index, row in enumerate(glyph):
            for column_index, dark in enumerate(row):
                if dark != "1":
                    continue
                x = left + (character_index * 6 + column_index) * scale
                y = top + row_index * scale
                for output_row in range(y, min(y + scale, PIXEL_HEIGHT)):
                    pixels[output_row][x : x + scale] = b"\x00" * scale


def _observation_views(connection, attempt_id: UUID) -> list[PrintObservationView]:
    rows = connection.execute(text("""
        SELECT observation_id, source, code, detail, raw_job_status, raw_printer_status,
               raw_provider_status, observed_at
        FROM iz_print.observations
        WHERE attempt_id=:attempt
        ORDER BY received_at, observation_id
    """), {"attempt": attempt_id}).mappings().all()
    return [PrintObservationView(**row) for row in rows]


def _job_view(connection, job_id: UUID) -> PrintJobView:
    row = connection.execute(
        text("""
            SELECT job.id, job.object_id, job.profile_id, job.copies, job.payload, job.created_at,
                   attempt.id AS attempt_id, attempt.state, attempt.version,
                   attempt.completion_evidence, attempt.updated_at
            FROM iz_print.jobs job
            JOIN iz_print.attempts attempt ON attempt.job_id = job.id
            WHERE job.id = :job
        """),
        {"job": job_id},
    ).mappings().first()
    if row is None:
        raise HTTPException(404, "Print job not found.")
    return PrintJobView(
        id=row["id"], object_id=row["object_id"], profile_id=row["profile_id"],
        copies=row["copies"], payload=row["payload"], created_at=row["created_at"],
        attempt=PrintAttemptView(
            id=row["attempt_id"], state=row["state"], version=row["version"],
            completion_evidence=row["completion_evidence"], updated_at=row["updated_at"],
            observations=_observation_views(connection, row["attempt_id"]),
        ),
    )


def _print_value(connection, object_id: UUID, payload: Literal["local", "uuid"]) -> str:
    item = connection.execute(
        text("""
            SELECT object.id,
                   CASE WHEN identifier.value IS NULL THEN NULL
                        ELSE 'I' || identifier.value END AS alias
            FROM iz.objects object
            JOIN iz.entities entity ON entity.id=object.id AND entity.archived_at IS NULL
            LEFT JOIN iz.identifiers identifier ON identifier.entity_id=object.id
                AND identifier.issuer_site_id=(SELECT id FROM iz.sites WHERE local)
            WHERE object.id=:id
        """), {"id": object_id},
    ).mappings().first()
    if item is None:
        raise HTTPException(404, "Object not found.")
    value = item["alias"] if payload == "local" else str(object_id)
    if value is None:
        raise HTTPException(409, "Allocate a local identifier before printing a local tag.")
    return value


def _label_context(connection, object_id: UUID, registry: PropertyRegistry) -> dict[str, Any]:
    properties = registry.resolve(connection, object_id)
    context = {field.key: field.formatted_value for field in properties}
    context.update({"caption:" + field.key: field.label for field in properties})
    site = connection.execute(
        text("SELECT display_name, logo_png FROM iz.sites WHERE local")
    ).mappings().one()
    context["site_name"] = site["display_name"] or "Local site"
    if site["logo_png"] is not None:
        context["site_logo_png"] = bytes(site["logo_png"])
    context["labeling.printed_at"] = format_property(datetime.now(UTC), "datetime")
    context["caption:labeling.printed_at"] = "Printed"
    return context


def _sample_label_context(connection, registry: PropertyRegistry) -> dict[str, Any]:
    context = {field.key: field.example for field in registry.definitions()}
    context.update({"caption:" + field.key: field.label for field in registry.definitions()})
    site = connection.execute(
        text("SELECT display_name, logo_png FROM iz.sites WHERE local")
    ).mappings().one()
    context["site_name"] = site["display_name"] or "Local site"
    if site["logo_png"] is not None:
        context["site_logo_png"] = bytes(site["logo_png"])
    context["labeling.printed_at"] = format_property(datetime.now(UTC), "datetime")
    context["caption:labeling.printed_at"] = "Printed"
    return context


def _template_view(row) -> LabelTemplateView:
    return LabelTemplateView(
        id=row["id"],
        name=row["name"],
        revision=row["current_revision"],
        width_mm=float(row["width_mm"]),
        height_mm=float(row["height_mm"]),
        media_kind=row["media_kind"],
        definition=LabelTemplateDefinition.model_validate(row["definition"]),
        is_default=row["is_default"],
        has_unpublished_changes=row["has_unpublished_changes"],
        updated_at=row["updated_at"],
    )


def _load_template(connection, template_id: UUID | None = None) -> LabelTemplateView:
    where = "template.id=:id" if template_id is not None else "template.is_default"
    parameters = {"id": template_id} if template_id is not None else {}
    row = connection.execute(text(f"""
        SELECT template.id, template.name, template.current_revision, template.is_default,
               template.updated_at, template.draft_width_mm AS width_mm,
               template.draft_height_mm AS height_mm,
               template.draft_media_kind AS media_kind,
               template.draft_definition AS definition,
               (template.draft_width_mm IS DISTINCT FROM revision.width_mm
                OR template.draft_height_mm IS DISTINCT FROM revision.height_mm
                OR template.draft_media_kind IS DISTINCT FROM revision.media_kind
                OR template.draft_definition IS DISTINCT FROM revision.definition)
                   AS has_unpublished_changes
        FROM iz_print.label_templates template
        JOIN iz_print.label_template_revisions revision
          ON revision.template_id=template.id AND revision.revision=template.current_revision
        WHERE {where}
          AND template.site_id=(SELECT id FROM iz.sites WHERE local)
    """), parameters).mappings().first()
    if row is None:
        raise HTTPException(404, "Label template not found.")
    return _template_view(row)


def _validate_template_expressions(definition: LabelTemplateDefinition) -> None:
    context = {
        "name": "Example", "uuid": "01234567-89ab-cdef-0123-456789abcdef",
        "local_id": "I123456", "description": "Example", "type_name": "Equipment",
        "site_name": "Local site",
    }
    try:
        for element in definition.elements:
            _element_text(element, context)
    except (ValueError, IndexError) as error:
        raise HTTPException(422, str(error)) from error


def label_placeholder_definitions(registry: PropertyRegistry) -> list[PropertyDefinition]:
    return sorted(
        [*registry.definitions(), *LABELING_PLACEHOLDERS],
        key=lambda field: (field.provider, field.key),
    )


@router.get("/label-placeholders", response_model=list[PropertyDefinition])
def list_label_placeholders(request: Request):
    authenticate(request, "label.print")
    definitions = label_placeholder_definitions(request.app.state.property_registry)
    with request.app.state.engine.connect() as connection:
        definitions.extend(stored_definitions(connection))
    return sorted(definitions, key=lambda field: (field.provider, field.key))


@router.get("/label-media-presets", response_model=list[MediaPreset])
def list_label_media_presets(request: Request):
    authenticate(request, "label.print")
    return [preset.model_copy(update={
        "margin_x_mm": printable_bounds(preset)[0] * 25.4 / 300,
        "margin_y_mm": printable_bounds(preset)[1] * 25.4 / 300,
    }) for preset in MEDIA_PRESETS]


@router.get("/printers/default/media", response_model=PrinterMediaView)
def get_printer_media(request: Request):
    authenticate(request, "label.print")
    return request.app.state.labeling_printer_status.view()


@router.post("/agent/printers/default/media", response_model=PrinterMediaView)
def report_printer_media(report: PrinterMediaReport, request: Request):
    authenticate_service(request, "print.agent")
    return request.app.state.labeling_printer_status.update(report)


@router.get("/label-templates", response_model=list[LabelTemplateView])
def list_label_templates(request: Request):
    authenticate(request, "label.print")
    with request.app.state.engine.connect() as connection:
        rows = connection.execute(text("""
            SELECT template.id, template.name, template.current_revision, template.is_default,
                   template.updated_at, template.draft_width_mm AS width_mm,
                   template.draft_height_mm AS height_mm,
                   template.draft_media_kind AS media_kind,
                   template.draft_definition AS definition,
                   (template.draft_width_mm IS DISTINCT FROM revision.width_mm
                    OR template.draft_height_mm IS DISTINCT FROM revision.height_mm
                    OR template.draft_media_kind IS DISTINCT FROM revision.media_kind
                    OR template.draft_definition IS DISTINCT FROM revision.definition)
                       AS has_unpublished_changes
            FROM iz_print.label_templates template
            JOIN iz_print.label_template_revisions revision
              ON revision.template_id=template.id AND revision.revision=template.current_revision
            WHERE template.site_id=(SELECT id FROM iz.sites WHERE local)
            ORDER BY template.is_default DESC, lower(template.name), template.id
        """)).mappings().all()
    return [_template_view(row) for row in rows]


@router.post("/label-templates", response_model=LabelTemplateView)
def create_label_template(command: SaveLabelTemplate, request: Request):
    session = authenticate(request, "label.template.manage")
    _validate_template_expressions(command.definition)
    template_id = uuid4()
    with request.app.state.engine.begin() as connection:
        site_id = connection.scalar(text("SELECT id FROM iz.sites WHERE local"))
        if command.is_default:
            connection.execute(text("""
                UPDATE iz_print.label_templates SET is_default=false, updated_at=now()
                WHERE site_id=:site
            """), {"site": site_id})
        connection.execute(text("""
            INSERT INTO iz_print.label_templates(
                id, site_id, name, is_default, created_by, draft_width_mm,
                draft_height_mm, draft_media_kind, draft_definition, draft_updated_by)
            VALUES (:id, :site, :name, :default, :actor, :width, :height, :kind,
                    CAST(:definition AS jsonb), :actor)
        """), {"id": template_id, "site": site_id, "name": command.name.strip(),
                 "default": command.is_default, "actor": session.account_id,
                 "width": command.width_mm, "height": command.height_mm,
                 "kind": command.media_kind,
                 "definition": json.dumps(command.definition.model_dump())})
        connection.execute(text("""
            INSERT INTO iz_print.label_template_revisions(
                template_id, revision, width_mm, height_mm, media_kind, definition, created_by)
            VALUES (:id, 1, :width, :height, :kind, CAST(:definition AS jsonb), :actor)
        """), {"id": template_id, "width": command.width_mm,
                 "height": command.height_mm, "kind": command.media_kind,
                 "definition": json.dumps(command.definition.model_dump()),
                 "actor": session.account_id})
        return _load_template(connection, template_id)


@router.put("/label-templates/{template_id}", response_model=LabelTemplateView)
def update_label_template(template_id: UUID, command: SaveLabelTemplate, request: Request):
    session = authenticate(request, "label.template.manage")
    _validate_template_expressions(command.definition)
    with request.app.state.engine.begin() as connection:
        row = connection.execute(text("""
            SELECT template.id, template.site_id
            FROM iz_print.label_templates template
            WHERE template.id=:id
              AND template.site_id=(SELECT id FROM iz.sites WHERE local)
            FOR UPDATE OF template
        """), {"id": template_id}).mappings().first()
        if row is None:
            raise HTTPException(404, "Label template not found.")
        if command.is_default:
            connection.execute(text("""
                UPDATE iz_print.label_templates SET is_default=false, updated_at=now()
                WHERE site_id=:site AND id<>:id
            """), {"site": row["site_id"], "id": template_id})
        connection.execute(text("""
            UPDATE iz_print.label_templates
            SET name=:name, is_default=:default, draft_width_mm=:width,
                draft_height_mm=:height, draft_media_kind=:kind,
                draft_definition=CAST(:definition AS jsonb), draft_updated_by=:actor,
                updated_at=now()
            WHERE id=:id
        """), {"id": template_id, "name": command.name.strip(),
                 "default": command.is_default, "width": command.width_mm,
                 "height": command.height_mm, "kind": command.media_kind,
                 "definition": json.dumps(command.definition.model_dump()),
                 "actor": session.account_id})
        return _load_template(connection, template_id)


@router.post("/label-templates/{template_id}/publish", response_model=LabelTemplateView)
def publish_label_template(template_id: UUID, request: Request):
    session = authenticate(request, "label.template.manage")
    with request.app.state.engine.begin() as connection:
        row = connection.execute(text("""
            SELECT template.current_revision, template.draft_width_mm,
                   template.draft_height_mm, template.draft_media_kind,
                   template.draft_definition
            FROM iz_print.label_templates template
            WHERE template.id=:id
              AND template.site_id=(SELECT id FROM iz.sites WHERE local)
            FOR UPDATE OF template
        """), {"id": template_id}).mappings().first()
        if row is None:
            raise HTTPException(404, "Label template not found.")
        published = connection.execute(text("""
            SELECT width_mm, height_mm, media_kind, definition
            FROM iz_print.label_template_revisions
            WHERE template_id=:id AND revision=:revision
        """), {"id": template_id, "revision": row["current_revision"]}).mappings().one()
        changed = any((
            row["draft_width_mm"] != published["width_mm"],
            row["draft_height_mm"] != published["height_mm"],
            row["draft_media_kind"] != published["media_kind"],
            row["draft_definition"] != published["definition"],
        ))
        if changed:
            revision = row["current_revision"] + 1
            connection.execute(text("""
                INSERT INTO iz_print.label_template_revisions(
                    template_id, revision, width_mm, height_mm, media_kind,
                    definition, created_by)
                VALUES (:id, :revision, :width, :height, :kind,
                        CAST(:definition AS jsonb), :actor)
            """), {"id": template_id, "revision": revision,
                     "width": row["draft_width_mm"], "height": row["draft_height_mm"],
                     "kind": row["draft_media_kind"],
                     "definition": json.dumps(row["draft_definition"]),
                     "actor": session.account_id})
            connection.execute(text("""
                UPDATE iz_print.label_templates
                SET current_revision=:revision, updated_at=now()
                WHERE id=:id
            """), {"id": template_id, "revision": revision})
        return _load_template(connection, template_id)


@router.get("/label-templates/{template_id}/preview.svg")
def preview_label_template(template_id: UUID, request: Request, object_id: UUID | None = None):
    authenticate(request, "label.print")
    with request.app.state.engine.connect() as connection:
        template = _load_template(connection, template_id)
        context = (
            _label_context(connection, object_id, request.app.state.property_registry)
            if object_id else _sample_label_context(connection, request.app.state.property_registry)
        )
    try:
        output = render_template_svg(template, context)
    except ValueError as error:
        raise HTTPException(422, str(error)) from error
    return Response(
        output,
        media_type="image/svg+xml",
        headers={"Cache-Control": "private, no-store"},
    )


@router.get("/label-templates/{template_id}/preview.png")
def preview_label_template_png(
    template_id: UUID, request: Request, object_id: UUID | None = None
):
    authenticate(request, "label.print")
    with request.app.state.engine.connect() as connection:
        template = _load_template(connection, template_id)
        context = (
            _label_context(connection, object_id, request.app.state.property_registry)
            if object_id else _sample_label_context(connection, request.app.state.property_registry)
        )
    try:
        output = render_template_png(template, context)
    except ValueError as error:
        raise HTTPException(422, str(error)) from error
    return Response(output, media_type="image/png", headers={"Cache-Control": "private, no-store"})


@router.get("/objects/{object_id}/label.svg")
def preview_label_svg(
    object_id: UUID, request: Request, payload: Literal["local", "uuid"] = "local"
):
    authenticate(request, "label.print")
    with request.app.state.engine.connect() as connection:
        value = _print_value(connection, object_id, payload)
    output = BytesIO()
    segno.make(value, micro=False, error="m").save(
        output, kind="svg", scale=4, border=4, xmldecl=False, title=value
    )
    return Response(
        output.getvalue(),
        media_type="image/svg+xml",
        headers={
            "Content-Disposition": f'inline; filename="inventoryzing-{value}.svg"',
            "Cache-Control": "private, no-store",
        },
    )


@router.get("/objects/{object_id}/tag.png")
def preview_tag(
    object_id: UUID, request: Request, payload: Literal["local", "uuid"] = "local"
):
    authenticate(request, "label.print")
    with request.app.state.engine.connect() as connection:
        value = _print_value(connection, object_id, payload)
    return Response(
        render_tag_png(value),
        media_type="image/png",
        headers={"Content-Disposition": f'inline; filename="inventoryzing-{value}.png"'},
    )


def _print_mailbox(request: Request) -> PrintRequestMailbox:
    return request.app.state.labeling_mailbox


def _print_request_view(item: _PrintRequest) -> PrintRequestView:
    return PrintRequestView(id=item.id, state=item.state, detail=item.detail)


@router.post("/print-requests", response_model=PrintRequestView)
def create_print_request(command: CreatePrintRequest, request: Request):
    authenticate(request, "label.print")
    if command.copies != 1:
        raise HTTPException(422, "Printing accepts one physical label at a time.")
    with request.app.state.engine.connect() as connection:
        template = _load_template(connection, command.template_id)
        context = _label_context(connection, command.object_id, request.app.state.property_registry)
        try:
            profile = raster_media_profile(template)
            raster = render_template_brother_raster(template, context)
        except ValueError as error:
            raise HTTPException(422, str(error)) from error
    return _print_request_view(_print_mailbox(request).submit(raster, profile))


@router.get("/print-requests/{request_id}", response_model=PrintRequestView)
def get_print_request(request_id: UUID, request: Request):
    authenticate(request, "label.print")
    return _print_request_view(_print_mailbox(request).view(request_id))


@router.post("/printers/default/force-reset")
def force_reset_printer(request: Request):
    authenticate(request, "label.print")
    reset_request_id = _print_mailbox(request).force_reset()
    return {"status": "reset", "cleared_request_id": reset_request_id}


@router.post("/agent/print-requests/claim", response_model=PrintRequestClaim | None)
def claim_print_request(request: Request, response: Response):
    authenticate_service(request, "print.agent")
    item = _print_mailbox(request).claim()
    if item is None:
        response.status_code = 204
        return None
    return PrintRequestClaim(
        id=item.id,
        profile_id=item.profile.id,
        media_type="application/vnd.inventoryzing.brother-raster",
        width_mm=item.profile.width_mm,
        height_mm=item.profile.height_mm,
        media_kind=item.profile.media_kind,
        feed_margin_dots=item.profile.feed_margin_dots,
        raster_line_bytes=BROTHER_RASTER_LINE_BYTES,
        raster_lines=item.profile.raster_lines,
        raster_base64=b64encode(item.raster).decode("ascii"),
    )


@router.post("/agent/print-requests/{request_id}/report", response_model=PrintRequestView)
def report_print_request(
    request_id: UUID, report: PrintRequestReport, request: Request
):
    authenticate_service(request, "print.agent")
    return _print_request_view(_print_mailbox(request).report(request_id, report))


@legacy_router.post("/print-jobs", response_model=PrintJobView)
def create_print_job(command: CreatePrintJob, request: Request):
    session = authenticate(request, "label.print")
    with request.app.state.engine.begin() as connection:
        existing = connection.execute(
            text("""
                SELECT id, object_id, payload_kind, copies
                FROM iz_print.jobs WHERE command_id=:id
            """),
            {"id": command.command_id},
        ).mappings().first()
        if existing is not None:
            if (existing["object_id"], existing["payload_kind"], existing["copies"]) != (
                command.object_id, command.payload, command.copies
            ):
                raise HTTPException(409, "Print command was replayed with different data.")
            return _job_view(connection, existing["id"])
        connection.execute(
            text("SELECT pg_advisory_xact_lock(hashtextextended(:slot, 0))"),
            {"slot": DEFAULT_PRINTER_SLOT},
        )
        active = connection.execute(text("""
            SELECT job.id AS job_id, attempt.id AS attempt_id, attempt.state
            FROM iz_print.attempts attempt
            JOIN iz_print.jobs job ON job.id=attempt.job_id
            WHERE attempt.state NOT IN ('Completed', 'Rejected')
            ORDER BY attempt.created_at, attempt.id
            LIMIT 1
            FOR UPDATE OF attempt
        """)).mappings().first()
        if active is not None:
            raise HTTPException(
                409,
                {
                    "code": "printer_busy",
                    "message": "The printer is already handling a label.",
                    "active_job_id": str(active["job_id"]),
                    "active_attempt_id": str(active["attempt_id"]),
                    "active_state": active["state"],
                },
            )
        value = _print_value(connection, command.object_id, command.payload)
        content = render_tag_png(value)
        digest = hashlib.sha256(content).digest()
        artifact_id = connection.scalar(
            text("SELECT id FROM iz_print.artifacts WHERE sha256=:sha"), {"sha": digest}
        )
        if artifact_id is None:
            artifact_id = uuid4()
            connection.execute(text("""
                INSERT INTO iz_print.artifacts(id, sha256, media_type, width_mm, height_mm,
                    pixel_width, pixel_height, dpi_x, dpi_y, palette, content)
                VALUES (:id, :sha, 'image/png', :width, :height, :pixel_width, :pixel_height,
                    300, 300, 'Monochrome', :content)
            """), {"id": artifact_id, "sha": digest, "width": WIDTH_MM,
                    "height": HEIGHT_MM, "pixel_width": PIXEL_WIDTH,
                    "pixel_height": PIXEL_HEIGHT, "content": content})
        job_id, attempt_id, acknowledgement_id = uuid4(), uuid4(), uuid4()
        connection.execute(text("""
            INSERT INTO iz_print.jobs(id, command_id, object_id, requested_by, artifact_id,
                profile_id, copies, payload_kind, payload)
            VALUES (:id, :command, :object, :actor, :artifact, :profile, :copies,
                :payload_kind, :payload)
        """), {"id": job_id, "command": command.command_id, "object": command.object_id,
                "actor": session.account_id, "artifact": artifact_id, "profile": PROFILE_ID,
                "copies": command.copies, "payload_kind": command.payload, "payload": value})
        connection.execute(text("""
            INSERT INTO iz_print.attempts(id, job_id, dispatch_ack_id)
            VALUES (:id, :job, :ack)
        """), {"id": attempt_id, "job": job_id, "ack": acknowledgement_id})
        return _job_view(connection, job_id)


@legacy_router.get("/print-jobs/{job_id}", response_model=PrintJobView)
def get_print_job(job_id: UUID, request: Request):
    authenticate(request, "label.print")
    with request.app.state.engine.connect() as connection:
        return _job_view(connection, job_id)


@legacy_router.get("/objects/{object_id}/print-jobs/latest", response_model=PrintJobView | None)
def get_latest_print_job(object_id: UUID, request: Request):
    authenticate(request, "label.print")
    with request.app.state.engine.connect() as connection:
        job_id = connection.scalar(text("""
            SELECT id FROM iz_print.jobs
            WHERE object_id=:object
            ORDER BY created_at DESC, id DESC
            LIMIT 1
        """), {"object": object_id})
        return None if job_id is None else _job_view(connection, job_id)


@legacy_router.post("/agent/print-attempts/claim", response_model=PrintClaim | None)
def claim_print_attempt(claim: ClaimRequest, request: Request, response: Response):
    identity = authenticate_service(request, "print.agent")
    now = datetime.now(UTC)
    lease_id = uuid4()
    with request.app.state.engine.begin() as connection:
        row = connection.execute(text("""
            SELECT attempt.id FROM iz_print.attempts attempt
            WHERE attempt.state='Created'
               OR (attempt.state='Claimed' AND attempt.dispatch_started_at IS NULL
                   AND attempt.lease_expires_at <= now())
            ORDER BY attempt.created_at, attempt.id
            FOR UPDATE SKIP LOCKED LIMIT 1
        """)).mappings().first()
        if row is None:
            response.status_code = 204
            return None
        expires = now + timedelta(seconds=LEASE_SECONDS)
        connection.execute(text("""
            UPDATE iz_print.attempts SET state='Claimed', version=version+1,
                claimed_by=:account, lease_id=:lease, lease_expires_at=:expires, updated_at=:now
            WHERE id=:id
        """), {"account": identity.account_id, "lease": lease_id, "expires": expires,
                "now": now, "id": row["id"]})
        claimed = connection.execute(text("""
            SELECT attempt.id, job.artifact_id, encode(artifact.sha256, 'hex') AS sha256,
                   artifact.media_type, artifact.width_mm, artifact.height_mm,
                   artifact.pixel_width, artifact.pixel_height, artifact.dpi_x, artifact.dpi_y,
                   artifact.palette, job.profile_id, job.copies
            FROM iz_print.attempts attempt JOIN iz_print.jobs job ON job.id=attempt.job_id
            JOIN iz_print.artifacts artifact ON artifact.id=job.artifact_id
            WHERE attempt.id=:id
        """), {"id": row["id"]}).mappings().one()
    return PrintClaim(
        attempt_id=claimed["id"], lease_id=lease_id, lease_expires_at=expires,
        artifact_id=claimed["artifact_id"], artifact_sha256=claimed["sha256"],
        media_type=claimed["media_type"], width_mm=float(claimed["width_mm"]),
        height_mm=float(claimed["height_mm"]), pixel_width=claimed["pixel_width"],
        pixel_height=claimed["pixel_height"], dpi_x=claimed["dpi_x"], dpi_y=claimed["dpi_y"],
        palette=claimed["palette"], profile_id=claimed["profile_id"], copies=claimed["copies"],
        document_name=f"inventoryzing-{claimed['id'].hex}",
    )


@legacy_router.get("/agent/print-artifacts/{artifact_id}")
def get_print_artifact(artifact_id: UUID, request: Request):
    authenticate_service(request, "print.agent")
    with request.app.state.engine.connect() as connection:
        row = connection.execute(
            text("""
                SELECT content, media_type, encode(sha256, 'hex') sha
                FROM iz_print.artifacts WHERE id=:id
            """),
            {"id": artifact_id},
        ).mappings().first()
    if row is None:
        raise HTTPException(404, "Print artifact not found.")
    return Response(
        bytes(row["content"]),
        media_type=row["media_type"],
        headers={"ETag": row["sha"]},
    )


def _leased_attempt(connection, attempt_id: UUID, lease_id: UUID, account_id: UUID):
    row = connection.execute(text("""
        SELECT * FROM iz_print.attempts WHERE id=:attempt FOR UPDATE
    """), {"attempt": attempt_id}).mappings().first()
    if row is None:
        raise HTTPException(404, "Print attempt not found.")
    if row["lease_id"] != lease_id or row["claimed_by"] != account_id:
        raise HTTPException(409, "Print attempt lease does not match this agent.")
    return row


@legacy_router.post("/agent/print-attempts/{attempt_id}/renew")
def renew_print_attempt(attempt_id: UUID, lease: LeaseRequest, request: Request):
    identity = authenticate_service(request, "print.agent")
    now = datetime.now(UTC)
    expires = now + timedelta(seconds=LEASE_SECONDS)
    with request.app.state.engine.begin() as connection:
        row = _leased_attempt(connection, attempt_id, lease.lease_id, identity.account_id)
        if row["dispatch_started_at"] is not None:
            raise HTTPException(409, "A dispatch-started attempt no longer uses a claim lease.")
        connection.execute(text("""
            UPDATE iz_print.attempts SET lease_expires_at=:expires, updated_at=:now WHERE id=:id
        """), {"expires": expires, "now": now, "id": attempt_id})
    return {"lease_expires_at": expires}


@legacy_router.post(
    "/agent/print-attempts/{attempt_id}/dispatch-started",
    response_model=DispatchAuthorizationView,
)
def dispatch_started(attempt_id: UUID, lease: LeaseRequest, request: Request):
    identity = authenticate_service(request, "print.agent")
    now = datetime.now(UTC)
    with request.app.state.engine.begin() as connection:
        row = _leased_attempt(connection, attempt_id, lease.lease_id, identity.account_id)
        if row["dispatch_started_at"] is None:
            if row["state"] != "Claimed":
                raise HTTPException(409, "Print attempt is not eligible to begin dispatch.")
            if row["lease_expires_at"] <= now:
                raise HTTPException(409, "Print attempt lease expired before dispatch.")
            connection.execute(text("""
                UPDATE iz_print.attempts SET state='Dispatching', version=version+1,
                    dispatch_started_at=:now, updated_at=:now WHERE id=:id
            """), {"now": now, "id": attempt_id})
        return DispatchAuthorizationView(
            acknowledgement_id=row["dispatch_ack_id"], status="Granted",
            detail="Dispatch start is durably acknowledged.",
        )


@legacy_router.post("/agent/print-attempts/{attempt_id}/report", response_model=PrintAttemptView)
def report_print_attempt(attempt_id: UUID, report: AttemptReport, request: Request):
    identity = authenticate_service(request, "print.agent")
    if (report.state == "Completed") != (report.completion_evidence is not None):
        raise HTTPException(422, "Only Completed reports carry completion evidence.")
    with request.app.state.engine.begin() as connection:
        if report.lease_id is not None:
            row = _leased_attempt(connection, attempt_id, report.lease_id, identity.account_id)
        else:
            row = connection.execute(
                text("SELECT * FROM iz_print.attempts WHERE id=:attempt FOR UPDATE"),
                {"attempt": attempt_id},
            ).mappings().first()
            if row is None:
                raise HTTPException(404, "Print attempt not found.")
            if row["claimed_by"] != identity.account_id or row["dispatch_started_at"] is None:
                raise HTTPException(409, "Post-dispatch report does not match this agent.")
        for observation in report.observations:
            values = observation.model_dump()
            values.update({"attempt": attempt_id, "received": datetime.now(UTC)})
            existing = connection.execute(text("""
                SELECT source, code, detail, raw_job_status, raw_printer_status,
                       raw_provider_status, observed_at
                FROM iz_print.observations WHERE observation_id=:observation_id
            """), values).mappings().first()
            expected = {key: values[key] for key in (
                "source", "code", "detail", "raw_job_status", "raw_printer_status",
                "raw_provider_status", "observed_at")}
            if existing is not None and dict(existing) != expected:
                raise HTTPException(409, "Observation was replayed with different data.")
            if existing is None:
                connection.execute(text("""
                    INSERT INTO iz_print.observations(observation_id, attempt_id, source, code,
                        detail, raw_job_status, raw_printer_status, raw_provider_status,
                        observed_at, received_at)
                    VALUES (:observation_id, :attempt, :source, :code, :detail, :raw_job_status,
                        :raw_printer_status, :raw_provider_status, :observed_at, :received)
                """), values)
        if row["state"] != report.state or row["completion_evidence"] != report.completion_evidence:
            if row["state"] in TERMINAL_STATES:
                raise HTTPException(409, "Terminal print result cannot be changed.")
            if report.state not in REPORT_TRANSITIONS.get(row["state"], set()):
                raise HTTPException(
                    409, f"Print attempt cannot transition from {row['state']} to {report.state}."
                )
            connection.execute(text("""
                UPDATE iz_print.attempts SET state=:state, completion_evidence=:evidence,
                    version=version+1, updated_at=now() WHERE id=:id
            """), {"state": report.state, "evidence": report.completion_evidence, "id": attempt_id})
        current = connection.execute(text("""
            SELECT id, state, version, completion_evidence, updated_at
            FROM iz_print.attempts WHERE id=:id
        """), {"id": attempt_id}).mappings().one()
        observations = _observation_views(connection, attempt_id)
    return PrintAttemptView(**current, observations=observations)


def register(app) -> None:
    """Register the trusted, in-process labeling module with the coordinator host."""
    app.state.labeling_mailbox = PrintRequestMailbox()
    app.state.labeling_printer_status = PrinterStatusMailbox()
    app.include_router(router)
