import base64
from datetime import UTC, datetime
from io import BytesIO
from uuid import uuid4

import pytest
from fastapi.testclient import TestClient
from PIL import Image
from sqlalchemy import event, text

from inventoryzing.app import create_app
from inventoryzing.auth import bootstrap, create_scanner_service, password_hasher
from inventoryzing.config import Settings

pytestmark = pytest.mark.integration


def test_unit_catalog_and_quantity_definition_dimensions(client):
    session = sign_in(client)
    catalog = client.get("/api/property-units").json()
    assert {item["id"] for item in catalog} == {"count", "mass", "length", "area", "volume"}
    for dimension in catalog:
        response = client.post(
            "/api/commands",
            json={
                "authority_site": session["site_id"],
                "authority_epoch": 1,
                "command_epoch": session["command_epoch"],
                "command_id": str(uuid4()),
                "payload": {
                    "kind": "property.definition.create",
                    "key": f"test.{dimension['id']}",
                    "label": dimension["label"],
                    "value_type": "quantity",
                    "quantity_dimension": dimension["id"],
                    "allowed_units": [unit["id"] for unit in dimension["units"]],
                },
            },
        )
        assert response.status_code == 200, response.text
    definitions = client.get("/api/properties").json()
    for dimension in catalog:
        stored = next(item for item in definitions if item["key"] == f"test.{dimension['id']}")
        assert stored["canonical_unit"] == dimension["canonical_unit"]


@pytest.fixture
def client(database):
    engine, _site, actor = database
    with engine.begin() as connection:
        connection.execute(
            text("UPDATE iz.accounts SET password_hash = :hash WHERE id = :id"),
            {"id": actor, "hash": password_hasher.hash("test-password-only")},
        )
        role = connection.scalar(text("SELECT id FROM iz.roles WHERE name = 'Test admin'"))
        if role is None:
            role = uuid4()
            connection.execute(
                text("INSERT INTO iz.roles VALUES (:id, 'Test admin')"), {"id": role}
            )
            connection.execute(
                text("INSERT INTO iz.role_permissions SELECT :role, code FROM iz.permissions"),
                {"role": role},
            )
        connection.execute(
            text("INSERT INTO iz.account_roles VALUES (:actor, :role)"),
            {"actor": actor, "role": role},
        )
    app = create_app(
        Settings(
            database_url=engine.url.render_as_string(hide_password=False),
            secure_cookies=False,
            public_origin="http://testserver",
        )
    )
    with TestClient(app) as client:
        client.headers["origin"] = "http://testserver"
        yield client


def sign_in(client):
    response = client.post(
        "/api/auth/login", json={"login": "TEST", "password": "test-password-only"}
    )
    assert response.status_code == 200, response.text
    assert "HttpOnly" in response.headers["set-cookie"]
    session = response.json()
    client.headers["x-csrf-token"] = session["csrf_token"]
    return session


def test_site_settings_logo_is_authorized_versioned_and_audited(client):
    sign_in(client)
    settings = client.get("/api/site/settings").json()
    assert settings["display_name"] == "Test workshop"
    assert settings["has_logo"] is False
    renamed = client.put("/api/site/settings", json={
        "display_name": "Machine shop", "expected_version": settings["settings_version"],
    })
    assert renamed.status_code == 200, renamed.text
    assert renamed.json()["display_name"] == "Machine shop"
    stale = client.put("/api/site/settings", json={
        "display_name": "Wrong", "expected_version": settings["settings_version"],
    })
    assert stale.status_code == 409
    raw = BytesIO()
    Image.new("RGBA", (20, 10), (0, 0, 0, 0)).save(raw, format="PNG")
    updated = client.put("/api/site/logo", json={
        "logo_base64": base64.b64encode(raw.getvalue()).decode(),
        "expected_version": renamed.json()["settings_version"],
    })
    assert updated.status_code == 200, updated.text
    assert updated.json()["has_logo"] is True
    logo = client.get("/api/site/logo")
    assert logo.status_code == 200
    assert logo.headers["content-type"] == "image/png"
    assert logo.content.startswith(b"\x89PNG\r\n\x1a\n")
    removed = client.delete(f"/api/site/logo?expected_version={updated.json()['settings_version']}")
    assert removed.status_code == 200
    assert removed.json()["has_logo"] is False
    assert client.get("/api/site/logo").status_code == 404


def test_site_settings_require_explicit_permission(client):
    sign_in(client)
    with client.app.state.engine.begin() as connection:
        connection.execute(
            text("DELETE FROM iz.role_permissions WHERE permission LIKE 'system.config.%'")
        )
    assert client.get("/api/site/settings").status_code == 403
    assert client.put("/api/site/settings", json={
        "display_name": "No", "expected_version": 1,
    }).status_code == 403


def test_runtime_database_role_can_change_site_settings(client):
    engine = client.app.state.engine
    with engine.connect() as connection:
        if not connection.scalar(
            text("SELECT 1 FROM pg_roles WHERE rolname = 'inventoryzing_app'")
        ):
            pytest.skip("Run inventoryzing.migrate to provision the runtime role")

    def restrict(connection):
        connection.execute(text("SET LOCAL ROLE inventoryzing_app"))

    event.listen(engine, "begin", restrict)
    try:
        sign_in(client)
        settings = client.get("/api/site/settings").json()
        response = client.put("/api/site/settings", json={
            "display_name": "Runtime role workshop",
            "expected_version": settings["settings_version"],
        })
        assert response.status_code == 200, response.text
    finally:
        event.remove(engine, "begin", restrict)


