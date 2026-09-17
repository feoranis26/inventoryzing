CREATE SCHEMA iz_scanner;

CREATE TABLE iz_scanner.terminals (
    id uuid PRIMARY KEY,
    site_id uuid NOT NULL REFERENCES iz.sites,
    browser_token_hash bytea NOT NULL UNIQUE CHECK (octet_length(browser_token_hash) = 32),
    display_name text NOT NULL CHECK (length(trim(display_name)) > 0),
    created_at timestamptz NOT NULL DEFAULT now(),
    last_seen_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE iz_scanner.agent_terminal_bindings (
    service_account_id uuid PRIMARY KEY REFERENCES iz.accounts,
    terminal_id uuid NOT NULL UNIQUE REFERENCES iz_scanner.terminals,
    bound_at timestamptz NOT NULL DEFAULT now()
);
