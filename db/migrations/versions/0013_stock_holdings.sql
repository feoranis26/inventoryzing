CREATE TABLE iz.stock_policies (
    type_id uuid PRIMARY KEY REFERENCES iz.object_types,
    quantity_dimension text NOT NULL CHECK (quantity_dimension IN ('count', 'volume', 'length', 'mass', 'area')),
    canonical_unit text NOT NULL,
    granularity numeric NOT NULL CHECK (granularity > 0),
    allow_negative boolean NOT NULL DEFAULT false
);

CREATE TABLE iz.stock_holdings (
    object_id uuid PRIMARY KEY REFERENCES iz.objects,
    quantity numeric NOT NULL,
    policy_type_id uuid NOT NULL REFERENCES iz.object_types
);
CREATE INDEX stock_holdings_policy ON iz.stock_holdings(policy_type_id);

CREATE TABLE iz.stock_movements (
    id uuid PRIMARY KEY,
    holding_id uuid NOT NULL REFERENCES iz.stock_holdings(object_id),
    event_id uuid NOT NULL REFERENCES iz.domain_events,
    operation text NOT NULL CHECK (operation IN ('initial', 'receive', 'consume', 'adjust', 'transfer_in', 'transfer_out')),
    delta numeric NOT NULL,
    balance_after numeric NOT NULL,
    reason text NOT NULL DEFAULT '',
    counterparty_holding_id uuid REFERENCES iz.stock_holdings(object_id),
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX stock_movements_holding ON iz.stock_movements(holding_id, created_at DESC);