def test_account_and_role_administration_uses_atomic_permissions_and_revokes_sessions(client):
    sign_in(client)
    permissions = client.get("/api/admin/permissions")
    assert permissions.status_code == 200
    assert {"role.manage", "role.assign", "user.manage"} <= set(permissions.json())
    role = client.post("/api/admin/roles", json={
        "name": "Viewer", "description": "Read only", "permissions": ["inventory.read"],
    })
    assert role.status_code == 200, role.text
    assert role.json()["permissions"] == ["inventory.read"]
    account = client.post("/api/admin/accounts", json={
        "login": "worker", "password": "worker-password-only", "role_ids": [role.json()["id"]],
    })
    assert account.status_code == 200, account.text
    assert account.json()["login"] == "worker"
    assert account.json()["permissions"] == ["inventory.read"]
    logged_in = client.post("/api/auth/login", json={
        "login": "worker", "password": "worker-password-only",
    })
    assert logged_in.status_code == 200
    sign_in(client)
    worker = account.json()
    disabled = client.put(f"/api/admin/accounts/{worker['id']}", json={
        "principal_id": None, "role_ids": [role.json()["id"]], "disabled": True,
        "expected_version": worker["version"],
    })
    assert disabled.status_code == 200, disabled.text
    failed_login = client.post("/api/auth/login", json={
        "login": "worker", "password": "worker-password-only",
    })
    assert failed_login.status_code == 401
    sign_in(client)
    accounts = client.get("/api/admin/accounts")
    assert next(item for item in accounts.json() if item["id"] == worker["id"])["disabled"] is True


def test_administration_cannot_disable_last_usable_administrator(client):
    session = sign_in(client)
    response = client.put(f"/api/admin/accounts/{session['account_id']}", json={
        "principal_id": None, "role_ids": [], "disabled": True, "expected_version": 1,
    })
    assert response.status_code == 409
    assert client.get("/api/admin/roles").status_code == 200


def test_principals_are_distinct_from_accounts_and_archive_after_unlinking(client):
    sign_in(client)
    team = client.post("/api/principals", json={
        "display_name": "Electronics team", "principal_kind": "team",
    })
    person = client.post("/api/principals", json={
        "display_name": "Ari", "principal_kind": "person",
    })
    assert team.status_code == 200 and person.status_code == 200
    principal_names = {entry["display_name"] for entry in client.get("/api/principals").json()}
    assert principal_names >= {"Electronics team", "Ari"}
    account = client.post("/api/admin/accounts", json={
        "login": "ari", "password": "ari-password-only", "principal_id": person.json()["id"],
    })
    assert account.status_code == 200, account.text
    bad_kind = client.put(f"/api/principals/{person.json()['id']}", json={
        "display_name": "Ari team", "principal_kind": "team",
        "expected_version": person.json()["version"],
    })
    assert bad_kind.status_code == 422
    person_id = person.json()["id"]
    person_version = person.json()["version"]
    archive_url = f"/api/principals/{person_id}/archive?expected_version={person_version}"
    blocked = client.post(archive_url)
    assert blocked.status_code == 409
    linked = account.json()
    unlinked = client.put(f"/api/admin/accounts/{linked['id']}", json={
        "principal_id": None, "role_ids": [], "disabled": False,
        "expected_version": linked["version"],
    })
    assert unlinked.status_code == 200, unlinked.text
    archived = client.post(archive_url)
    assert archived.status_code == 200, archived.text
    assert archived.json()["archived"] is True


def test_authenticated_create_scan_move_history_and_label(client):
    assert client.get("/api/objects").status_code == 401
    session = sign_in(client)

    def submit(payload):
        response = client.post(
            "/api/commands",
            json={
                "authority_site": session["site_id"],
                "authority_epoch": 1,
                "command_epoch": session["command_epoch"],
                "command_id": str(uuid4()),
                "payload": payload,
            },
        )
        assert response.status_code == 200, response.text
        return response.json()

    shelf = submit({"kind": "object.create", "name": "Shelf"})
    item = submit({"kind": "object.create", "name": "Meter", "description": "Calibrated"})
    resolved = client.get("/api/resolve", params={"value": item["alias"]})
    assert resolved.status_code == 200
    assert resolved.json()["id"] == item["entity_id"]
    submit(
        {
            "kind": "object.move",
            "object_id": item["entity_id"],
            "expected_version": 1,
            "parent_id": shelf["entity_id"],
        }
    )
    assert client.get(f"/api/objects/{item['entity_id']}").json()["parent_name"] == "Shelf"
    assert (
        client.get(f"/api/objects/{item['entity_id']}/ancestors").json()[0]["id"]
        == shelf["entity_id"]
    )
    assert client.get("/api/objects", params={"query": item["alias"]}).json()["total"] == 1
    assert client.get("/api/objects", params={"roots": True}).json()["total"] == 1
    history = client.get(f"/api/objects/{item['entity_id']}/history").json()
    assert [entry["event_type"] for entry in history] == ["object.move", "object.create"]
    image = client.get(f"/api/objects/{item['entity_id']}/label.svg")
    assert image.status_code == 200
    assert "<svg" in image.text and item["alias"] in image.text
    assert client.post("/api/auth/logout").status_code == 204
    assert client.get("/api/auth/session").status_code == 401


