# ADR-018: Metadata API, guest accounts and signed join tickets

Status: Accepted (VR-080). Implements SYSTEM (API ownership), PERSISTENCE_SECURITY (controls), SAVE_GAME_CLOUD_PERSISTENCE (storage split, API sketch). Supersedes ADR-017's self-chosen session ids on servers that require tickets.

## Decision
- **API**: ASP.NET Core (.NET 10) minimal APIs in `backend/VoxelRush.Api`, PostgreSQL via Npgsql with forward-only embedded SQL migrations (advisory-locked), one container (`docker/api`), non-root and read-only.
- **Accounts**: guest accounts first. Access tokens are HS256 JWTs (15 min); refresh tokens are 32 random bytes, stored only as SHA-256, rotated on every use; reuse of a rotated token revokes the whole login family. Account linking (Apple/Google) comes later.
- **Worlds**: owner, members (visitor/editor), visibility private/friends/public, per-owner quota. Worlds a caller can't see are reported as not found (no existence leaks); ownership is checked before payload validation.
- **Join tickets**: 98-byte binary ticket (ticket id nonce, player, world, role, issued/expires, HMAC-SHA256), 60 s lifetime, signed with a key shared only between API and game servers. The codec (`Core/Networking/JoinTicket.cs`) is compiled into both. A server started with `-worldId` (key from `VOXELRUSH_TICKET_KEY`) admits only valid tickets for its world, remembers ticket ids until expiry (replay), keys sessions by the ticket's player id and takes the player's role from the ticket (zones/roles of DES-002 then decide edits). Protocol 3 payload: version, kind (session|ticket), body.
- **Snapshots**: S3-compatible store (Cloudflare R2 in production; any S3 API in tests) indexed in PostgreSQL. Game servers get presigned PUT/GET URLs from internal endpoints authenticated with a service token, so storage credentials never leave the API. An index row becomes `committed` only after size and SHA-256 of the stored object are verified; committed snapshots are immutable; keys are built from GUIDs and integers only.
- Rate limits per IP on auth and per player on world endpoints; 16 KiB request bodies.

## Alternatives
- Asymmetric ticket signatures (Ed25519/ECDSA): game servers wouldn't need a secret, but availability/behaviour of those primitives in Unity's Mono/IL2CPP needs verification; revisit when servers run on third-party hosts.
- EF Core: heavier tooling for a handful of tables; plain SQL keeps migrations reviewable.
- Letting servers write to storage directly: puts long-lived storage credentials on every game server.

## Consequences / risks
- The ticket key is a shared secret: rotate by running both keys during a transition (not implemented yet).
- Development servers (no `-worldId`) still accept self-chosen sessions as editors.

## Migration / rollback
Protocol 3 (old clients refused with `protocol_mismatch`). Database: migration 001 creates new tables only. Rollback: revert; drop the database.
