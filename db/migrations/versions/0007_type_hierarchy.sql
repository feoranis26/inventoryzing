ALTER TABLE iz.object_types
    ADD COLUMN parent_type_id uuid REFERENCES iz.object_types(id),
    ADD COLUMN abstract boolean NOT NULL DEFAULT false,
    ADD CONSTRAINT object_type_not_own_parent CHECK (id IS DISTINCT FROM parent_type_id);

CREATE INDEX object_types_parent ON iz.object_types(parent_type_id);

CREATE FUNCTION iz.object_type_is_concrete(type_id uuid) RETURNS boolean
LANGUAGE sql STABLE PARALLEL SAFE AS $$
    SELECT coalesce((SELECT NOT abstract FROM iz.object_types WHERE id = type_id), false)
$$;

ALTER TABLE iz.objects
    ADD CONSTRAINT objects_concrete_type CHECK (
        object_type_id IS NULL OR iz.object_type_is_concrete(object_type_id)
    ) NOT VALID;

ALTER TABLE iz.objects VALIDATE CONSTRAINT objects_concrete_type;

CREATE FUNCTION iz.prevent_abstract_assigned_type() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.abstract AND NOT OLD.abstract AND EXISTS (
        SELECT 1 FROM iz.objects WHERE object_type_id = NEW.id
    ) THEN
        RAISE EXCEPTION 'assigned object type cannot be abstract: %', NEW.id;
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER object_type_abstract_guard
    BEFORE UPDATE OF abstract ON iz.object_types
    FOR EACH ROW EXECUTE FUNCTION iz.prevent_abstract_assigned_type();
