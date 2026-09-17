ALTER TABLE iz.entities DROP CONSTRAINT entities_kind_check;
ALTER TABLE iz.entities ADD CONSTRAINT entities_kind_check
    CHECK (kind IN ('object', 'object_type', 'principal', 'account', 'tag',
                    'property_definition'));

CREATE TABLE iz.property_definitions (
    id uuid PRIMARY KEY,
    kind text GENERATED ALWAYS AS ('property_definition'::text) STORED,
    key text NOT NULL UNIQUE CHECK (
        key ~ '^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$'
    ),
    namespace text NOT NULL CHECK (namespace ~ '^[a-z][a-z0-9_]*$'),
    label text NOT NULL CHECK (length(trim(label)) > 0),
    description text NOT NULL DEFAULT '',
    value_type text NOT NULL CHECK (
        value_type IN ('text', 'integer', 'decimal', 'boolean', 'date', 'datetime', 'quantity')
    ),
    quantity_dimension text,
    canonical_unit text,
    allowed_units text[] NOT NULL DEFAULT ARRAY[]::text[],
    schema_revision bigint NOT NULL DEFAULT 1 CHECK (schema_revision > 0),
    FOREIGN KEY (id, kind) REFERENCES iz.entities(id, kind),
    CHECK ((value_type = 'quantity') =
           (quantity_dimension IS NOT NULL AND canonical_unit IS NOT NULL
            AND cardinality(allowed_units) > 0)),
    CHECK (value_type <> 'quantity' OR canonical_unit = ANY(allowed_units)),
    CHECK (split_part(key, '.', 1) = namespace)
);

CREATE TABLE iz.type_property_declarations (
    type_id uuid NOT NULL REFERENCES iz.object_types,
    property_id uuid NOT NULL REFERENCES iz.property_definitions,
    PRIMARY KEY (type_id, property_id)
);
CREATE INDEX type_property_declarations_property
    ON iz.type_property_declarations(property_id, type_id);

CREATE TABLE iz.property_values (
    target_id uuid NOT NULL,
    target_kind text NOT NULL CHECK (target_kind IN ('object', 'object_type')),
    property_id uuid NOT NULL REFERENCES iz.property_definitions,
    state text NOT NULL CHECK (state IN ('value', 'unset')),
    value jsonb,
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (target_id, property_id),
    FOREIGN KEY (target_id, target_kind) REFERENCES iz.entities(id, kind),
    CHECK ((state = 'value') = (value IS NOT NULL))
);
CREATE INDEX property_values_property ON iz.property_values(property_id, target_id);

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
        WHEN 'property_definition' THEN SELECT EXISTS (
            SELECT 1 FROM iz.property_definitions WHERE id = identity
        ) INTO complete;
    END CASE;
    IF NOT complete THEN RAISE EXCEPTION 'incomplete entity subtype: %', identity; END IF;
    RETURN NULL;
END $$;

CREATE CONSTRAINT TRIGGER property_definition_subtype
    AFTER DELETE ON iz.property_definitions DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION iz.entity_complete();
