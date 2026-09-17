CREATE SCHEMA iz;

CREATE TABLE iz.sites (
    id uuid PRIMARY KEY,
    display_name text NOT NULL CHECK (length(trim(display_name)) > 0),
    local boolean NOT NULL DEFAULT false,
    incarnation bigint NOT NULL DEFAULT 1 CHECK (incarnation > 0)
);
CREATE UNIQUE INDEX one_local_site ON iz.sites(local) WHERE local;

CREATE TABLE iz.entities (
    id uuid PRIMARY KEY,
    kind text NOT NULL CHECK (kind IN ('object', 'object_type', 'principal', 'account')),
    home_site_id uuid NOT NULL REFERENCES iz.sites,
    write_site_id uuid NOT NULL REFERENCES iz.sites,
    authority_epoch bigint NOT NULL DEFAULT 1 CHECK (authority_epoch > 0),
    version bigint NOT NULL DEFAULT 1 CHECK (version > 0),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    archived_at timestamptz,
    UNIQUE(id, kind)
);

CREATE TABLE iz.principals (
    id uuid PRIMARY KEY,
    kind text GENERATED ALWAYS AS ('principal'::text) STORED,
    principal_kind text NOT NULL CHECK (principal_kind IN ('person', 'team', 'organization', 'project', 'site')),
    display_name text NOT NULL CHECK (length(trim(display_name)) > 0),
    FOREIGN KEY (id, kind) REFERENCES iz.entities(id, kind)
);
CREATE TABLE iz.accounts (
    id uuid PRIMARY KEY,
    kind text GENERATED ALWAYS AS ('account'::text) STORED,
    principal_id uuid REFERENCES iz.principals,
    login text NOT NULL UNIQUE,
    password_hash text NOT NULL,
    disabled boolean NOT NULL DEFAULT false,
    FOREIGN KEY (id, kind) REFERENCES iz.entities(id, kind)
);
CREATE TABLE iz.roles (id uuid PRIMARY KEY, name text NOT NULL UNIQUE);
CREATE TABLE iz.permissions (code text PRIMARY KEY);
CREATE TABLE iz.role_permissions (
    role_id uuid REFERENCES iz.roles,
    permission text REFERENCES iz.permissions,
    PRIMARY KEY(role_id, permission)
);
CREATE TABLE iz.account_roles (
    account_id uuid REFERENCES iz.accounts,
    role_id uuid REFERENCES iz.roles,
    PRIMARY KEY(account_id, role_id)
);
INSERT INTO iz.permissions(code) VALUES
    ('inventory.read'), ('inventory.create'), ('inventory.edit'), ('inventory.move'),
    ('label.print'), ('principal.manage'), ('system.epochs.manage');
