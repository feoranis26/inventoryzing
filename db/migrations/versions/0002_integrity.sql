CREATE FUNCTION iz.entity_complete() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE identity uuid; entity_kind text; complete boolean;
BEGIN
    IF TG_TABLE_NAME = 'placements' THEN
        identity := OLD.object_id;
    ELSIF TG_OP = 'DELETE' THEN
        identity := OLD.id;
    ELSE
        identity := NEW.id;
    END IF;
    SELECT kind INTO entity_kind FROM iz.entities WHERE id = identity;
    IF entity_kind IS NULL THEN RETURN NULL; END IF;
    CASE entity_kind
        WHEN 'object' THEN SELECT EXISTS (
            SELECT 1 FROM iz.objects JOIN iz.placements ON object_id = id WHERE id = identity
        ) INTO complete;
        WHEN 'object_type' THEN SELECT EXISTS (
            SELECT 1 FROM iz.object_types WHERE id = identity
        ) INTO complete;
        WHEN 'principal' THEN SELECT EXISTS (
            SELECT 1 FROM iz.principals WHERE id = identity
        ) INTO complete;
        WHEN 'account' THEN SELECT EXISTS (
            SELECT 1 FROM iz.accounts WHERE id = identity
        ) INTO complete;
    END CASE;
    IF NOT complete THEN RAISE EXCEPTION 'incomplete entity subtype: %', identity; END IF;
    RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER entity_subtype AFTER INSERT OR UPDATE ON iz.entities
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION iz.entity_complete();
CREATE CONSTRAINT TRIGGER object_subtype AFTER DELETE ON iz.objects
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION iz.entity_complete();
CREATE CONSTRAINT TRIGGER type_subtype AFTER DELETE ON iz.object_types
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION iz.entity_complete();
CREATE CONSTRAINT TRIGGER principal_subtype AFTER DELETE ON iz.principals
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION iz.entity_complete();
CREATE CONSTRAINT TRIGGER account_subtype AFTER DELETE ON iz.accounts
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION iz.entity_complete();
CREATE CONSTRAINT TRIGGER object_placement AFTER DELETE ON iz.placements
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION iz.entity_complete();

CREATE FUNCTION iz.admit_epoch(authority_site uuid, command_epoch bigint)
RETURNS text LANGUAGE plpgsql SECURITY DEFINER SET search_path = pg_catalog, iz AS $$
DECLARE epoch_state text;
BEGIN
    SELECT state INTO epoch_state FROM iz.command_epochs
        WHERE site_id = authority_site AND epoch = command_epoch FOR SHARE;
    RETURN epoch_state;
END $$;
REVOKE ALL ON FUNCTION iz.admit_epoch(uuid, bigint) FROM PUBLIC;
ALTER FUNCTION iz.guard_receipt() SECURITY DEFINER;
ALTER FUNCTION iz.guard_receipt() SET search_path = pg_catalog, iz;

CREATE FUNCTION iz.receipt_immutable() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF OLD.result IS NOT NULL OR NEW.authority_site <> OLD.authority_site
       OR NEW.authority_epoch <> OLD.authority_epoch OR NEW.command_epoch <> OLD.command_epoch
       OR NEW.command_id <> OLD.command_id OR NEW.actor_id <> OLD.actor_id
       OR NEW.fingerprint <> OLD.fingerprint OR NEW.created_at <> OLD.created_at THEN
        RAISE EXCEPTION 'committed receipt cannot be changed';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER immutable_receipt BEFORE UPDATE ON iz.command_receipts
    FOR EACH ROW EXECUTE FUNCTION iz.receipt_immutable();