def test_object_type_change_preserves_identity_and_updates_inheritance(client, database):
    session = sign_in(client)

    def submit(payload, expected=200, command_id=None):
        response = client.post(
            "/api/commands",
            json={
                "authority_site": session["site_id"],
                "authority_epoch": 1,
                "command_epoch": session["command_epoch"],
                "command_id": command_id or str(uuid4()),
                "payload": payload,
            },
        )
        assert response.status_code == expected, response.text
        return response.json()

    original_tag = submit({"kind": "tag.create", "name": "Original classification"})["entity_id"]
    corrected_tag = submit({"kind": "tag.create", "name": "Corrected classification"})["entity_id"]
    direct_tag = submit({"kind": "tag.create", "name": "Needs repair"})["entity_id"]
    original_type = submit(
        {"kind": "type.create", "name": "Original type", "tag_ids": [original_tag]}
    )["entity_id"]
    corrected_type = submit(
        {"kind": "type.create", "name": "Corrected type", "tag_ids": [corrected_tag]}
    )["entity_id"]
    shelf = submit({"kind": "object.create", "name": "Shelf"})["entity_id"]
    item = submit(
        {
            "kind": "object.create",
            "name": "Meter",
            "description": "Keep this",
            "object_type_id": original_type,
            "parent_id": shelf,
            "tag_ids": [direct_tag],
        }
    )
    sibling = submit(
        {"kind": "object.create", "name": "Other meter", "object_type_id": original_type}
    )["entity_id"]
    item_url = f"/api/objects/{item['entity_id']}"
    before = client.get(item_url).json()
    label_before = client.get(f"{item_url}/label.svg").content
    change = {
        "kind": "object.type.set",
        "object_id": item["entity_id"],
        "expected_version": 1,
        "object_type_id": corrected_type,
    }
    change_id = str(uuid4())
    result = submit(change, command_id=change_id)
    assert result["entity_id"] == item["entity_id"]
    assert result["version"] == 2
    after = client.get(item_url).json()
    for field in ("id", "name", "description", "alias", "parent_id", "relation", "location_path"):
        assert after[field] == before[field]
    assert after["object_type_id"] == corrected_type
    assert after["type_name"] == "Corrected type"
    assert client.get(f"{item_url}/label.svg").content == label_before
    assert client.get("/api/resolve", params={"value": item["alias"]}).json()["id"] == before["id"]
    classification = client.get(f"{item_url}/tags").json()
    assert classification["explicit_tag_ids"] == [direct_tag]
    assert {tag["id"] for tag in classification["effective_tags"]} == {direct_tag, corrected_tag}
    assert client.get("/api/objects", params={"type_id": corrected_type}).json()["total"] == 1
    assert client.get("/api/objects", params={"tag_id": corrected_tag}).json()["total"] == 1
    original_matches = client.get("/api/objects", params={"tag_id": original_tag}).json()
    assert [entry["id"] for entry in original_matches["items"]] == [sibling]
    assert client.get(f"/api/objects/{sibling}").json()["version"] == 1
    assert all(entry["version"] == 1 for entry in client.get("/api/types").json())
    assert submit(change, expected=409)["code"] == "VERSION_CONFLICT"
    for invalid_type in (str(uuid4()), shelf, item["entity_id"]):
        rejected = submit(
            {**change, "expected_version": 2, "object_type_id": invalid_type}, expected=404
        )
        assert rejected["code"] == "NOT_FOUND"
    assert client.get(item_url).json()["version"] == 2

    removed = submit({**change, "expected_version": 2, "object_type_id": None})
    assert removed["version"] == 3
    assert client.get(item_url).json()["object_type_id"] is None
    assert client.get(item_url).json()["type_name"] is None
    remaining_tags = client.get(f"{item_url}/tags").json()["effective_tags"]
    assert {tag["id"] for tag in remaining_tags} == {direct_tag}
    assert client.get("/api/objects", params={"tag_id": corrected_tag}).json()["total"] == 0
    assert submit(change, command_id=change_id) == result
    assert client.get(item_url).json()["object_type_id"] is None
    assert client.get(item_url).json()["version"] == 3
    reassigned = submit({**change, "expected_version": 3, "object_type_id": original_type})
    assert reassigned["version"] == 4
    assert client.get(item_url).json()["alias"] == before["alias"]
    history = client.get(f"{item_url}/history").json()
    assert [entry["event_type"] for entry in history] == [
        "object.type.set",
        "object.type.set",
        "object.type.set",
        "object.create",
    ]
    assert history[2]["payload"]["object_type_id"] == corrected_type
    engine, _site, _actor = database
    with engine.connect() as connection:
        assert (
            connection.scalar(
                text("""
            SELECT count(*) FROM iz.command_receipts receipt
            JOIN iz.domain_events event ON event.command_id = receipt.command_id
            JOIN iz.event_subjects subject ON subject.event_id = event.id
            JOIN iz.replication_outbox outbox ON outbox.event_id = event.id
            WHERE receipt.command_id = :command AND subject.entity_id = :entity
              AND subject.version = 2
        """),
                {"command": change_id, "entity": item["entity_id"]},
            )
            == 1
        )


@pytest.mark.parametrize(
    "invalid_case, expected_status",
    [
        ("archived_type", 404),
        ("foreign_type", 409),
        ("archived_object", 404),
        ("missing_object", 404),
        ("wrong_kind_object", 404),
        ("identical_type_ids", 404),
        ("no_edit_permission", 403),
    ],
)
def test_object_type_change_rejects_invalid_references_and_permissions(
    client, database, invalid_case, expected_status
):
    session = sign_in(client)

    def envelope(payload):
        return {
            "authority_site": session["site_id"],
            "authority_epoch": 1,
            "command_epoch": session["command_epoch"],
            "command_id": str(uuid4()),
            "payload": payload,
        }

    item_response = client.post(
        "/api/commands", json=envelope({"kind": "object.create", "name": "Meter"})
    )
    type_response = client.post(
        "/api/commands", json=envelope({"kind": "type.create", "name": "Instrument"})
    )
    assert item_response.status_code == type_response.status_code == 200
    item_id = item_response.json()["entity_id"]
    type_id = type_response.json()["entity_id"]
    payload = {
        "kind": "object.type.set",
        "object_id": item_id,
        "expected_version": 1,
        "object_type_id": type_id,
    }
    engine, _site, _actor = database
    with engine.begin() as connection:
        if invalid_case in {"archived_type", "archived_object"}:
            connection.execute(
                text("UPDATE iz.entities SET archived_at = now() WHERE id = :id"),
                {"id": type_id if invalid_case == "archived_type" else item_id},
            )
        elif invalid_case == "foreign_type":
            other_site = uuid4()
            connection.execute(
                text("INSERT INTO iz.sites(id, display_name) VALUES (:id, 'Other')"),
                {"id": other_site},
            )
            connection.execute(
                text("UPDATE iz.entities SET write_site_id = :site WHERE id = :id"),
                {"id": type_id, "site": other_site},
            )
        elif invalid_case == "missing_object":
            payload["object_id"] = str(uuid4())
        elif invalid_case == "wrong_kind_object":
            payload.update(object_id=type_id, object_type_id=None)
        elif invalid_case == "identical_type_ids":
            payload["object_id"] = type_id
        elif invalid_case == "no_edit_permission":
            connection.execute(
                text("DELETE FROM iz.role_permissions WHERE permission = 'inventory.edit'")
            )
    command = envelope(payload)
    response = client.post("/api/commands", json=command)
    assert response.status_code == expected_status, response.text
    with engine.connect() as connection:
        assert (
            connection.scalar(
                text("SELECT object_type_id FROM iz.objects WHERE id = :id"), {"id": item_id}
            )
            is None
        )
        for entity_id in (item_id, type_id):
            assert (
                connection.scalar(
                    text("SELECT version FROM iz.entities WHERE id = :id"), {"id": entity_id}
                )
                == 1
            )
        assert (
            connection.scalar(
                text("SELECT count(*) FROM iz.domain_events WHERE command_id = :id"),
                {"id": command["command_id"]},
            )
            == 0
        )
        assert (
            connection.scalar(
                text("SELECT count(*) FROM iz.command_receipts WHERE command_id = :id"),
                {"id": command["command_id"]},
            )
            == 0
        )


