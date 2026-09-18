from uuid import uuid4

from test_api import client as client
from test_api import sign_in


def test_stock_holdings_use_exact_canonical_balances_and_a_ledger(client):
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

    screws = submit({"kind": "type.create", "name": "M5 screw"})["entity_id"]
    submit({
        "kind": "stock.policy.set", "type_id": screws, "expected_version": 1,
        "quantity_dimension": "count", "unit": "each", "granularity": "1",
    })
    first = submit({
        "kind": "stock.holding.create", "name": "M5 screws — drawer", "object_type_id": screws,
        "amount": "500", "unit": "each",
    })
    second = submit({
        "kind": "stock.holding.create", "name": "M5 screws — toolbox", "object_type_id": screws,
        "amount": "80", "unit": "each",
    })
    first_object = client.get(f"/api/objects/{first['entity_id']}").json()
    assert first_object["stock"] == {
        "quantity": "500", "quantity_dimension": "count", "canonical_unit": "each",
        "granularity": "1", "allow_negative": False, "policy_type_id": screws,
        "policy_type_name": "M5 screw",
    }
    received = submit({
        "kind": "stock.receive", "holding_id": first["entity_id"],
        "expected_version": first["version"], "amount": "25", "unit": "each", "reason": "Delivery",
    })
    consumed = submit({
        "kind": "stock.consume", "holding_id": first["entity_id"],
        "expected_version": received["version"], "amount": "5", "unit": "each", "reason": "Used",
    })
    transferred = submit({
        "kind": "stock.transfer", "source_holding_id": first["entity_id"],
        "source_expected_version": consumed["version"],
        "destination_holding_id": second["entity_id"],
        "destination_expected_version": second["version"], "amount": "20", "unit": "each",
        "reason": "Restock toolbox",
    })
    assert client.get(f"/api/objects/{first['entity_id']}").json()["stock"]["quantity"] == "500"
    assert client.get(f"/api/objects/{second['entity_id']}").json()["stock"]["quantity"] == "100"
    history = client.get(f"/api/objects/{first['entity_id']}/history").json()
    assert history[0]["event_type"] == "stock.transfer"
    rejected = submit({
        "kind": "stock.consume", "holding_id": first["entity_id"],
        "expected_version": transferred["version"], "amount": "501", "unit": "each",
        "reason": "Too many",
    }, expected=409)
    assert rejected["code"] == "INSUFFICIENT_STOCK"


def test_stock_policy_is_inherited_but_holding_records_its_resolved_policy(client):
    session = sign_in(client)

    def submit(payload):
        response = client.post("/api/commands", json={
            "authority_site": session["site_id"], "authority_epoch": 1,
            "command_epoch": session["command_epoch"], "command_id": str(uuid4()),
            "payload": payload,
        })
        assert response.status_code == 200, response.text
        return response.json()

    parent = submit({"kind": "type.create", "name": "Wire"})["entity_id"]
    submit({"kind": "stock.policy.set", "type_id": parent, "expected_version": 1,
            "quantity_dimension": "length", "unit": "m", "granularity": "0.01"})
    child = submit({
        "kind": "type.create", "name": "Red wire", "parent_type_id": parent,
    })["entity_id"]
    policy = client.get(f"/api/types/{child}/stock-policy").json()
    assert policy["policy_type_id"] == parent and policy["canonical_unit"] == "m"
    holding = submit({
        "kind": "stock.holding.create", "name": "Red wire spool", "object_type_id": child,
        "amount": "25", "unit": "ft",
    })
    object_view = client.get(f"/api/objects/{holding['entity_id']}").json()
    assert object_view["stock"]["quantity"] == "7.6200"


def test_stock_type_creates_holdings_on_every_object_creation_path(client):
    session = sign_in(client)

    def submit(payload, expected=200):
        response = client.post("/api/commands", json={
            "authority_site": session["site_id"], "authority_epoch": 1,
            "command_epoch": session["command_epoch"], "command_id": str(uuid4()),
            "payload": payload,
        })
        assert response.status_code == expected, response.text
        return response.json()

    stock_type = submit({"kind": "type.create", "name": "M3 nut"})["entity_id"]
    preconfigured = submit({
        "kind": "object.create", "name": "Created before policy", "object_type_id": stock_type,
    })
    submit({"kind": "stock.policy.set", "type_id": stock_type, "expected_version": 1,
            "quantity_dimension": "count", "unit": "each", "granularity": "1"})
    preconfigured_view = client.get(f"/api/objects/{preconfigured['entity_id']}").json()
    assert preconfigured_view["stock"]["quantity"] == "0"
    zero = submit({"kind": "object.create", "name": "Empty tray", "object_type_id": stock_type})
    assert client.get(f"/api/objects/{zero['entity_id']}").json()["stock"]["quantity"] == "0"
    counted = submit({"kind": "object.create", "name": "Full tray", "object_type_id": stock_type,
                      "stock_amount": "250", "stock_unit": "each"})
    assert client.get(f"/api/objects/{counted['entity_id']}").json()["stock"]["quantity"] == "250"
    ordinary = submit({"kind": "object.create", "name": "Ordinary object"})
    converted = submit({"kind": "object.type.set", "object_id": ordinary["entity_id"],
                        "expected_version": ordinary["version"], "object_type_id": stock_type})
    assert client.get(f"/api/objects/{converted['entity_id']}").json()["stock"]["quantity"] == "0"
    converted_back = submit({
        "kind": "object.type.set", "object_id": converted["entity_id"],
        "expected_version": converted["version"], "object_type_id": None,
    })
    converted_back_view = client.get(f"/api/objects/{converted_back['entity_id']}").json()
    assert converted_back_view["stock"] is None
    history = client.get(f"/api/objects/{converted_back['entity_id']}/history").json()
    assert history[0]["event_type"] == "object.type.set"


def test_split_creates_a_second_placeable_holding_and_preserves_total(client):
    session = sign_in(client)

    def submit(payload):
        response = client.post("/api/commands", json={
            "authority_site": session["site_id"], "authority_epoch": 1,
            "command_epoch": session["command_epoch"], "command_id": str(uuid4()),
            "payload": payload,
        })
        assert response.status_code == 200, response.text
        return response.json()

    material = submit({"kind": "type.create", "name": "Heat shrink"})["entity_id"]
    submit({"kind": "stock.policy.set", "type_id": material, "expected_version": 1,
            "quantity_dimension": "length", "unit": "m", "granularity": "0.01"})
    source = submit({
        "kind": "stock.holding.create", "name": "Main roll", "object_type_id": material,
        "amount": "10", "unit": "m",
    })
    split = submit({"kind": "stock.split", "source_holding_id": source["entity_id"],
                    "source_expected_version": source["version"], "name": "Bench roll",
                    "amount": "2.5", "unit": "m", "reason": "Move to bench"})
    assert client.get(f"/api/objects/{source['entity_id']}").json()["stock"]["quantity"] == "7.5"
    created = client.get(f"/api/objects/{split['entity_id']}").json()
    assert created["name"] == "Bench roll" and created["stock"]["quantity"] == "2.5"
    history = client.get(f"/api/objects/{split['entity_id']}/history").json()
    assert history[0]["event_type"] == "stock.split"
