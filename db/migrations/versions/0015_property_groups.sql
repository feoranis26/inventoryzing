ALTER TABLE iz.entities DROP CONSTRAINT entities_kind_check;
ALTER TABLE iz.entities ADD CONSTRAINT entities_kind_check CHECK
    (kind IN ('object', 'object_type', 'principal', 'account', 'tag', 'property_definition', 'property_group'));
ALTER TABLE iz.property_definitions DROP CONSTRAINT property_definitions_value_type_check;
ALTER TABLE iz.property_definitions DROP CONSTRAINT property_definitions_check;
ALTER TABLE iz.property_definitions DROP CONSTRAINT property_definitions_check1;
ALTER TABLE iz.property_definitions ADD CHECK
    (value_type IN ('text', 'integer', 'decimal', 'boolean', 'date', 'datetime', 'quantity', 'quantity_range'));
ALTER TABLE iz.property_definitions ADD CHECK
    ((value_type IN ('quantity', 'quantity_range')) =
     (quantity_dimension IS NOT NULL AND canonical_unit IS NOT NULL AND cardinality(allowed_units) > 0));
ALTER TABLE iz.property_definitions ADD CHECK
    (value_type NOT IN ('quantity', 'quantity_range') OR canonical_unit = ANY(allowed_units));

CREATE TABLE iz.property_groups (
    id uuid PRIMARY KEY,
    kind text GENERATED ALWAYS AS ('property_group'::text) STORED,
    name text NOT NULL CHECK (length(trim(name)) > 0),
    description text NOT NULL DEFAULT '',
    FOREIGN KEY (id, kind) REFERENCES iz.entities(id, kind)
);
CREATE TABLE iz.property_group_members (
    group_id uuid NOT NULL REFERENCES iz.property_groups,
    property_id uuid NOT NULL REFERENCES iz.property_definitions,
    PRIMARY KEY (group_id, property_id)
);
CREATE INDEX property_group_members_property ON iz.property_group_members(property_id);
CREATE TABLE iz.type_property_groups (
    type_id uuid NOT NULL REFERENCES iz.object_types,
    group_id uuid NOT NULL REFERENCES iz.property_groups,
    PRIMARY KEY (type_id, group_id)
);
CREATE INDEX type_property_groups_group ON iz.type_property_groups(group_id);
CREATE VIEW iz.available_type_properties AS
    SELECT type_id, property_id, true AS direct FROM iz.type_property_declarations
    UNION ALL
    SELECT assignment.type_id, member.property_id, false AS direct
    FROM iz.type_property_groups assignment
    JOIN iz.property_group_members member ON member.group_id = assignment.group_id;

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
        WHEN 'property_group' THEN SELECT EXISTS (
            SELECT 1 FROM iz.property_groups WHERE id = identity
        ) INTO complete;
        WHEN 'property_definition' THEN SELECT EXISTS (
            SELECT 1 FROM iz.property_definitions WHERE id = identity
        ) INTO complete;
    END CASE;
    IF NOT complete THEN RAISE EXCEPTION 'incomplete entity subtype: %', identity; END IF;
    RETURN NULL;
END $$;

CREATE CONSTRAINT TRIGGER property_group_subtype
    AFTER DELETE ON iz.property_groups DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION iz.entity_complete();