def test_csrf_origin_and_capability_boundary(client, database):
    session = sign_in(client)
    command = {
        "authority_site": session["site_id"],
        "authority_epoch": 1,
        "command_epoch": 1,
        "command_id": str(uuid4()),
        "payload": {"kind": "object.create", "name": "Blocked"},
    }
    assert (
        client.post(
            "/api/commands", json=command, headers={"origin": "https://attacker.invalid"}
        ).status_code
        == 403
    )
    assert (
        client.post("/api/commands", json=command, headers={"x-csrf-token": "wrong"}).status_code
        == 403
    )
    engine, _site, actor = database
    with engine.begin() as connection:
        connection.execute(
            text("DELETE FROM iz.account_roles WHERE account_id = :id"), {"id": actor}
        )
    assert client.post("/api/commands", json=command).status_code == 403


def test_hierarchy_paths_subtree_search_pagination_and_ancestor_move(client):
    session = sign_in(client)

    def submit(payload):
        response = client.post(
            "/api/commands",
            json={
                "authority_site": session["site_id"],
                "authority_epoch": 1,
                "command_epoch": session["command_epoch"],
                "command_id": str(uuid4()),
                "payload": payload,
            },
        )
        assert response.status_code == 200, response.text
        return response.json()["entity_id"]

    rack = submit({"kind": "object.create", "name": "Rack A"})
    shelf = submit({"kind": "object.create", "name": "Shelf 2", "parent_id": rack})
    drawer = submit({"kind": "object.create", "name": "Drawer", "parent_id": shelf})
    meter = submit({"kind": "object.create", "name": "Meter", "parent_id": drawer})
    other = submit({"kind": "object.create", "name": "Other room"})
    submit({"kind": "object.create", "name": "Meter outside", "parent_id": other})
    expected_path = [
        {"id": rack, "name": "Rack A"},
        {"id": shelf, "name": "Shelf 2"},
        {"id": drawer, "name": "Drawer"},
    ]
    item = client.get(f"/api/objects/{meter}").json()
    assert item["location_path"] == expected_path
    assert item["child_count"] == 0
    assert client.get(f"/api/objects/{rack}").json()["location_path"] == []
    assert client.get(f"/api/objects/{drawer}").json()["child_count"] == 1
    matches = client.get("/api/objects", params={"subtree_id": rack, "query": "Meter"}).json()
    assert matches["total"] == 1
    assert matches["items"][0]["location_path"] == expected_path
    subtree = client.get("/api/objects", params={"subtree_id": shelf}).json()
    assert {item["id"] for item in subtree["items"]} == {shelf, drawer, meter}
    for index in range(51):
        submit({"kind": "object.create", "name": f"Part {index:02d}", "parent_id": drawer})
    first = client.get("/api/objects", params={"parent_id": drawer}).json()
    second = client.get("/api/objects", params={"parent_id": drawer, "offset": 50}).json()
    assert first["total"] == second["total"] == 52
    assert len(first["items"]) == 50 and len(second["items"]) == 2
    assert len({item["id"] for item in first["items"] + second["items"]}) == 52
    submit({"kind": "object.move", "object_id": shelf, "expected_version": 1, "parent_id": other})
    moved = client.get(f"/api/objects/{meter}").json()
    assert moved["version"] == 1
    assert moved["location_path"] == [{"id": other, "name": "Other room"}, *expected_path[1:]]
    assert client.get("/api/objects", params={"subtree_id": rack}).json()["total"] == 1


