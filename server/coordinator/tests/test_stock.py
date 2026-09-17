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
