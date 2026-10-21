# Task: ACCT-001 — Link devices to one account from the phone

## Goal
A player can move their account (progression, worlds, cosmetics) to another phone or play on several, using the SAVE-006 link-code endpoints.

## Changes
- `PlayerApi.CreateLinkCodeAsync` (`POST /v1/auth/link-code`, authorized) and `RedeemLinkCodeAsync` (`POST /v1/auth/link`). Redeeming switches the device to the linked account and stores its refresh token in place of the device's guest token.
- Online panel: **Link a device** shows a short-lived code. A code field with **Use** redeems a code from another device, then reloads the world list.

## Not done
- No warning when redeeming would leave behind a guest that has progress; the guest account remains on the server.

## Verification
- EditMode 544/544 (new: code issued on one device signs the other into the same account; wrong or expired code message).

## Risks / rollback
Client-only. Rollback: revert.
