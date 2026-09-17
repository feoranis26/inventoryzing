ALTER TABLE iz.entities DROP CONSTRAINT entities_kind_check;
ALTER TABLE iz.entities ADD CONSTRAINT entities_kind_check
    CHECK (kind IN ('object', 'object_type', 'principal', 'account', 'tag'));

CREATE TABLE iz.tags (
    id uuid PRIMARY KEY,
    kind text GENERATED ALWAYS AS ('tag'::text) STORED,
    name text NOT NULL CHECK (length(trim(name)) > 0),
    description text NOT NULL DEFAULT '',
    FOREIGN KEY (id, kind) REFERENCES iz.entities(id, kind)
);

CREATE TABLE iz.tag_edges (
    child_id uuid NOT NULL REFERENCES iz.tags,
    parent_id uuid NOT NULL REFERENCES iz.tags,
    PRIMARY KEY (child_id, parent_id),
    CHECK (child_id <> parent_id)
);
CREATE INDEX tag_edges_parent ON iz.tag_edges(parent_id, child_id);

CREATE TABLE iz.entity_tags (
    entity_id uuid NOT NULL,
    target_kind text NOT NULL CHECK (target_kind IN ('object', 'object_type')),
    tag_id uuid NOT NULL REFERENCES iz.tags,
    PRIMARY KEY (entity_id, tag_id),
    FOREIGN KEY (entity_id, target_kind) REFERENCES iz.entities(id, kind)
);
CREATE INDEX entity_tags_tag ON iz.entity_tags(tag_id, entity_id);

CREATE CONSTRAINT TRIGGER tag_subtype AFTER DELETE ON iz.tags
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION iz.entity_complete();

CREATE OR REPLACE FUNCTION iz.entity_complete() RETURNS trigger LANGUAGE plpgsql AS $$
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
        WHEN 'tag' THEN SELECT EXISTS (
            SELECT 1 FROM iz.tags WHERE id = identity
        ) INTO complete;
    END CASE;
    IF NOT complete THEN RAISE EXCEPTION 'incomplete entity subtype: %', identity; END IF;
    RETURN NULL;
END $$;