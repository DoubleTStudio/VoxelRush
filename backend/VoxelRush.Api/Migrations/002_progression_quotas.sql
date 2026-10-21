-- SAVE-001: player progression, settings, idempotent reward ledger and per-owner world quotas
-- (SAVE_GAME_CLOUD_PERSISTENCE data models: PlayerProgress, WorldQuota; settings with version/ETag).
CREATE TABLE player_progress (
    player_id  uuid        PRIMARY KEY REFERENCES players (id) ON DELETE CASCADE,
    xp         bigint      NOT NULL DEFAULT 0 CHECK (xp >= 0),
    level      int         NOT NULL DEFAULT 1 CHECK (level >= 1),
    -- Optimistic concurrency: every change bumps it; writers send the version they read.
    version    bigint      NOT NULL DEFAULT 1 CHECK (version >= 1),
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE player_settings (
    player_id  uuid        PRIMARY KEY REFERENCES players (id) ON DELETE CASCADE,
    settings   jsonb       NOT NULL CHECK (octet_length(settings::text) <= 8192),
    version    bigint      NOT NULL DEFAULT 1 CHECK (version >= 1),
    updated_at timestamptz NOT NULL DEFAULT now()
);

-- One row per reward ever granted; the key (e.g. "race:<raceId>:<playerId>") makes grants idempotent.
CREATE TABLE reward_grants (
    grant_key  text        PRIMARY KEY CHECK (char_length(grant_key) BETWEEN 1 AND 128),
    player_id  uuid        NOT NULL REFERENCES players (id) ON DELETE CASCADE,
    kind       text        NOT NULL CHECK (kind IN ('xp')),
    amount     int         NOT NULL CHECK (amount > 0 AND amount <= 100000),
    source     text        NOT NULL CHECK (char_length(source) BETWEEN 1 AND 64),
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX reward_grants_player ON reward_grants (player_id, created_at);

-- Absent row = defaults from configuration.
CREATE TABLE world_quotas (
    owner_id              uuid   PRIMARY KEY REFERENCES players (id) ON DELETE CASCADE,
    max_worlds            int    NOT NULL CHECK (max_worlds BETWEEN 0 AND 100),
    max_stored_bytes      bigint NOT NULL CHECK (max_stored_bytes >= 0),
    idle_shutdown_seconds int    NOT NULL CHECK (idle_shutdown_seconds BETWEEN 30 AND 86400)
);