def test_tag_dag_inheritance_provenance_filters_and_cycle_rejection(client):
    session = sign_in(client)

    def submit(payload, expected=200):
        response = client.post(
            "/api/commands",
            json={
                "authority_site": session["site_id"],
                "authority_epoch": 1,
                "command_epoch": session["command_epoch"],
                "command_id": str(uuid4()),
                "payload": payload,
            },
        )
        assert response.status_code == expected, response.text
        return response.json()

    hardware = submit({"kind": "tag.create", "name": "Hardware"})["entity_id"]
    metric = submit({"kind": "tag.create", "name": "Metric Hardware", "parent_ids": [hardware]})[
        "entity_id"
    ]
    m5 = submit({"kind": "tag.create", "name": "M5", "parent_ids": [metric]})["entity_id"]
    nut = submit({"kind": "tag.create", "name": "Nut"})["entity_id"]
    slot = submit({"kind": "tag.create", "name": "M5 Slot Nut"})["entity_id"]
    submit(
        {"kind": "tag.parents.set", "tag_id": slot, "expected_version": 1, "parent_ids": [nut, m5]}
    )
    service = submit({"kind": "tag.create", "name": "Needs Repair"})["entity_id"]
    object_type = submit({"kind": "type.create", "name": "DIN 508 T-nut"})["entity_id"]
    submit(
        {
            "kind": "tags.set",
            "entity_id": object_type,
            "entity_kind": "object_type",
            "expected_version": 1,
            "tag_ids": [slot],
        }
    )
    box = submit({"kind": "object.create", "name": "Fastener box"})["entity_id"]
    item = submit(
        {
            "kind": "object.create",
            "name": "T-nut stock",
            "object_type_id": object_type,
            "parent_id": box,
            "tag_ids": [service],
        }
    )["entity_id"]
    submit({"kind": "object.create", "name": "Outside stock", "object_type_id": object_type})

    tags = {tag["name"]: tag for tag in client.get("/api/tags").json()}
    assert set(tags["M5 Slot Nut"]["parent_ids"]) == {nut, m5}
    assert tags["M5 Slot Nut"]["direct_type_count"] == 1
    assert tags["Needs Repair"]["direct_object_count"] == 1

    effective = {
        tag["name"]: tag for tag in client.get(f"/api/objects/{item}/tags").json()["effective_tags"]
    }
    assert set(effective) == {
        "Hardware",
        "Metric Hardware",
        "M5",
        "Nut",
        "M5 Slot Nut",
        "Needs Repair",
    }
    assert effective["Hardware"]["sources"] == [
        {"source_kind": "object_type", "tag_id": slot, "tag_name": "M5 Slot Nut"}
    ]
    assert effective["Needs Repair"]["sources"] == [
        {"source_kind": "object", "tag_id": service, "tag_name": "Needs Repair"}
    ]
    filtered = client.get("/api/objects", params={"tag_id": hardware, "subtree_id": box}).json()
    assert filtered["total"] == 1 and filtered["items"][0]["id"] == item
    assert client.get("/api/objects", params={"type_id": object_type}).json()["total"] == 2

    submit({"kind": "tag.parents.set", "tag_id": slot, "expected_version": 2, "parent_ids": [nut]})
    assert client.get("/api/objects", params={"tag_id": hardware}).json()["total"] == 0
    assert client.get(f"/api/objects/{item}").json()["version"] == 1
    submit(
        {"kind": "tag.parents.set", "tag_id": slot, "expected_version": 3, "parent_ids": [nut, m5]}
    )
    assert client.get("/api/objects", params={"tag_id": hardware}).json()["total"] == 2

    submit({"kind": "tag.edit", "tag_id": service, "expected_version": 1, "name": "Service Due"})
    refreshed = client.get(f"/api/objects/{item}/tags").json()
    assert "Service Due" in {tag["name"] for tag in refreshed["effective_tags"]}
    assert client.get(f"/api/objects/{item}").json()["version"] == 1

    rejected = submit(
        {
            "kind": "tag.parents.set",
            "tag_id": hardware,
            "expected_version": 1,
            "parent_ids": [slot],
        },
        expected=409,
    )
    assert rejected["code"] == "TAG_CYCLE"


def test_type_hierarchy_abstract_assignments_inherited_tags_and_filters(client):
    session = sign_in(client)

    def submit(payload, expected=200):
        response = client.post(
            "/api/commands",
            json={
                "authority_site": session["site_id"],
                "authority_epoch": 1,
                "command_epoch": session["command_epoch"],
                "command_id": str(uuid4()),
                "payload": payload,
            },
        )
        assert response.status_code == expected, response.text
        return response.json()

    container = submit({"kind": "tag.create", "name": "Storage container"})["entity_id"]
    stackable = submit({"kind": "tag.create", "name": "Stackable"})["entity_id"]
    root = submit(
        {
            "kind": "type.create",
            "name": "Storage container",
            "abstract": True,
            "tag_ids": [container],
        }
    )["entity_id"]
    family = submit(
        {
            "kind": "type.create",
            "name": "ACME SuperStack",
            "abstract": True,
            "parent_type_id": root,
            "tag_ids": [stackable],
        }
    )["entity_id"]
    tote = submit(
        {"kind": "type.create", "name": "Medium SuperStack Tote", "parent_type_id": family}
    )["entity_id"]

    types = {item["id"]: item for item in client.get("/api/types").json()}
    assert types[root]["abstract"] is True and types[root]["parent_type_id"] is None
    assert types[family]["parent_type_id"] == root
    assert types[tote]["parent_name"] == "ACME SuperStack"

    item = submit({"kind": "object.create", "name": "Tote A17", "object_type_id": tote})[
        "entity_id"
    ]
    effective = client.get(f"/api/objects/{item}/tags").json()["effective_tags"]
    assert {tag["name"] for tag in effective} == {"Storage container", "Stackable"}
    assert client.get("/api/objects", params={"type_id": root}).json()["total"] == 1
    assert (
        client.get("/api/objects", params={"type_id": root, "exact_type": True}).json()["total"]
        == 0
    )
    assert client.get("/api/objects", params={"type_id": family}).json()["total"] == 1
    assert client.get("/api/objects", params={"type_id": tote}).json()["total"] == 1

    rejected = submit(
        {"kind": "object.create", "name": "Invalid", "object_type_id": family}, expected=409
    )
    assert rejected["code"] == "ABSTRACT_TYPE"
    rejected = submit(
        {
            "kind": "type.edit",
            "type_id": root,
            "expected_version": 1,
            "name": "Storage container",
            "parent_type_id": tote,
            "abstract": True,
        },
        expected=409,
    )
    assert rejected["code"] == "TYPE_CYCLE"
    rejected = submit(
        {
            "kind": "type.edit",
            "type_id": tote,
            "expected_version": 1,
            "name": "Medium SuperStack Tote",
            "parent_type_id": family,
            "abstract": True,
        },
        expected=409,
    )
    assert rejected["code"] == "TYPE_IN_USE"


