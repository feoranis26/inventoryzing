ALTER TABLE iz.accounts
    ADD COLUMN account_kind text NOT NULL DEFAULT 'human'
    CHECK (account_kind IN ('human', 'service'));

CREATE TABLE iz.service_credentials (
    id uuid PRIMARY KEY,
    account_id uuid NOT NULL REFERENCES iz.accounts,
    token_hash bytea NOT NULL UNIQUE CHECK (octet_length(token_hash) = 32),
    description text NOT NULL DEFAULT '',
    expires_at timestamptz,
    revoked_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX service_credentials_account ON iz.service_credentials(account_id);

INSERT INTO iz.permissions(code) VALUES
    ('print.job.create'), ('print.agent')
ON CONFLICT DO NOTHING;

CREATE SCHEMA iz_print;

CREATE TABLE iz_print.artifacts (
    id uuid PRIMARY KEY,
    sha256 bytea NOT NULL UNIQUE CHECK (octet_length(sha256) = 32),
    media_type text NOT NULL CHECK (media_type = 'image/png'),
    width_mm numeric(8,3) NOT NULL CHECK (width_mm > 0),
    height_mm numeric(8,3) NOT NULL CHECK (height_mm > 0),
    pixel_width integer NOT NULL CHECK (pixel_width > 0),
    pixel_height integer NOT NULL CHECK (pixel_height > 0),
    dpi_x integer NOT NULL CHECK (dpi_x > 0),
    dpi_y integer NOT NULL CHECK (dpi_y > 0),
    palette text NOT NULL CHECK (palette = 'Monochrome'),
    content bytea NOT NULL CHECK (octet_length(content) > 0 AND octet_length(content) <= 16777216),
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TRIGGER immutable_print_artifacts BEFORE UPDATE OR DELETE ON iz_print.artifacts
    FOR EACH ROW EXECUTE FUNCTION iz.prevent_change();

CREATE TABLE iz_print.jobs (
    id uuid PRIMARY KEY,
    command_id uuid NOT NULL UNIQUE,
    object_id uuid NOT NULL REFERENCES iz.objects,
    requested_by uuid NOT NULL REFERENCES iz.accounts,
    artifact_id uuid NOT NULL REFERENCES iz_print.artifacts,
    profile_id text NOT NULL,
    copies integer NOT NULL CHECK (copies > 0 AND copies <= 100),
    payload_kind text NOT NULL CHECK (payload_kind IN ('local', 'uuid')),
    payload text NOT NULL CHECK (length(payload) > 0),
    created_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE iz_print.attempts (
    id uuid PRIMARY KEY,
    job_id uuid NOT NULL REFERENCES iz_print.jobs,
    state text NOT NULL DEFAULT 'Created' CHECK (state IN (
        'Created', 'Claimed', 'Staged', 'Prepared', 'Dispatching', 'DriverAccepted',
        'SpoolerQueued', 'Printing', 'Blocked', 'Completed', 'Rejected', 'Failed', 'Unknown')),
    version bigint NOT NULL DEFAULT 1 CHECK (version > 0),
    claimed_by uuid REFERENCES iz.accounts,
    lease_id uuid,
    lease_expires_at timestamptz,
    dispatch_ack_id uuid NOT NULL UNIQUE,
    dispatch_started_at timestamptz,
    completion_evidence text CHECK (completion_evidence IN (
        'SpoolerConfirmed', 'BrotherMonitorConfirmed', 'DeviceConfirmed')),
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    UNIQUE(job_id, id),
    CHECK ((state = 'Completed') = (completion_evidence IS NOT NULL)),
    CHECK ((lease_id IS NULL AND lease_expires_at IS NULL AND claimed_by IS NULL)
        OR (lease_id IS NOT NULL AND lease_expires_at IS NOT NULL AND claimed_by IS NOT NULL))
);
CREATE INDEX print_attempt_claim_queue ON iz_print.attempts(created_at, id)
    WHERE state = 'Created';

CREATE TABLE iz_print.observations (
    observation_id uuid PRIMARY KEY,
    attempt_id uuid NOT NULL REFERENCES iz_print.attempts,
    source text NOT NULL,
    code text NOT NULL,
    detail text,
    raw_job_status bigint,
    raw_printer_status bigint,
    raw_provider_status integer,
    observed_at timestamptz NOT NULL,
    received_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX print_observations_attempt ON iz_print.observations(attempt_id, received_at);
CREATE TRIGGER immutable_print_observations BEFORE UPDATE OR DELETE ON iz_print.observations
    FOR EACH ROW EXECUTE FUNCTION iz.prevent_change();
