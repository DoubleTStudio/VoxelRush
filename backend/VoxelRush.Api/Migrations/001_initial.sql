-- VR-080: accounts, refresh tokens, worlds/ACL and the chunk snapshot index (SAVE_GAME_CLOUD_PERSISTENCE data models).
CREATE TABLE players (
    id           uuid PRIMARY KEY,
    kind         text        NOT NULL CHECK (kind IN ('guest')),
    created_at   timestamptz NOT NULL DEFAULT now(),
    last_seen_at timestamptz NOT NULL DEFAULT now()
);

-- Opaque rotating refresh tokens, stored as SHA-256 hashes only. A family is one login; reusing a rotated token
-- revokes the whole family (stolen-token detection).
CREATE TABLE refresh_tokens (
    token_hash  bytea       PRIMARY KEY,
    player_id   uuid        NOT NULL REFERENCES players (id) ON DELETE CASCADE,
    family_id   uuid        NOT NULL,
    expires_at  timestamptz NOT NULL,
    rotated_at  timestamptz,
    revoked_at  timestamptz,
    created_at  timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX refresh_tokens_family ON refresh_tokens (family_id);

CREATE TABLE worlds (
    id                uuid        PRIMARY KEY,
    owner_id          uuid        NOT NULL REFERENCES players (id),
    name              text        NOT NULL CHECK (char_length(name) BETWEEN 1 AND 40),
    seed              bigint      NOT NULL,
    generator_version int         NOT NULL,
    schema_version    int         NOT NULL,
    visibility        text        NOT NULL CHECK (visibility IN ('private', 'friends', 'public')),
    status            text        NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'deleted')),
    created_at        timestamptz NOT NULL DEFAULT now(),
    last_saved_at     timestamptz,
    revision          bigint      NOT NULL DEFAULT 0
);
CREATE INDEX worlds_owner ON worlds (owner_id) WHERE status = 'active';

CREATE TABLE world_members (
    world_id   uuid        NOT NULL REFERENCES worlds (id) ON DELETE CASCADE,
    player_id  uuid        NOT NULL REFERENCES players (id) ON DELETE CASCADE,
    role       text        NOT NULL CHECK (role IN ('visitor', 'editor')),
    invited_by uuid        NOT NULL REFERENCES players (id),
    expires_at timestamptz,
    PRIMARY KEY (world_id, player_id)
);

CREATE TABLE chunk_snapshots (
    world_id    uuid        NOT NULL REFERENCES worlds (id) ON DELETE CASCADE,
    chunk_x     int         NOT NULL,
    chunk_y     int         NOT NULL,
    chunk_z     int         NOT NULL,
    revision    bigint      NOT NULL CHECK (revision >= 0),
    storage_key text        NOT NULL,
    sha256      bytea       NOT NULL CHECK (octet_length(sha256) = 32),
    size_bytes  int         NOT NULL CHECK (size_bytes > 0),
    status      text        NOT NULL CHECK (status IN ('pending', 'committed')),
    created_at  timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (world_id, chunk_x, chunk_y, chunk_z, revision)
);