def test_typed_property_inheritance_unset_quantities_and_structural_guards(client):
    session = sign_in(client)

    def submit(payload, expected=200):
        response = client.post(
            "/api/commands",
            json={
                "authority_site": session["site_id"],
                "authority_epoch": 1,
                "command_epoch": session["command_epoch"],
                "command_id": str(uuid4()),
                "payload": payload,
            },
        )
        assert response.status_code == expected, response.text
        return response.json()

    volume = submit(
        {
            "kind": "property.definition.create",
            "key": "storage.volume",
            "label": "Volume",
            "value_type": "quantity",
            "quantity_dimension": "volume",
            "allowed_units": ["ml", "l", "us_gal", "imperial_gal"],
        }
    )["entity_id"]
    placeholders = client.get("/api/label-placeholders").json()
    assert "storage.volume" in {field["key"] for field in placeholders}
    root = submit({"kind": "type.create", "name": "Storage container", "abstract": True})[
        "entity_id"
    ]
    tote = submit({"kind": "type.create", "name": "20 gallon tote", "parent_type_id": root})[
        "entity_id"
    ]
    submit(
        {
            "kind": "type.property.declare",
            "type_id": root,
            "expected_version": 1,
            "property_id": volume,
            "applicable": True,
        }
    )
    submit(
        {
            "kind": "property.value.set",
            "target_id": tote,
            "target_kind": "object_type",
            "expected_version": 1,
            "property_id": volume,
            "mode": "value",
            "value": {"amount": "20", "unit": "us_gal"},
        }
    )
    item = submit({"kind": "object.create", "name": "Tote A17", "object_type_id": tote})[
        "entity_id"
    ]

    inherited = client.get(f"/api/objects/{item}/properties").json()
    stored = next(field for field in inherited if field["id"] == volume)
    assert stored["formatted_value"] == "20 US gal"
    assert stored["value"]["amount"] == "75708.235680"
    assert stored["source_id"] == tote and stored["local_state"] == "inherit"
    assert stored["applicability_source_id"] == root

    submit(
        {
            "kind": "property.value.set",
            "target_id": item,
            "target_kind": "object",
            "expected_version": 1,
            "property_id": volume,
            "mode": "unset",
        }
    )
    unset = next(
        field
        for field in client.get(f"/api/objects/{item}/properties").json()
        if field["id"] == volume
    )
    assert unset["effective_state"] == "unset" and unset["local_state"] == "unset"
    submit(
        {
            "kind": "property.value.set",
            "target_id": item,
            "target_kind": "object",
            "expected_version": 2,
            "property_id": volume,
            "mode": "inherit",
        }
    )
    assert (
        next(
            field
            for field in client.get(f"/api/objects/{item}/properties").json()
            if field["id"] == volume
        )["formatted_value"]
        == "20 US gal"
    )
    submit(
        {
            "kind": "property.value.set",
            "target_id": item,
            "target_kind": "object",
            "expected_version": 3,
            "property_id": volume,
            "mode": "value",
            "value": {"amount": "18", "unit": "us_gal"},
        }
    )

    rejected = submit(
        {
            "kind": "type.property.declare",
            "type_id": root,
            "expected_version": 2,
            "property_id": volume,
            "applicable": False,
        },
        expected=409,
    )
    assert rejected["code"] == "PROPERTY_APPLICABILITY_CONFLICT"
    rejected = submit(
        {
            "kind": "type.edit",
            "type_id": tote,
            "expected_version": 2,
            "name": "20 gallon tote",
            "parent_type_id": None,
        },
        expected=409,
    )
    assert rejected["code"] == "PROPERTY_APPLICABILITY_CONFLICT"
    rejected = submit(
        {
            "kind": "object.type.set",
            "object_id": item,
            "expected_version": 4,
            "object_type_id": None,
        },
        expected=409,
    )
    assert rejected["code"] == "PROPERTY_APPLICABILITY_CONFLICT"
    rejected = submit(
        {
            "kind": "property.value.set",
            "target_id": item,
            "target_kind": "object",
            "expected_version": 4,
            "property_id": volume,
            "mode": "value",
            "value": {"amount": "1", "unit": "kg"},
        },
        expected=422,
    )
    assert rejected["code"] == "INVALID_PROPERTY_VALUE"


def test_property_edits_unit_conversion_and_unused_definition_deletion(client):
    session = sign_in(client)

    def submit(payload, expected=200):
        response = client.post(
            "/api/commands",
            json={
                "authority_site": session["site_id"],
                "authority_epoch": 1,
                "command_epoch": session["command_epoch"],
                "command_id": str(uuid4()),
                "payload": payload,
            },
        )
        assert response.status_code == expected, response.text
        return response.json()

    property_id = submit(
        {
            "kind": "property.definition.create",
            "key": "storage.volume",
            "label": "Volume",
            "description": "Original description",
            "value_type": "quantity",
            "quantity_dimension": "volume",
            "allowed_units": ["ml", "l", "us_gal"],
        }
    )["entity_id"]
    submit(
        {
            "kind": "property.definition.edit",
            "property_id": property_id,
            "expected_version": 1,
            "label": "Capacity",
            "description": "Container capacity",
        }
    )
    definition = next(
        item for item in client.get("/api/properties").json() if item["id"] == property_id
    )
    assert definition["key"] == "storage.volume"
    assert definition["label"] == "Capacity"
    root = submit({"kind": "type.create", "name": "Container", "abstract": True})["entity_id"]
    submit(
        {
            "kind": "type.property.declare",
            "type_id": root,
            "expected_version": 1,
            "property_id": property_id,
            "applicable": True,
        }
    )
    submit(
        {
            "kind": "property.value.set",
            "target_id": root,
            "target_kind": "object_type",
            "expected_version": 2,
            "property_id": property_id,
            "mode": "value",
            "value": {"amount": "20", "unit": "us_gal"},
        }
    )
    submit(
        {
            "kind": "property.unit.set",
            "target_id": root,
            "target_kind": "object_type",
            "expected_version": 3,
            "property_id": property_id,
            "unit": "l",
        }
    )
    converted = next(
        item
        for item in client.get(f"/api/types/{root}/properties").json()
        if item["id"] == property_id
    )
    assert converted["value"]["amount"] == "75708.235680"
    assert converted["value"]["display_unit"] == "l"
    assert converted["value"]["display_amount"] == "75.70823568"
    rejected = submit(
        {
            "kind": "definition.delete",
            "entity_id": property_id,
            "entity_kind": "property_definition",
            "expected_version": 2,
        },
        expected=409,
    )
    assert rejected["code"] == "DEFINITION_IN_USE"
    submit(
        {
            "kind": "property.value.set",
            "target_id": root,
            "target_kind": "object_type",
            "expected_version": 4,
            "property_id": property_id,
            "mode": "inherit",
        }
    )
    submit(
        {
            "kind": "type.property.declare",
            "type_id": root,
            "expected_version": 5,
            "property_id": property_id,
            "applicable": False,
        }
    )
    submit(
        {
            "kind": "definition.delete",
            "entity_id": property_id,
            "entity_kind": "property_definition",
            "expected_version": 2,
        }
    )
    assert property_id not in {item["id"] for item in client.get("/api/properties").json()}

    unused_tag = submit({"kind": "tag.create", "name": "Unused"})["entity_id"]
    submit(
        {
            "kind": "definition.delete",
            "entity_id": unused_tag,
            "entity_kind": "tag",
            "expected_version": 1,
        }
    )
    assert unused_tag not in {item["id"] for item in client.get("/api/tags").json()}
    unused_type = submit({"kind": "type.create", "name": "Unused type"})["entity_id"]
    submit(
        {
            "kind": "definition.delete",
            "entity_id": unused_type,
            "entity_kind": "object_type",
            "expected_version": 1,
        }
    )
    assert unused_type not in {item["id"] for item in client.get("/api/types").json()}


