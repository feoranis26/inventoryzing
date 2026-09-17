import base64
import struct
from datetime import UTC, datetime, timedelta
from io import BytesIO
from types import SimpleNamespace
from uuid import uuid4

import pytest
from fastapi import HTTPException
from fastapi.testclient import TestClient
from PIL import Image
from sqlalchemy import text

from inventoryzing.app import create_app
from inventoryzing.auth import create_printer_service, password_hasher
from inventoryzing.config import Settings
from inventoryzing.modules import labeling
from inventoryzing.modules.labeling import (
    BROTHER_LEFT_MARGIN,
    BROTHER_RASTER_HEIGHT,
    BROTHER_RASTER_LINE_BYTES,
    BROTHER_RASTER_WIDTH,
    PIXEL_HEIGHT,
    PIXEL_WIDTH,
    RASTER_MEDIA_PROFILES,
    LabelElement,
    LabelTemplateDefinition,
    LabelTemplateView,
    PrintRequestMailbox,
    PrintRequestReport,
    render_brother_raster,
    render_tag_png,
    render_template_brother_raster,
    render_template_png,
    render_template_svg,
    resolve_label_text,
)


@pytest.mark.parametrize("profile", RASTER_MEDIA_PROFILES)
def test_media_profile_render_and_claim_preserve_geometry(profile, monkeypatch):
    from PIL import Image

    template = LabelTemplateView(
        id=uuid4(), name="Media test", revision=1,
        width_mm=profile.width_mm, height_mm=profile.height_mm,
        media_kind="die_cut", is_default=False, updated_at=datetime.now(UTC),
        definition=LabelTemplateDefinition(elements=[]),
    )
    def bitmap(template, context, width, height):
        assert (width, height) == (round(template.width_mm * 300 / 25.4),
                                   round(template.height_mm * 300 / 25.4))
        image = Image.new("1", (width, height), 1)
        left, top, right, bottom = labeling.printable_bounds(template)
        image.putpixel((left, top), 0)
        image.putpixel((right - 1, bottom - 1), 0)
        image.putpixel((0, 0), 0)  # Outside the printable area: must be clipped.
        return image

    monkeypatch.setattr(labeling, "_render_template_bitmap", bitmap)
    raster = render_template_brother_raster(template, {})
    expected = bytearray(90 * profile.raster_lines)
    first_pin = 719 - profile.left_margin
    last_pin = first_pin - profile.printable_width + 1
    expected[first_pin // 8] = 0x80 >> (first_pin % 8)
    expected[(profile.raster_lines - 1) * 90 + last_pin // 8] = 0x80 >> (last_pin % 8)
    assert raster == expected

    mailbox = PrintRequestMailbox()
    mailbox.claim()
    mailbox.submit(raster, profile)
    request = SimpleNamespace(app=SimpleNamespace(state=SimpleNamespace(labeling_mailbox=mailbox)))
    monkeypatch.setattr(labeling, "authenticate_service", lambda *args: None)
    claim = labeling.claim_print_request(request, None)
    assert claim.profile_id == profile.id
    assert (claim.width_mm, claim.height_mm) == (profile.width_mm, profile.height_mm)
    assert claim.media_kind == "die_cut"
    assert claim.feed_margin_dots == 0
    assert claim.raster_lines == profile.raster_lines
    assert base64.b64decode(claim.raster_base64) == raster


@pytest.mark.parametrize("width,height,kind", [(63, 29, "die_cut"), (29, 62, "die_cut"),
                                              (29, 50, "continuous")])
def test_unsupported_media_is_not_silently_resized(width, height, kind):
    template = LabelTemplateView(
        id=uuid4(), name="Unsupported", revision=1, width_mm=width, height_mm=height,
        media_kind=kind, is_default=False, updated_at=datetime.now(UTC),
        definition=LabelTemplateDefinition(elements=[]),
    )
    with pytest.raises(ValueError, match="supported roll profile"):
        render_template_brother_raster(template, {})


def test_dk_1221_uses_verified_full_width_and_normal_feed_length():
    template = LabelTemplateView(
        id=uuid4(), name="DK-1221", revision=1, width_mm=23, height_mm=23,
        media_kind="die_cut", is_default=False, updated_at=datetime.now(UTC),
        definition=LabelTemplateDefinition(elements=[]),
    )
    profile = labeling.raster_media_profile(template)
    assert profile.id == "brother-ql820nwb-23x23-mono-300"
    assert (profile.printable_width, profile.raster_lines, profile.left_margin) == (272, 202, 424)
    assert labeling.printable_bounds(template) == (0, 35, 272, 237)


def test_print_keeps_physical_size_and_matches_png_crop():
    template = LabelTemplateView(
        id=uuid4(), name="Physical size", revision=1, width_mm=23, height_mm=23,
        media_kind="die_cut", is_default=False, updated_at=datetime.now(UTC),
        definition=LabelTemplateDefinition(elements=[LabelElement(
            id="block", kind="site_branding", x_mm=5, y_mm=5,
            width_mm=10, height_mm=10,
        )]),
    )
    logo = BytesIO()
    Image.new("RGB", (200, 200), "black").save(logo, format="PNG")
    context = {"site_logo_png": logo.getvalue()}
    preview = Image.open(BytesIO(render_template_png(template, context)))
    raster = render_template_brother_raster(template, context)
    profile = labeling.raster_media_profile(template)
    left, top, _, _ = labeling.printable_bounds(template)
    dark_x = set()
    for y in range(profile.raster_lines):
        for x in range(profile.printable_width):
            pin = 719 - profile.left_margin - x
            dark = bool(raster[y * 90 + pin // 8] & (0x80 >> (pin % 8)))
            assert dark == (preview.getpixel((left + x, top + y)) == 0)
            if dark:
                dark_x.add(x)
    assert len(dark_x) == round(10 * 300 / 25.4)


def test_rotated_dk_1221_rotates_the_design_but_keeps_cutter_safe_geometry():
    element = LabelElement(
        id="block", kind="site_branding", x_mm=4, y_mm=4, width_mm=5, height_mm=9,
    )
    normal = LabelTemplateView(
        id=uuid4(), name="Normal", revision=1, width_mm=23, height_mm=23,
        media_kind="die_cut", is_default=False, updated_at=datetime.now(UTC),
        definition=LabelTemplateDefinition(elements=[element]),
    )
    rotated = normal.model_copy(update={
        "definition": LabelTemplateDefinition(elements=[element], orientation="rotated")
    })
    logo = BytesIO()
    Image.new("RGB", (200, 200), "black").save(logo, format="PNG")
    context = {"site_logo_png": logo.getvalue()}
    assert labeling.raster_media_profile(rotated) == labeling.raster_media_profile(normal)
    assert labeling.oriented_printable_bounds(rotated) == (35, 0, 237, 272)
    svg = render_template_svg(rotated, context).decode()
    assert 'rotate(90 11.5 11.5)' in svg
    normal_image = labeling._render_template_bitmap(normal, context, 272, 272)
    rotated_image = labeling._render_template_bitmap(rotated, context, 272, 272)
    assert rotated_image.tobytes() == normal_image.rotate(90).tobytes()


@pytest.mark.parametrize("width,height,printable,left", [
    (62, 50, 720, 0),
    (12, 25.4, 142, 549),
])
def test_continuous_media_uses_template_cut_length(width, height, printable, left, monkeypatch):
    from PIL import Image

    template = LabelTemplateView(
        id=uuid4(), name="Continuous", revision=1, width_mm=width, height_mm=height,
        media_kind="continuous", is_default=False, updated_at=datetime.now(UTC),
        definition=LabelTemplateDefinition(elements=[]),
    )
    expected_lines = round(height * 300 / 25.4) - 70
    monkeypatch.setattr(labeling, "_render_template_bitmap",
                        lambda template, context, image_width, image_height:
                        Image.new("1", (image_width, image_height), 1))
    profile = labeling.raster_media_profile(template)
    raster = render_template_brother_raster(template, {})
    assert profile.media_kind == "continuous"
    assert profile.media_length_mm == 0
    assert profile.feed_margin_dots == 35
    assert (profile.printable_width, profile.left_margin) == (printable, left)
    assert profile.raster_lines == expected_lines
    assert len(raster) == 90 * expected_lines


@pytest.mark.parametrize("height", [12.6, 1000.1])
def test_continuous_media_rejects_invalid_cut_length(height):
    template = LabelTemplateView(
        id=uuid4(), name="Continuous", revision=1, width_mm=62, height_mm=height,
        media_kind="continuous", is_default=False, updated_at=datetime.now(UTC),
        definition=LabelTemplateDefinition(elements=[]),
    )
    with pytest.raises(ValueError, match="between 12.7 and 1000"):
        render_template_brother_raster(template, {})


def test_fixed_profile_renderer_is_deterministic_and_exact_size():
    first = render_tag_png("I000123")
    second = render_tag_png("I000123")

    assert first == second
    assert first.startswith(b"\x89PNG\r\n\x1a\n")
    width, height = struct.unpack(">II", first[16:24])
    assert (width, height) == (PIXEL_WIDTH, PIXEL_HEIGHT)


def test_brother_raster_renderer_is_deterministic_and_respects_hardware_padding():
    first = render_brother_raster("I000123")
    second = render_brother_raster("I000123")

    assert first == second
    assert len(first) == BROTHER_RASTER_LINE_BYTES * BROTHER_RASTER_HEIGHT
    assert any(first)
    # The QL raster stream maps head pins right-to-left. Visual content must
    # remain within the documented 29 mm printable band after that reversal.
    assert any(first)
    for row in range(BROTHER_RASTER_HEIGHT):
        line = first[row * BROTHER_RASTER_LINE_BYTES : (row + 1) * BROTHER_RASTER_LINE_BYTES]
        for x in range(BROTHER_RASTER_WIDTH):
            if line[x // 8] & (0x80 >> (x % 8)):
                visual_x = BROTHER_RASTER_WIDTH - 1 - x - BROTHER_LEFT_MARGIN
                assert 0 <= visual_x < 306


def test_template_placeholders_support_fields_and_bounded_slices():
    context = {
        "name": "Bench meter", "uuid": "01234567-89ab-cdef-0123-456789abcdef",
        "local_id": "I123456", "description": "Calibrated", "type_name": "Meter",
        "site_name": "Workshop",
    }

    assert resolve_label_text(
        "{object.name} · {object.uuid[-8:]}", context
    ) == "Bench meter · 89abcdef"
    with pytest.raises(ValueError, match="Unknown label placeholder"):
        resolve_label_text("{object.__class__}", context)


def test_missing_module_fields_and_qr_do_not_break_label_rendering():
    template = LabelTemplateView(
        id=uuid4(), name="Module label", revision=1, width_mm=29, height_mm=90,
        media_kind="die_cut", is_default=False, updated_at=datetime.now(UTC),
        definition=LabelTemplateDefinition(elements=[
            LabelElement(id="file", kind="field", x_mm=1, y_mm=1, width_mm=25,
                         height_mm=5, content="{manufacturing.gcode_filename}", show_label=True),
            LabelElement(id="qr", kind="qr", x_mm=1, y_mm=10, width_mm=25,
                         height_mm=25, content="{manufacturing.gcode_filename}"),
            LabelElement(id="name", kind="field", x_mm=1, y_mm=40, width_mm=25,
                         height_mm=5, content="{object.name}"),
        ]),
    )
    context = {"object.name": "Box {A}"}
    assert b"Box {A}" in render_template_svg(template, context)
    assert render_template_png(template, context).startswith(b"\x89PNG")
    raster = render_template_brother_raster(template, context)
    assert len(raster) == BROTHER_RASTER_LINE_BYTES * BROTHER_RASTER_HEIGHT
    assert any(raster)


def test_template_renderer_resolves_live_branding_and_prints_fixed_profile():
    template = LabelTemplateView(
        id=uuid4(), name="Test", revision=1, width_mm=29, height_mm=90,
        media_kind="die_cut", is_default=True, updated_at=datetime.now(UTC),
        definition=LabelTemplateDefinition(elements=[
            LabelElement(id="qr", kind="qr", x_mm=2, y_mm=2, width_mm=25,
                         height_mm=25, content="{object.uuid}"),
            LabelElement(id="site", kind="site_branding", x_mm=2, y_mm=30,
                         width_mm=25, height_mm=5),
            LabelElement(id="product", kind="inventoryzing_branding", x_mm=2, y_mm=38,
                         width_mm=25, height_mm=5),
        ]),
    )
    context = {
        "name": "Bench meter", "uuid": str(uuid4()), "local_id": "I123456",
        "description": "", "type_name": "Meter", "site_name": "Current workshop",
    }

    assert b"Current workshop" in render_template_svg(template, context)
    assert b"data:image/svg+xml;base64" in render_template_svg(template, context)
    raster = render_template_brother_raster(template, context)
    assert len(raster) == BROTHER_RASTER_LINE_BYTES * BROTHER_RASTER_HEIGHT
    assert any(raster)


def test_template_renderer_prefers_live_site_logo_when_present():
    logo = BytesIO()
    Image.new("RGBA", (8, 4), "black").save(logo, format="PNG")
    template = LabelTemplateView(
        id=uuid4(), name="Brand", revision=1, width_mm=29, height_mm=90,
        media_kind="die_cut", is_default=False, updated_at=datetime.now(UTC),
        definition=LabelTemplateDefinition(elements=[
            LabelElement(id="site", kind="site_branding", x_mm=2, y_mm=2, width_mm=25, height_mm=8),
        ]),
    )
    context = {"site_name": "Fallback workshop", "site_logo_png": logo.getvalue()}
    svg = render_template_svg(template, context)
    assert b"data:image/png;base64" in svg
    assert b"Fallback workshop" not in svg
    assert any(render_template_brother_raster(template, context))


def test_template_renderer_wraps_autofits_and_ellipsizes_text():
    template = LabelTemplateView(
        id=uuid4(), name="Text layout", revision=1, width_mm=29, height_mm=90,
        media_kind="die_cut", is_default=False, updated_at=datetime.now(UTC),
        definition=LabelTemplateDefinition(elements=[
            LabelElement(
                id="wrapped", kind="field", x_mm=2, y_mm=2, width_mm=12,
                height_mm=5, content="{object.description}", font_size_mm=3,
                wrap_text=True, vertical_align="top",
            ),
            LabelElement(
                id="fitted", kind="field", x_mm=2, y_mm=10, width_mm=12,
                height_mm=5, content="{object.name}", font_size_mm=4,
                auto_fit=True, min_font_size_mm=1.5, vertical_align="bottom",
            ),
        ]),
    )
    context = {
        "name": "An exceptionally long inventory object name",
        "uuid": str(uuid4()), "local_id": "I123456",
        "description": "A long description that needs several lines and cannot all be shown",
        "type_name": "Meter", "site_name": "Current workshop",
    }

    svg = render_template_svg(template, context)
    assert "…".encode() in svg
    assert svg.count(b'<text ') >= 2
    raster = render_template_brother_raster(template, context)
    assert len(raster) == BROTHER_RASTER_LINE_BYTES * BROTHER_RASTER_HEIGHT


def test_print_request_mailbox_has_one_active_request_and_forgets_after_reset():
    mailbox = PrintRequestMailbox()
    assert mailbox.claim() is None
    item = mailbox.submit(b"raster")

    with pytest.raises(HTTPException) as busy:
        mailbox.submit(b"another-raster")
    assert busy.value.status_code == 409

    assert mailbox.claim() is item
    assert mailbox.force_reset() == item.id

    with pytest.raises(HTTPException) as missing:
        mailbox.report(item.id, PrintRequestReport(state="Completed"))
    assert missing.value.status_code == 404
    assert mailbox.submit(b"fresh-raster").state == "Queued"


def test_print_request_mailbox_discards_raster_when_the_agent_finishes():
    mailbox = PrintRequestMailbox()
    assert mailbox.claim() is None
    item = mailbox.submit(b"one-label-only")
    assert mailbox.claim() is item

    finished = mailbox.report(item.id, PrintRequestReport(state="Completed", detail="done"))

    assert finished.raster == b""
    assert mailbox.view(item.id).detail == "done"


def test_print_request_mailbox_rejects_new_work_without_a_live_agent():
    with pytest.raises(HTTPException) as unavailable:
        PrintRequestMailbox().submit(b"raster")

    assert unavailable.value.status_code == 503
    assert unavailable.value.detail["code"] == "printer_unavailable"


def test_missing_report_is_unknown_and_keeps_printer_busy_until_late_report():
    mailbox = PrintRequestMailbox()
    mailbox.claim()
    item = mailbox.submit(b"raster")
    mailbox.claim()
    item.expires_at = datetime.now(UTC) - timedelta(seconds=1)
    assert mailbox.view(item.id).state == "Unknown"
    with pytest.raises(HTTPException) as busy:
        mailbox.submit(b"second label")
    assert busy.value.status_code == 409
    assert mailbox.report(item.id, PrintRequestReport(state="Rejected")).state == "Rejected"


def test_unclaimed_request_expires_and_is_never_delivered_later():
    mailbox = PrintRequestMailbox()
    mailbox.claim()
    item = mailbox.submit(b"raster")
    item.expires_at = datetime.now(UTC) - timedelta(seconds=1)
    assert mailbox.claim() is None
    assert mailbox.view(item.id).state == "Rejected"


def test_durable_print_routes_are_not_part_of_the_active_api():
    paths = create_app().openapi()["paths"]

    assert "/api/print-requests" in paths
    assert "/api/print-jobs" not in paths
    assert "/api/agent/print-artifacts/{artifact_id}" not in paths


@pytest.mark.integration
def test_print_request_claim_and_completion_never_create_a_durable_job(database):
    engine, _site, actor = database
    with engine.begin() as connection:
        connection.execute(
            text("UPDATE iz.accounts SET password_hash=:hash WHERE id=:id"),
            {"id": actor, "hash": password_hasher.hash("test-password-only")},
        )
        role = uuid4()
        connection.execute(
            text("INSERT INTO iz.roles(id, name) VALUES (:id, 'Print test admin')"),
            {"id": role},
        )
        connection.execute(
            text("INSERT INTO iz.role_permissions SELECT :role, code FROM iz.permissions"),
            {"role": role},
        )
        connection.execute(
            text("INSERT INTO iz.account_roles(account_id, role_id) VALUES (:account, :role)"),
            {"account": actor, "role": role},
        )
    _service_account, token = create_printer_service(engine, "test-agent")
    app = create_app(
        Settings(
            database_url=engine.url.render_as_string(hide_password=False),
            secure_cookies=False,
            public_origin="http://testserver",
        )
    )
    with TestClient(app) as client:
        client.headers["origin"] = "http://testserver"
        login = client.post(
            "/api/auth/login", json={"login": "test", "password": "test-password-only"}
        )
        assert login.status_code == 200, login.text
        client.headers["x-csrf-token"] = login.json()["csrf_token"]
        session = login.json()
        template = client.post("/api/label-templates", json={
            "name": "Print request test",
            "width_mm": 29,
            "height_mm": 90,
            "media_kind": "die_cut",
            "definition": {"elements": []},
            "is_default": True,
        })
        assert template.status_code == 200, template.text
        created = client.post("/api/commands", json={
            "authority_site": session["site_id"], "authority_epoch": 1,
            "command_epoch": session["command_epoch"], "command_id": str(uuid4()),
            "payload": {"kind": "object.create", "name": "Tagged meter"},
        })
        assert created.status_code == 200, created.text
        preview = client.get(
            f"/api/objects/{created.json()['entity_id']}/tag.png?payload=local"
        )
        assert preview.status_code == 200, preview.text
        assert preview.headers["content-type"] == "image/png"
        assert struct.unpack(">II", preview.content[16:24]) == (PIXEL_WIDTH, PIXEL_HEIGHT)
        client.headers.pop("x-csrf-token")
        client.headers.pop("origin")
        client.headers["authorization"] = f"Bearer {token}"
        assert client.post("/api/agent/print-requests/claim").status_code == 204
        client.headers.pop("authorization")
        client.headers["origin"] = "http://testserver"
        client.headers["x-csrf-token"] = login.json()["csrf_token"]
        job = client.post("/api/print-requests", json={
            "object_id": created.json()["entity_id"],
        })
        assert job.status_code == 200, job.text
        blocked = client.post("/api/print-requests", json={
            "object_id": created.json()["entity_id"],
        })
        assert blocked.status_code == 409, blocked.text
        assert blocked.json()["detail"]["code"] == "printer_busy"

        client.headers.pop("x-csrf-token")
        client.headers.pop("origin")
        client.headers["authorization"] = f"Bearer {token}"
        claim = client.post("/api/agent/print-requests/claim")
        assert claim.status_code == 200, claim.text
        claimed = claim.json()
        assert len(base64.b64decode(claimed["raster_base64"])) == (
            BROTHER_RASTER_LINE_BYTES * BROTHER_RASTER_HEIGHT
        )
        report = {"state": "Completed", "detail": "printer completed"}
        completed = client.post(
            f"/api/agent/print-requests/{claimed['id']}/report", json=report
        )
        assert completed.status_code == 200, completed.text
        assert completed.json()["state"] == "Completed"
        client.headers.pop("authorization")
        client.headers["origin"] = "http://testserver"
        client.headers["x-csrf-token"] = login.json()["csrf_token"]
        result = client.get(f"/api/print-requests/{claimed['id']}")
        assert result.json()["state"] == "Completed"
        with engine.connect() as connection:
            durable_count = connection.execute(
                text("SELECT count(*) FROM iz_print.jobs WHERE object_id=:id"),
                {"id": created.json()["entity_id"]},
            ).scalar_one()
        assert durable_count == 0
