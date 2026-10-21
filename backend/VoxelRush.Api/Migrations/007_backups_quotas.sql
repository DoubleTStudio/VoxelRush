-- SAVE-009: point-in-time restore, retention and audit. A restore starts a new epoch whose index rows point at the
-- objects that were current at the chosen time; nothing is overwritten, so a restore can itself be undone.
ALTER TABLE worlds ADD COLUMN restore_epoch int NOT NULL DEFAULT 0 CHECK (restore_epoch >= 0);

ALTER TABLE chunk_snapshots DROP CONSTRAINT chunk_snapshots_pkey;
ALTER TABLE chunk_snapshots ADD COLUMN epoch int NOT NULL DEFAULT 0 CHECK (epoch >= 0);
ALTER TABLE chunk_snapshots ADD COLUMN committed_at timestamptz;
UPDATE chunk_snapshots SET committed_at = created_at WHERE status = 'committed';
ALTER TABLE chunk_snapshots ADD PRIMARY KEY (world_id, epoch, chunk_x, chunk_y, chunk_z, revision);
ALTER TABLE chunk_snapshots ADD CONSTRAINT chunk_snapshots_committed_at CHECK ((status = 'committed') = (committed_at IS NOT NULL));
-- Objects are shared between epochs; one is deleted only when no row references it.
CREATE INDEX chunk_snapshots_storage_key ON chunk_snapshots (storage_key);

CREATE TABLE world_restores (
    id         bigserial   PRIMARY KEY,
    world_id   uuid        NOT NULL REFERENCES worlds (id) ON DELETE CASCADE,
    actor_id   uuid        NOT NULL REFERENCES players (id),
    as_of      timestamptz NOT NULL,
    epoch      int         NOT NULL,
    chunks     int         NOT NULL,
    created_at timestamptz NOT NULL
);
CREATE INDEX world_restores_world ON world_restores (world_id, created_at);