def test_live_scanner_move_session_is_memory_only_and_uses_inventory_commands(client):
    session = sign_in(client)
    controller_id = str(uuid4())
    scanner_headers = {
        "X-CSRF-Token": session["csrf_token"],
        "X-Scanner-Controller": controller_id,
    }

    def submit(payload):
        response = client.post(
            "/api/commands",
            headers={"X-CSRF-Token": session["csrf_token"]},
            json={
                "authority_site": session["site_id"],
                "authority_epoch": 1,
                "command_epoch": session["command_epoch"],
                "command_id": str(uuid4()),
                "payload": payload,
            },
        )
        assert response.status_code == 200, response.text
        return response.json()

    destination = submit({"kind": "object.create", "name": "Destination"})["entity_id"]
    item = submit({"kind": "object.create", "name": "Scanned item"})["entity_id"]
    created = client.post(
        "/api/scanner/sessions",
        headers=scanner_headers,
        json={"mode": "move", "destination_id": destination, "controller_id": controller_id},
    )
    assert created.status_code == 200, created.text
    scanner_session = created.json()

    unknown = client.post(
        f"/api/scanner/sessions/{scanner_session['id']}/input",
        headers=scanner_headers,
        json={"payload": "not-an-inventory-label"},
    )
    assert unknown.json()["outcome"] == "no_action"
    assert unknown.json()["command_completed"] is False
    moved = client.post(
        f"/api/scanner/sessions/{scanner_session['id']}/input",
        headers=scanner_headers,
        json={"payload": item},
    )
    assert moved.status_code == 200, moved.text
    assert moved.json()["outcome"] == "success"
    assert moved.json()["command_completed"] is True
    assert client.get(f"/api/objects/{item}").json()["parent_id"] == destination
    results = client.get(
        f"/api/scanner/sessions/{scanner_session['id']}", headers=scanner_headers
    ).json()["results"]
    assert [result["outcome"] for result in results] == ["no_action", "success"]

    deleted = client.delete(
        f"/api/scanner/sessions/{scanner_session['id']}", headers=scanner_headers
    )
    assert deleted.status_code == 204
    assert (
        client.get(
            f"/api/scanner/sessions/{scanner_session['id']}", headers=scanner_headers
        ).status_code
        == 404
    )


def test_scanner_agent_rechecks_terminal_operator_access_before_each_scan(client):
    session = sign_in(client)
    controller_id = str(uuid4())
    headers = {
        "X-CSRF-Token": session["csrf_token"],
        "X-Scanner-Controller": controller_id,
    }
    item = client.post(
        "/api/commands",
        json={
            "authority_site": session["site_id"],
            "authority_epoch": 1,
            "command_epoch": session["command_epoch"],
            "command_id": str(uuid4()),
            "payload": {"kind": "object.create", "name": "Protected scan item"},
        },
    )
    assert item.status_code == 200, item.text
    active = client.post(
        "/api/scanner/sessions",
        headers=headers,
        json={"mode": "lookup", "controller_id": controller_id},
    )
    assert active.status_code == 200, active.text
    _service_id, token = create_scanner_service(client.app.state.engine, "test scanner")
    with client.app.state.engine.begin() as connection:
        connection.execute(
            text("DELETE FROM iz.account_roles WHERE account_id=:account"),
            {"account": session["account_id"]},
        )
    response = client.post(
        "/api/agent/scans",
        headers={"Authorization": f"Bearer {token}"},
        json={
            "event_id": str(uuid4()),
            "terminal_id": active.json()["terminal_id"],
            "payload": item.json()["entity_id"],
            "source_id": "test-scanner",
            "runtime_id": "runtime-1",
            "source_type": "test",
            "received_at": datetime.now(UTC).isoformat(),
        },
    )
    assert response.status_code == 200, response.text
    assert response.json()["outcome"] == "failure"
    assert "no longer authorized" in response.json()["message"]


