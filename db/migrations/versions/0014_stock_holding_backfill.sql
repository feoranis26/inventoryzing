CREATE TEMP TABLE stock_holding_backfill ON COMMIT DROP AS
WITH RECURSIVE lineage(object_id, type_id, depth) AS (
    SELECT object.id, object.object_type_id, 0
    FROM iz.objects object
    LEFT JOIN iz.stock_holdings holding ON holding.object_id = object.id
    WHERE holding.object_id IS NULL AND object.object_type_id IS NOT NULL
    UNION ALL
    SELECT lineage.object_id, type.parent_type_id, lineage.depth + 1
    FROM lineage JOIN iz.object_types type ON type.id = lineage.type_id
    WHERE type.parent_type_id IS NOT NULL
)
SELECT DISTINCT ON (lineage.object_id) lineage.object_id, policy.type_id AS policy_type_id
FROM lineage JOIN iz.stock_policies policy ON policy.type_id = lineage.type_id
ORDER BY lineage.object_id, lineage.depth;

UPDATE iz.entities entity SET version = version + 1, updated_at = now()
FROM stock_holding_backfill backfill WHERE entity.id = backfill.object_id;

INSERT INTO iz.stock_holdings(object_id, quantity, policy_type_id)
SELECT object_id, 0, policy_type_id FROM stock_holding_backfill;

INSERT INTO iz.domain_events(
    id, source_site_id, source_incarnation, event_type, actor_id, command_id,
    authority_epoch, command_epoch, payload)
SELECT backfill.object_id, entity.write_site_id, site.incarnation,
       'stock.holding.initialized', NULL, backfill.object_id,
       entity.authority_epoch,
       (SELECT epoch FROM iz.command_epochs
        WHERE site_id=entity.write_site_id AND state='OPEN' LIMIT 1),
       jsonb_build_object('migrated', true, 'quantity', '0')
FROM stock_holding_backfill backfill
JOIN iz.entities entity ON entity.id=backfill.object_id
JOIN iz.sites site ON site.id=entity.write_site_id;

INSERT INTO iz.event_subjects(event_id, entity_id, version)
SELECT backfill.object_id, backfill.object_id, entity.version
FROM stock_holding_backfill backfill JOIN iz.entities entity ON entity.id=backfill.object_id;

INSERT INTO iz.stock_movements(
    id, holding_id, event_id, operation, delta, balance_after, reason)
SELECT object_id, object_id, object_id, 'initial', 0, 0, 'Initialized during stock migration'
FROM stock_holding_backfill;

INSERT INTO iz.replication_outbox(event_id)
SELECT object_id FROM stock_holding_backfill;
