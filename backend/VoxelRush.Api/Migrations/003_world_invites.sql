-- SAVE-004: owner-issued invitations (stored as SHA-256 of the code; the code itself is shown once).
CREATE TABLE world_invites (
    code_hash  bytea       PRIMARY KEY CHECK (octet_length(code_hash) = 32),
    world_id   uuid        NOT NULL REFERENCES worlds (id) ON DELETE CASCADE,
    role       text        NOT NULL CHECK (role IN ('visitor', 'editor')),
    created_by uuid        NOT NULL REFERENCES players (id),
    expires_at timestamptz NOT NULL,
    max_uses   int         NOT NULL CHECK (max_uses BETWEEN 1 AND 50),
    uses       int         NOT NULL DEFAULT 0 CHECK (uses >= 0),
    revoked_at timestamptz,
    created_at timestamptz NOT NULL DEFAULT now(),
    CHECK (uses <= max_uses)
);
CREATE INDEX world_invites_world ON world_invites (world_id);
