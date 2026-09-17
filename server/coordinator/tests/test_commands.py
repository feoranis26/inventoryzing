from concurrent.futures import ThreadPoolExecutor
from threading import Barrier, Event
from uuid import uuid4

import pytest
from sqlalchemy import event, text
from sqlalchemy.exc import DBAPIError

from inventoryzing.commands import CommandError, execute_command, resolve_identifier
from inventoryzing.contracts import Command

pytestmark = pytest.mark.integration


def envelope(site, **payload):
    return Command(
        authority_site=site, authority_epoch=1, command_epoch=1, command_id=uuid4(), payload=payload
    )


def create(database, name="Oscilloscope", **values):
    engine, site, actor = database
    return execute_command(engine, envelope(site, kind="object.create", name=name, **values), actor)


def create_tag(database, name):
    engine, site, actor = database
    return execute_command(engine, envelope(site, kind="tag.create", name=name), actor)


def test_create_property_on_type_is_atomic_replayable_and_inherited(database):
    from inventoryzing.properties import resolve_stored_properties

    engine, site, actor = database
    parent = execute_command(engine, envelope(site, kind="type.create", name="Container"), actor)
    child = execute_command(engine, envelope(
        site, kind="type.create", name="Tote", parent_type_id=parent.entity_id
    ), actor)
    command = envelope(site, kind="property.definition.create", label="Capacity",
                       value_type="decimal", type_id=parent.entity_id, expected_version=1)
    result = execute_command(engine, command, actor)
    assert execute_command(engine, command, actor) == result
    with engine.connect() as connection:
        fields = resolve_stored_properties(connection, child.entity_id, "object_type")
        assert len(fields) == 1 and fields[0].id == result.entity_id
        assert fields[0].key.startswith("site.p_")
        assert fields[0].effective_state == "missing"
        assert fields[0].applicability_source_id == parent.entity_id
        assert connection.scalar(text("SELECT version FROM iz.entities WHERE id=:id"),
                                 {"id": parent.entity_id}) == 2
        assert connection.scalar(text("SELECT count(*) FROM iz.event_subjects WHERE event_id=:id"),
                                 {"id": result.event_id}) == 2
    with pytest.raises(CommandError, match="Refresh"):
        execute_command(engine, envelope(site, kind="property.definition.create", label="Stale",
                        value_type="text", type_id=parent.entity_id, expected_version=1), actor)
    with engine.connect() as connection:
        assert connection.scalar(text("SELECT count(*) FROM iz.property_definitions")) == 1
    another = execute_command(engine, envelope(site, kind="property.definition.create",
        label="Capacity", value_type="decimal", type_id=parent.entity_id,
        expected_version=2), actor)
    assert another.entity_id != result.entity_id


def test_duplicate_delivery_has_one_state_event_outbox_receipt(database):
    engine, site, actor = database
    command = envelope(site, kind="object.create", name="Oscilloscope")
    barrier = Barrier(2)

    def deliver():
        barrier.wait(timeout=10)
        return execute_command(engine, command, actor)

    with ThreadPoolExecutor(2) as executor:
        futures = [executor.submit(deliver) for _index in range(2)]
        first, duplicate = [future.result(timeout=20) for future in futures]
    assert first == duplicate
    assert execute_command(engine, command, actor) == first
    with engine.connect() as connection:
        for table in ("objects", "domain_events", "replication_outbox", "command_receipts"):
            assert connection.scalar(text(f"SELECT count(*) FROM iz.{table}")) == 1
        assert resolve_identifier(connection, first.alias) == first.entity_id
    changed = command.model_copy(
        update={"payload": command.payload.model_copy(update={"name": "Changed"})}
    )
    with pytest.raises(CommandError, match="different input"):
        execute_command(engine, changed, actor)


def test_alias_conflict_retries_and_exhaustion_rolls_back(database, monkeypatch):
    monkeypatch.setattr("inventoryzing.commands.secrets.randbelow", lambda _limit: 0)
    first = create(database)
    assert first.alias == "I000000"
    with pytest.raises(CommandError, match="No alias"):
        create(database, "Must not survive")
    engine, _site, _actor = database
    with engine.connect() as connection:
        assert connection.scalar(text("SELECT count(*) FROM iz.objects")) == 1
        assert connection.scalar(text("SELECT count(*) FROM iz.command_receipts")) == 1
    candidates = iter((0, 999999))
    monkeypatch.setattr("inventoryzing.commands.secrets.randbelow", lambda _limit: next(candidates))
    assert create(database, "Second").alias == "I999999"


