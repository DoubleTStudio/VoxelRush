-- SAVE-005: one authoritative server per world (ADR-010 single writer). The fencing number grows with every new
-- holder; writes carrying an older number are refused even if that server still thinks it holds the world.
CREATE TABLE world_leases (
    world_id   uuid        PRIMARY KEY REFERENCES worlds (id) ON DELETE CASCADE,
    holder     text        NOT NULL CHECK (char_length(holder) BETWEEN 1 AND 128),
    fencing    bigint      NOT NULL CHECK (fencing >= 1),
    expires_at timestamptz NOT NULL,
    released   boolean     NOT NULL DEFAULT false
);
