# Task: ECON-003 — Garage, unlocks and verified store purchases on the client

## Goal
Players can see the cosmetics catalog and their level, unlock level-gated items, buy store items, and equip a paint, with ownership decided by the API (ECON-001/002).

## Changes
- **PlayerApi:** `ProgressAsync`, `CatalogAsync`, `OwnedAsync`, `ClaimAsync`, and `SubmitPurchaseAsync`, which sends Apple's signed transaction, or Google's product id plus purchase token, to `/v1/players/me/purchases`. The verdict is Granted, AlreadyOwned, Pending, Rejected or Retry.
- **StoreClient** (Unity IAP 5.4.3, StoreKit 2 / Play Billing): fetches store products from the catalog. A store order is confirmed only after the API granted it. Unverified or failed orders stay pending in the store and are re-delivered on the next launch (`FetchPurchases`); they are never granted locally.
- **GarageMenu** (on the join screen once signed in): level and XP, and every catalog item as owned / Unlock / "Reach level N" / price. Owned paints can be equipped; the paint tints the player's own car body (MaterialPropertyBlock) and is remembered on the device.
- `ClientBootstrap.HideJoinPanels` keeps the join panels from drawing or taking clicks under the garage.

## Not done
- Other players don't see your paint yet: that needs the server to learn the equipped cosmetic and check ownership.
- Store products must be created in App Store Connect and Play Console (`voxelrush.paint.chrome`, `voxelrush.flag.checkered_gold`), and server store credentials configured, before purchases work. Until then the store shows "unavailable".
- Non-paint cosmetics (decal, trail, horn, flag) show as owned but have no visuals yet.

## Verification
- PlayMode 36/36 (new: garage item states, palette, scene wiring). EditMode 543/543.
- `tools/build.sh android --development` and `tools/build.sh server-mac` succeed with the IAP package.

## Risks / rollback
Adds the `com.unity.purchasing` package to the client (Presentation only; the server compiles Presentation out). Rollback: revert.