@pytest.mark.parametrize(
    "value", ["000000", "i000000", "I00000", "I0000000", " I000000", "I000000\n", "I１２３４５６"]
)
def test_strict_machine_identifier(database, value):
    engine, _site, _actor = database
    with engine.connect() as connection, pytest.raises(CommandError) as error:
        resolve_identifier(connection, value)
    assert error.value.code == "INVALID_IDENTIFIER"


def test_concurrent_inverse_moves_cannot_create_cycle(database):
    engine, site, actor = database
    first, second = create(database, "Shelf"), create(database, "Bin")
    barrier = Barrier(2)

    def move(source, destination):
        barrier.wait(timeout=10)
        try:
            return execute_command(
                engine,
                envelope(
                    site,
                    kind="object.move",
                    object_id=source.entity_id,
                    expected_version=1,
                    parent_id=destination.entity_id,
                ),
                actor,
            )
        except CommandError as error:
            return error.code

    with ThreadPoolExecutor(2) as executor:
        futures = [executor.submit(move, first, second), executor.submit(move, second, first)]
        results = [future.result(timeout=20) for future in futures]
    assert results.count("PLACEMENT_CYCLE") == 1
    with engine.connect() as connection:
        assert connection.scalar(text("SELECT count(*) FROM iz.domain_events")) == 3


def test_concurrent_inverse_tag_edges_cannot_create_cycle(database):
    engine, site, actor = database
    first, second = create_tag(database, "Metric"), create_tag(database, "Hardware")
    barrier = Barrier(2)

    def set_parent(source, destination):
        barrier.wait(timeout=10)
        try:
            return execute_command(
                engine,
                envelope(
                    site,
                    kind="tag.parents.set",
                    tag_id=source.entity_id,
                    expected_version=1,
                    parent_ids=[destination.entity_id],
                ),
                actor,
            )
        except CommandError as error:
            return error.code

    with ThreadPoolExecutor(2) as executor:
        futures = [
            executor.submit(set_parent, first, second),
            executor.submit(set_parent, second, first),
        ]
        results = [future.result(timeout=20) for future in futures]
    assert results.count("TAG_CYCLE") == 1
    with engine.connect() as connection:
        assert connection.scalar(text("SELECT count(*) FROM iz.tag_edges")) == 1
        assert connection.scalar(text("SELECT count(*) FROM iz.domain_events")) == 3


def test_concurrent_inverse_type_parents_cannot_create_cycle(database):
    engine, site, actor = database
    first = execute_command(engine, envelope(site, kind="type.create", name="First"), actor)
    second = execute_command(engine, envelope(site, kind="type.create", name="Second"), actor)
    barrier = Barrier(2)

    def set_parent(source, destination):
        barrier.wait(timeout=10)
        try:
            return execute_command(
                engine,
                envelope(
                    site,
                    kind="type.edit",
                    type_id=source.entity_id,
                    expected_version=1,
                    name="Changed",
                    parent_type_id=destination.entity_id,
                ),
                actor,
            )
        except CommandError as error:
            return error.code

    with ThreadPoolExecutor(2) as executor:
        futures = [
            executor.submit(set_parent, first, second),
            executor.submit(set_parent, second, first),
        ]
        results = [future.result(timeout=20) for future in futures]
    assert results.count("TYPE_CYCLE") == 1
    with engine.connect() as connection:
        assert connection.scalar(
            text("SELECT count(*) FROM iz.object_types WHERE parent_type_id IS NOT NULL")
        ) == 1


def test_stale_update_rejected_and_move_preserves_description(database):
    engine, site, actor = database
    item = create(database, description="Calibrated")
    moved = execute_command(
        engine,
        envelope(
            site, kind="object.move", object_id=item.entity_id, expected_version=1, parent_id=None
        ),
        actor,
    )
    assert moved.version == 2
    with pytest.raises(CommandError) as error:
        execute_command(
            engine,
            envelope(
                site,
                kind="object.edit",
                object_id=item.entity_id,
                expected_version=1,
                name="Stale",
                description="Lost",
            ),
            actor,
        )
    assert error.value.code == "VERSION_CONFLICT"
    with engine.connect() as connection:
        assert (
            connection.scalar(
                text("SELECT description FROM iz.objects WHERE id = :id"), {"id": item.entity_id}
            )
            == "Calibrated"
        )