CREATE TABLE iz.sessions (
    token_hash bytea PRIMARY KEY CHECK (octet_length(token_hash) = 32),
    account_id uuid NOT NULL REFERENCES iz.accounts,
    csrf_token text NOT NULL,
    expires_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX sessions_account ON iz.sessions(account_id);
CREATE INDEX sessions_expiry ON iz.sessions(expires_at);
CREATE TABLE iz.login_attempts (
    login text PRIMARY KEY,
    failures integer NOT NULL DEFAULT 0,
    window_started_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE iz.object_types (
    id uuid PRIMARY KEY,
    kind text GENERATED ALWAYS AS ('object_type'::text) STORED,
    name text NOT NULL CHECK (length(trim(name)) > 0),
    description text NOT NULL DEFAULT '',
    FOREIGN KEY (id, kind) REFERENCES iz.entities(id, kind)
);
CREATE TABLE iz.objects (
    id uuid PRIMARY KEY,
    kind text GENERATED ALWAYS AS ('object'::text) STORED,
    name text NOT NULL CHECK (length(trim(name)) > 0),
    object_type_id uuid REFERENCES iz.object_types,
    description text NOT NULL DEFAULT '',
    owner_principal_id uuid REFERENCES iz.principals,
    assigned_principal_id uuid REFERENCES iz.principals,
    custodian_principal_id uuid REFERENCES iz.principals,
    FOREIGN KEY (id, kind) REFERENCES iz.entities(id, kind)
);
CREATE INDEX objects_type ON iz.objects(object_type_id);
CREATE INDEX objects_owner ON iz.objects(owner_principal_id);
CREATE TABLE iz.placements (
    object_id uuid PRIMARY KEY REFERENCES iz.objects,
    parent_id uuid REFERENCES iz.objects,
    relation text NOT NULL DEFAULT 'contained_in'
        CHECK (relation IN ('contained_in', 'installed_in', 'mounted_in', 'located_in')),
    CHECK (object_id IS DISTINCT FROM parent_id)
);
CREATE INDEX placement_children ON iz.placements(parent_id);

CREATE TABLE iz.identifiers (
    id uuid PRIMARY KEY,
    namespace text NOT NULL CHECK (namespace = 'inventoryzing.local'),
    issuer_site_id uuid NOT NULL REFERENCES iz.sites,
    value text NOT NULL CHECK (value ~ '^[0-9]{6}$'),
    entity_id uuid NOT NULL REFERENCES iz.entities,
    created_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE(namespace, issuer_site_id, value),
    UNIQUE(namespace, issuer_site_id, entity_id)
);
CREATE INDEX identifier_target ON iz.identifiers(entity_id);

CREATE TABLE iz.command_epochs (
    site_id uuid REFERENCES iz.sites,
    epoch bigint CHECK (epoch > 0),
    state text NOT NULL DEFAULT 'OPEN' CHECK (state IN ('OPEN', 'CLOSED')),
    opened_at timestamptz NOT NULL DEFAULT now(),
    closed_at timestamptz,
    PRIMARY KEY(site_id, epoch),
    CHECK ((state = 'OPEN' AND closed_at IS NULL) OR (state = 'CLOSED' AND closed_at IS NOT NULL))
);
CREATE UNIQUE INDEX one_open_epoch ON iz.command_epochs(site_id) WHERE state = 'OPEN';
CREATE TABLE iz.command_receipts (
    authority_site uuid NOT NULL REFERENCES iz.sites,
    authority_epoch bigint NOT NULL CHECK (authority_epoch > 0),
    command_epoch bigint NOT NULL,
    command_id uuid NOT NULL,
    actor_id uuid NOT NULL REFERENCES iz.accounts,
    fingerprint bytea NOT NULL CHECK (octet_length(fingerprint) = 32),
    result jsonb,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY(authority_site, authority_epoch, command_epoch, command_id),
    FOREIGN KEY(authority_site, command_epoch) REFERENCES iz.command_epochs(site_id, epoch)
);

CREATE TABLE iz.domain_events (
    id uuid PRIMARY KEY,
    source_site_id uuid NOT NULL REFERENCES iz.sites,
    source_incarnation bigint NOT NULL CHECK (source_incarnation > 0),
    event_type text NOT NULL,
    schema_version integer NOT NULL DEFAULT 1 CHECK (schema_version > 0),
    actor_id uuid REFERENCES iz.accounts,
    command_id uuid NOT NULL,
    authority_epoch bigint NOT NULL,
    command_epoch bigint NOT NULL,
    occurred_at timestamptz NOT NULL DEFAULT now(),
    payload jsonb NOT NULL
);
CREATE INDEX event_command ON iz.domain_events(command_id);
CREATE TABLE iz.event_subjects (
    event_id uuid REFERENCES iz.domain_events,
    entity_id uuid REFERENCES iz.entities,
    version bigint NOT NULL CHECK (version > 0),
    PRIMARY KEY(event_id, entity_id)
);
CREATE INDEX subject_history ON iz.event_subjects(entity_id);
CREATE TABLE iz.replication_outbox (
    event_id uuid PRIMARY KEY REFERENCES iz.domain_events,
    enqueued_at timestamptz NOT NULL DEFAULT now()
);

CREATE FUNCTION iz.prevent_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'immutable record in %', TG_TABLE_NAME;
END $$;
CREATE TRIGGER immutable_events BEFORE UPDATE OR DELETE ON iz.domain_events
    FOR EACH ROW EXECUTE FUNCTION iz.prevent_change();
CREATE TRIGGER immutable_subjects BEFORE UPDATE OR DELETE ON iz.event_subjects
    FOR EACH ROW EXECUTE FUNCTION iz.prevent_change();
CREATE TRIGGER immutable_alias BEFORE UPDATE OR DELETE ON iz.identifiers
    FOR EACH ROW EXECUTE FUNCTION iz.prevent_change();

CREATE FUNCTION iz.guard_epoch() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' OR OLD.state = 'CLOSED' OR NEW.epoch <> OLD.epoch OR NEW.site_id <> OLD.site_id THEN
        RAISE EXCEPTION 'command epoch cannot be removed or reopened';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER epoch_lifecycle BEFORE UPDATE OR DELETE ON iz.command_epochs
    FOR EACH ROW EXECUTE FUNCTION iz.guard_epoch();

CREATE FUNCTION iz.guard_receipt() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE epoch_state text;
BEGIN
    IF TG_OP = 'DELETE' THEN
        SELECT state INTO epoch_state FROM iz.command_epochs
            WHERE site_id = OLD.authority_site AND epoch = OLD.command_epoch FOR SHARE;
        IF epoch_state IS DISTINCT FROM 'CLOSED' THEN
            RAISE EXCEPTION 'receipt epoch remains executable';
        END IF;
        RETURN OLD;
    END IF;
    SELECT state INTO epoch_state FROM iz.command_epochs
        WHERE site_id = NEW.authority_site AND epoch = NEW.command_epoch FOR SHARE;
    IF epoch_state IS DISTINCT FROM 'OPEN' THEN
        RAISE EXCEPTION 'command epoch expired';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER receipt_admission BEFORE INSERT OR DELETE ON iz.command_receipts
    FOR EACH ROW EXECUTE FUNCTION iz.guard_receipt();
CREATE FUNCTION iz.receipt_complete() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF EXISTS (SELECT 1 FROM iz.command_receipts WHERE authority_site = NEW.authority_site
        AND authority_epoch = NEW.authority_epoch AND command_epoch = NEW.command_epoch
        AND command_id = NEW.command_id AND result IS NULL) THEN
        RAISE EXCEPTION 'command receipt has no committed result';
    END IF;
    RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER completed_receipt AFTER INSERT OR UPDATE ON iz.command_receipts
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION iz.receipt_complete();

CREATE FUNCTION iz.guard_placement() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    PERFORM pg_advisory_xact_lock(841901);
    IF NEW.parent_id IS NOT NULL AND EXISTS (
        WITH RECURSIVE ancestors(id) AS (
            SELECT NEW.parent_id
            UNION
            SELECT placement.parent_id FROM iz.placements placement
                JOIN ancestors ON placement.object_id = ancestors.id
                WHERE placement.parent_id IS NOT NULL
        ) SELECT 1 FROM ancestors WHERE id = NEW.object_id
    ) THEN
        RAISE EXCEPTION 'physical hierarchy cycle' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END $$;
CREATE TRIGGER physical_hierarchy BEFORE INSERT OR UPDATE ON iz.placements
    FOR EACH ROW EXECUTE FUNCTION iz.guard_placement();