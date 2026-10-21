# Purchases and rewarded ads (ECON-002)

Everything sold is cosmetic (ECON-001 catalog). The backend decides what a player owns; the game only shows it.

## Purchase flow
1. The client buys a store product (`voxelrush.paint.chrome`, `voxelrush.flag.checkered_gold`; `productId` in `backend/VoxelRush.Api/catalog.json`) with StoreKit 2 or Google Play Billing.
2. The client sends the store's proof to `POST /v1/players/me/purchases`:
   - Apple: `{"platform":"apple","signedTransaction":"<JWS>"}`
   - Google: `{"platform":"google","productId":"…","purchaseToken":"…"}`
3. The API verifies it. Apple: the JWS chain must reach the pinned Apple Root CA - G3 and carry Apple's marker OIDs, signed ES256, then bundle id and environment are checked. Google: the Android Publisher API is called as the service account.
4. The API grants the SKU in the entitlement ledger under one id per original purchase (`apple:<originalTransactionId>`, `google:<orderId>`). It then acknowledges Google purchases (Google auto-refunds unacknowledged ones after 3 days).

| Response | Meaning |
|---|---|
| `200 {granted:true}` / `200 {granted:false}` | New grant / already owned (duplicate submit or restore on another device) |
| `202 pending` | Google pending payment; resubmit later |
| `409 purchase_revoked` | Refunded, cancelled or revoked; nothing is granted, and an earlier grant is revoked |
| `409 receipt_in_use` | The purchase belongs to another account |
| `400 invalid_receipt` / `404` | Unverifiable / not a store product we sell |
| `503 store_unavailable` / `501 store_disabled` | Google API down / Google not configured |

## Refunds
- **App Store Server Notifications v2** → `POST /v1/store/apple/notifications`. Verified like transactions; `REFUND` and `REVOKE` revoke the entitlement. Configure the production URL in App Store Connect.
- **Google real-time developer notifications** → Pub/Sub push subscription to `POST /v1/store/google/notifications?token=<Store:GooglePushToken>`. `voidedPurchaseNotification` revokes.
- A refunded receipt can't be resubmitted into a new grant.

## Configuration (environment, never in the game)
| Setting | Notes |
|---|---|
| `VoxelRush__Store__AppleBundleId` | `com.doubletstudio.voxelrush` |
| `VoxelRush__Store__AppleEnvironments__0` (`__1`…) | `Production`; add `Sandbox` only on development/TestFlight deployments |
| `VoxelRush__Store__GoogleServiceAccountJson` | Service account key with Android Publisher access (secret store) |
| `VoxelRush__Store__GooglePushToken` | Random secret for the push URL |
| `VoxelRush__Store__GoogleAllowTestPurchases` | `true` only for internal-testing backends |

## Rewarded ads — disabled until the privacy and age review
- `GET /v1/ads/admob/reward` is AdMob's server-side verification callback. The query string is signed with Google's published ECDSA keys (fetched from `Ads:AdMobKeysUrl`, refreshed on an unknown key id).
- The reward is `Ads:RewardXp` (25) progression XP, keyed by AdMob's `transaction_id` so retries and replays never pay twice, and capped at `Ads:DailyCap` (5) per player per day. Callbacks older than `Ads:MaxCallbackAgeHours` (24) are refused.
- Off unless `VoxelRush__Ads__Enabled=true`. **Launch gates before enabling:** privacy/age-rating review (child-directed settings, consent/ATT, regional rules), ad SDK data-safety declarations, and confirming the `user_id` sent to AdMob (the player's pseudonymous id) is acceptable. No ads in active races (client rule).

## Not done yet (blocked)
- Client store integration (Unity IAP / StoreKit 2 and Play Billing) and the AdMob SDK. These need App Store Connect and Play Console products plus the privacy review.
- Real-store end-to-end tests (sandbox accounts). The API tests use a self-made Apple-style PKI, a fake Android Publisher API and fake AdMob keys.