def test_epoch_closure_waits_for_admitted_command_and_remains_closed(database, monkeypatch):
    from inventoryzing.commands import allocate_alias

    engine, site, actor = database
    admitted, release, closing = Event(), Event(), Event()
    command = envelope(site, kind="object.create", name="In flight")

    def paused_alias(*args):
        admitted.set()
        assert release.wait(timeout=10)
        return allocate_alias(*args)

    def close_epoch():
        assert admitted.wait(timeout=10)
        with engine.begin() as connection:
            connection.execute(text("SET LOCAL lock_timeout = '5s'"))
            closing.set()
            connection.execute(
                text(
                    "UPDATE iz.command_epochs SET state = 'CLOSED', closed_at = now() "
                    "WHERE site_id = :site"
                ),
                {"site": site},
            )
            connection.execute(text("DELETE FROM iz.command_receipts"))

    monkeypatch.setattr("inventoryzing.commands.allocate_alias", paused_alias)
    with ThreadPoolExecutor(2) as executor:
        write = executor.submit(execute_command, engine, command, actor)
        close = executor.submit(close_epoch)
        assert closing.wait(timeout=10)
        assert not close.done()
        release.set()
        write.result(timeout=15)
        close.result(timeout=15)
    with pytest.raises(CommandError) as error:
        execute_command(engine, command, actor)
    assert error.value.code == "COMMAND_EPOCH_EXPIRED"
    with engine.connect() as connection:
        assert connection.scalar(text("SELECT count(*) FROM iz.objects")) == 1
        assert connection.scalar(text("SELECT count(*) FROM iz.domain_events")) == 1
    with pytest.raises(DBAPIError), engine.begin() as connection:
        connection.execute(text("UPDATE iz.command_epochs SET state = 'OPEN', closed_at = NULL"))


def test_open_epoch_receipt_cannot_be_deleted(database):
    create(database)
    engine, _site, _actor = database
    with pytest.raises(DBAPIError), engine.begin() as connection:
        connection.execute(text("DELETE FROM iz.command_receipts"))


def test_incomplete_entity_and_committed_receipt_changes_cannot_commit(database):
    engine, site, _actor = database
    with pytest.raises(DBAPIError), engine.begin() as connection:
        connection.execute(
            text("""
            INSERT INTO iz.entities(id, kind, home_site_id, write_site_id)
            VALUES (:id, 'object', :site, :site)
        """),
            {"id": uuid4(), "site": site},
        )
    create(database)
    with pytest.raises(DBAPIError), engine.begin() as connection:
        connection.execute(text("UPDATE iz.command_receipts SET result = '{}'::jsonb"))


def test_database_runtime_role_cannot_bypass_epoch_lifecycle(database):
    engine, _site, _actor = database
    with engine.connect() as connection:
        available = connection.scalar(
            text("SELECT 1 FROM pg_roles WHERE rolname = 'inventoryzing_app'")
        )
    if not available:
        pytest.skip("Run inventoryzing.migrate to provision the runtime role")
    with pytest.raises(DBAPIError), engine.begin() as connection:
        connection.execute(text("SET LOCAL ROLE inventoryzing_app"))
        connection.execute(text("UPDATE iz.command_epochs SET state = 'CLOSED', closed_at = now()"))


def test_runtime_role_can_create_move_and_replay(database):
    engine, site, actor = database
    with engine.connect() as connection:
        if not connection.scalar(
            text("SELECT 1 FROM pg_roles WHERE rolname = 'inventoryzing_app'")
        ):
            pytest.skip("Run inventoryzing.migrate to provision the runtime role")

    def restricted(connection):
        connection.execute(text("SET LOCAL ROLE inventoryzing_app"))

    event.listen(engine, "begin", restricted)
    try:
        item = create(database)
        command = envelope(
            site, kind="object.move", object_id=item.entity_id, expected_version=1, parent_id=None
        )
        result = execute_command(engine, command, actor)
        assert result.version == 2
        assert execute_command(engine, command, actor) == result
        tag = create_tag(database, "Restricted tag")
        classified = execute_command(
            engine,
            envelope(
                site,
                kind="tags.set",
                entity_id=item.entity_id,
                entity_kind="object",
                expected_version=2,
                tag_ids=[tag.entity_id],
            ),
            actor,
        )
        assert classified.version == 3
        definition = execute_command(
            engine,
            envelope(
                site,
                kind="property.definition.create",
                key="test.serial",
                label="Serial",
                value_type="text",
            ),
            actor,
        )
        object_type = execute_command(
            engine, envelope(site, kind="type.create", name="Test equipment"), actor
        )
        declared = execute_command(
            engine,
            envelope(
                site,
                kind="type.property.declare",
                type_id=object_type.entity_id,
                expected_version=1,
                property_id=definition.entity_id,
                applicable=True,
            ),
            actor,
        )
        assert declared.version == 2
    finally:
        event.remove(engine, "begin", restricted)
