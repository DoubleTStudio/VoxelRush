-- SAVE-006: one-time codes that sign a second device into the same player (cross-device continuity).
CREATE TABLE device_link_codes (
    code_hash  bytea       PRIMARY KEY CHECK (octet_length(code_hash) = 32),
    player_id  uuid        NOT NULL REFERENCES players (id) ON DELETE CASCADE,
    expires_at timestamptz NOT NULL,
    used_at    timestamptz,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX device_link_codes_player ON device_link_codes (player_id);