def test_scanner_terminal_rejects_silent_second_tab_takeover(client):
    session = sign_in(client)
    first = str(uuid4())
    headers = {"X-CSRF-Token": session["csrf_token"], "X-Scanner-Controller": first}
    created = client.post(
        "/api/scanner/sessions", headers=headers, json={"mode": "lookup", "controller_id": first}
    )
    assert created.status_code == 200, created.text

    second = str(uuid4())
    replacement = client.post(
        "/api/scanner/sessions",
        headers={
            "X-CSRF-Token": session["csrf_token"],
            "X-Scanner-Controller": second,
        },
        json={"mode": "lookup", "controller_id": second},
    )
    assert replacement.status_code == 409

    takeover = client.post(
        "/api/scanner/sessions",
        headers={
            "X-CSRF-Token": session["csrf_token"],
            "X-Scanner-Controller": second,
        },
        json={"mode": "lookup", "controller_id": second, "take_over": True},
    )
    assert takeover.status_code == 200, takeover.text
    assert (
        client.get(f"/api/scanner/sessions/{created.json()['id']}", headers=headers).status_code
        == 404
    )


def test_object_creation_commits_initial_placement_and_property_overrides_together(client):
    session = sign_in(client)

    def submit(payload):
        response = client.post(
            "/api/commands",
            headers={"X-CSRF-Token": session["csrf_token"]},
            json={
                "authority_site": session["site_id"],
                "authority_epoch": 1,
                "command_epoch": session["command_epoch"],
                "command_id": str(uuid4()),
                "payload": payload,
            },
        )
        assert response.status_code == 200, response.text
        return response.json()

    parent = submit(
        {"kind": "object.create", "name": "Fixture", "allocate_alias": False}
    )["entity_id"]
    object_type = submit(
        {"kind": "type.create", "name": "Tool", "abstract": False, "tag_ids": []}
    )["entity_id"]
    field = submit(
        {
            "kind": "property.definition.create",
            "type_id": object_type,
            "expected_version": 1,
            "label": "Manufacturer",
            "value_type": "text",
        }
    )["entity_id"]
    created = submit(
        {
            "kind": "object.create",
            "name": "Caliper",
            "object_type_id": object_type,
            "parent_id": parent,
            "relation": "mounted_in",
            "allocate_alias": True,
            "property_values": [{"property_id": field, "mode": "value", "value": "Acme"}],
        }
    )
    object_id = created["entity_id"]
    object_view = client.get(f"/api/objects/{object_id}").json()
    assert object_view["parent_id"] == parent
    assert object_view["relation"] == "mounted_in"
    properties = client.get(f"/api/objects/{object_id}/properties").json()
    manufacturer = next(item for item in properties if item["id"] == field)
    assert manufacturer["value"] == "Acme"
    assert manufacturer["local_state"] == "value"


def test_duplicate_copies_local_properties_and_declarations_with_new_identity(client):
    session = sign_in(client)

    def submit(payload, status=200, command_id=None):
        response = client.post("/api/commands", headers={"X-CSRF-Token": session["csrf_token"]},
            json={"authority_site": session["site_id"], "authority_epoch": 1,
                  "command_epoch": session["command_epoch"],
                  "command_id": command_id or str(uuid4()), "payload": payload})
        assert response.status_code == status, response.text
        return response.json()

    source_type = submit({"kind": "type.create", "name": "Meter"})["entity_id"]
    field = submit({"kind": "property.definition.create", "type_id": source_type,
        "expected_version": 1, "label": "Manufacturer", "value_type": "text"})["entity_id"]
    submit({"kind": "property.value.set", "target_kind": "object_type", "target_id": source_type,
        "expected_version": 2, "property_id": field, "mode": "value", "value": "Acme"})
    copied_type = submit({"kind": "type.create", "name": "Other meter",
        "copy_properties_from": {"id": source_type, "expected_version": 3}})["entity_id"]
    properties = client.get(f"/api/types/{copied_type}/properties").json()
    assert next(item for item in properties if item["id"] == field)["value"] == "Acme"
    source = submit({"kind": "object.create", "name": "Original", "object_type_id": source_type,
        "property_values": [{"property_id": field, "mode": "unset"}]})["entity_id"]
    payload = {"kind": "object.create", "name": "Copy", "object_type_id": copied_type,
        "copy_properties_from": {"id": source, "expected_version": 1}}
    command_id = str(uuid4())
    copied = submit(payload, command_id=command_id)["entity_id"]
    assert submit(payload, command_id=command_id)["entity_id"] == copied
    assert copied != source
    copied_view = client.get(f"/api/objects/{copied}").json()
    source_view = client.get(f"/api/objects/{source}").json()
    assert copied_view["alias"] != source_view["alias"]
    properties = client.get(f"/api/objects/{copied}/properties").json()
    assert next(item for item in properties if item["id"] == field)["local_state"] == "unset"
    # A changed source is rejected rather than copying a mix of old and new details.
    submit({"kind": "object.edit", "object_id": source, "expected_version": 1,
            "name": "Changed original"})
    failure = submit(payload, status=409)
    assert failure["code"] == "VERSION_CONFLICT"


def test_failed_logins_are_rate_limited(client):
    for _attempt in range(10):
        assert (
            client.post(
                "/api/auth/login", json={"login": "unknown", "password": "wrong"}
            ).status_code
            == 401
        )
    assert (
        client.post("/api/auth/login", json={"login": "unknown", "password": "wrong"}).status_code
        == 429
    )


def test_explicit_bootstrap_is_atomic_and_not_repeatable(database):
    engine, _site, _actor = database
    with engine.begin() as connection:
        connection.execute(text("TRUNCATE iz.sites CASCADE"))
    bootstrap(engine, "Workshop", "Admin", "test-password-only")
    with pytest.raises(ValueError, match="already initialized"):
        bootstrap(engine, "Overwrite", "Admin", "test-password-only")
    with engine.connect() as connection:
        assert connection.scalar(text("SELECT count(*) FROM iz.accounts")) == 1
        assert connection.scalar(text("SELECT count(*) FROM iz.domain_events")) == 1
        assert connection.scalar(text("SELECT count(*) FROM iz.command_receipts")) == 1
