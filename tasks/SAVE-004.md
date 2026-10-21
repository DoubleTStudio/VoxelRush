# Task: SAVE-004 — World create/list/join/visibility/invite permissions

## Goal
Owners control who sees, joins and edits their worlds. Acceptance (BACKLOG): ACL and authorization tests.

## Prerequisites / relevant docs
VR-080 (worlds, members, join tickets), DES-002 (roles in game). SAVE_GAME_CLOUD_PERSISTENCE (private/friends/public; owner controls invitations and edit permissions; `POST /v1/worlds/{id}/invites` with expiration), ADR-018.

## In scope / out of scope
In: migration `003_world_invites.sql`; `InviteStore` (create: owner only, role visitor/editor, 1–168 h, 1–50 uses; accept: atomic use count, never downgrades a member, owners get 409; revoke all); `DELETE /v1/worlds/{id}` (soft delete, frees quota); `GET /v1/worlds/public` (discovery, paged by creation time).
Out: a friends graph (until it exists, "friends" worlds are visible to members only, like private), moderation of public worlds, restoring deleted worlds (SAVE-009), notifications.

## Rules
| | owner | editor member | visitor member | stranger |
|---|---|---|---|---|
| see / join (private, friends) | owner | editor | visitor | 404 |
| see / join (public) | owner | editor | visitor | visitor |
| rename/visibility, members, invites, delete | yes | 403 | 403 | 404 if not visible, else 403 |
Invalid, expired, revoked and used-up invite codes all answer 404 (no oracle). Codes are 32 random bytes, shown once, stored as SHA-256.

## Tests (`backend/VoxelRush.Api.Tests/AclTests.cs`, real PostgreSQL)
8-case matrix (visibility × caller × GET/join role/PATCH/invite/DELETE); invite grants its role and is used up; expiry, revocation and unknown codes; never downgrades; owners can't accept their own invite or create owner invites; out-of-range expiry rejected; codes not stored in plaintext; deleted worlds disappear from public lists, joins and "mine", and free the quota; discovery lists only public worlds.

## Verification commands and actual results (2026-10-21)
| Command | Result |
|---|---|
| `dotnet test backend/VoxelRush.Api.Tests` | Passed 59/59 |

## Definition of done
- [x] ACL and authorization tests.

## PR summary / risks / rollback
- Risks: soft-deleted worlds keep data until a retention job exists (SAVE-009).
- Rollback: revert; drop `world_invites`.
