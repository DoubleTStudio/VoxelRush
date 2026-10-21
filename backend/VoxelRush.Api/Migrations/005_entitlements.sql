-- ECON-001: cosmetic entitlement ledger. Backend-authoritative; one row per (source, transaction), revocable.
CREATE TABLE player_entitlements (
    player_id      uuid        NOT NULL REFERENCES players (id) ON DELETE CASCADE,
    sku            text        NOT NULL CHECK (char_length(sku) BETWEEN 1 AND 64),
    source         text        NOT NULL CHECK (source IN ('level', 'grant', 'purchase')),
    transaction_id text        NOT NULL CHECK (char_length(transaction_id) BETWEEN 1 AND 128),
    status         text        NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'revoked')),
    granted_at     timestamptz NOT NULL DEFAULT now(),
    revoked_at     timestamptz,
    PRIMARY KEY (source, transaction_id)
);
CREATE INDEX player_entitlements_player ON player_entitlements (player_id) WHERE status = 'active';
