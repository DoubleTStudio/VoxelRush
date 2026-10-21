# Task: ECON-002 — Verified IAP and optional rewarded ads (after privacy review)

## Goal
Cosmetic purchases that can't be faked, shared, replayed or kept after a refund; rewarded ads that pay once per verified view, kept off until the privacy review. Acceptance (BACKLOG): duplicate/revoked purchase and reward replay tests.

## Prerequisites / relevant docs
ECON-001 (catalog, entitlement ledger), SAVE-006 (progression grants). MONETIZATION_ECONOMY (server-side verification, refunds/revocations, idempotent grants, verified ad completion, no ads in active races, compliance gates), skills/economy-monetization (no store secrets in Unity; privacy/age review before ads).

## In scope / out of scope
In:
- `AppleVerifier`: StoreKit 2 JWS checked against the pinned Apple Root CA - G3 (embedded, SHA-256 checked at start-up; overridable for tests). It requires Apple's marker OIDs on the leaf and intermediate, ES256 only, bundle id and allowed environments. App Store Server Notifications v2 are verified the same way.
- `GooglePlayVerifier`: Android Publisher `purchases.products.get` with a service-account JWT bearer token (cached). Purchased/cancelled/pending states are handled, license-tester purchases only where allowed, and purchases are acknowledged after granting.
- `PurchaseService`: store product → cosmetic SKU, grants through the ledger keyed per original purchase, revokes on refund. Endpoints: `POST /v1/players/me/purchases`, `/v1/store/apple/notifications`, `/v1/store/google/notifications?token=`.
- `AdRewardService`: AdMob SSV (ECDSA over the query string, key rotation, 24 h max age). It grants XP keyed by transaction id, with a daily cap, behind `Ads:Enabled=false`.
- Catalog store items now carry a `productId`, and validation requires it.
- `docs/operations/PURCHASES_ADS.md`.

Out (blocked): client store and ad SDK integration, store product setup, sandbox end-to-end, the privacy/age review itself.

## Tests (`backend/VoxelRush.Api.Tests/StoreTests.cs`)
- Apple: a verified purchase grants, and a restore with a new transaction id on the same original purchase is a **duplicate** (no new grant). The same receipt on another account gets `409 receipt_in_use`. A **revoked** transaction is refused and removes the earlier grant, and the old receipt can't be replayed. A refund notification revokes, and a forged notification is refused. Also refused: a chain to another root, a chain under the trusted root without Apple's marker OIDs (proved by a mutation run: removing the OID check fails this test), a payload swapped after signing, `alg: none`, another app's bundle, sandbox in production, garbage, and unknown products. The embedded Apple root loads and matches its pin.
- Google: a purchase is granted and acknowledged, and a **duplicate** token grants nothing. Cancelled → 409, pending → 202, license-tester → 400, unknown token → 400. A shared token on a second account is refused. A voided-purchase notification needs the push secret, revokes, and the token can't be replayed. During a store outage → 503 with no grant. The fake verifies the service-account assertion signature.
- Ads: a verified reward grants 25 XP once, and a **replay** is `Duplicate`. Refused: a wrong signing key, a reward redirected to another user after signing, a stale timestamp, an unknown key id, and malformed input. The daily cap stops the 4th reward with cap 3. Ads are off by default (404).

## Verification commands and actual results (2026-10-21)
| Command | Result |
|---|---|
| `dotnet test backend/VoxelRush.Api.Tests` | Passed 104/104 (twice) |
| gitleaks (CI image) over backend, tools, Unity assets, docs | no leaks |

## Definition of done
- [x] Duplicate/revoked purchase and reward replay tests.
- [ ] Privacy/age review, then client store and ad integration (blocked: store accounts and review owners).

## PR summary / risks / rollback
- Risks: Apple certificate revocation (OCSP) isn't checked, which matches common server practice but is noted. Google voided-purchase handling relies on a URL secret rather than Pub/Sub OIDC tokens. The AdMob `user_id` shares a pseudonymous player id with Google (privacy review item).
- Rollback: revert; store items fall back to service-granted only (ECON-001), and ads stay off.
