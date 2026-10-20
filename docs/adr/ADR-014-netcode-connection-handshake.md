# ADR-014: Netcode baseline, connection handshake and admission

Status: Accepted (VR-002). Implements ADR-008 (NGO + Unity Transport) for the first slice.

## Context
VR-002 needs clients to join an authoritative Linux dedicated server. CONTRACTS.md requires `protocolVersion` on the wire; GAMEPLAY_RULES/PERSISTENCE_SECURITY require room membership limits and rejecting malformed input.

## Decision
- **Packages:** `com.unity.netcode.gameobjects` **2.13.3**, the version shipped with editor 6000.3.26f1 (3.x exists but is not the editor's verified line); Unity Transport resolves to **2.7.4** via that dependency (locked in `packages-lock.json`).
- **Boundary:** NGO types stay inside `VoxelRush.Networking` (`ServerNetwork`, `ClientNetwork`); Server/Presentation never reference NGO directly.
- **Handshake:** NGO connection approval with payload = `protocolVersion` as 2 bytes little-endian (`ConnectionPayload`). Exact length only; anything else is `malformed_payload`.
- **Admission (`ConnectionGate`, engine-free):** rejects `malformed_payload`, `protocol_mismatch`, `server_full`. A slot is reserved at approval (so simultaneous approvals can't exceed `-maxPlayers`), released on disconnect, and expired if the handshake doesn't finish within 10 s. A client that completes after its slot expired is disconnected with `handshake_timeout`. Reason codes are stable strings sent to the client.
- **No implicit player objects:** `CreatePlayerObject = false`; vehicles spawn explicitly in VR-012.
- **Scene management off** (`EnableSceneManagement = false`): server and client run different scenes, and the world is streamed as chunks (M2), not NGO scene sync. NGO's default made clients wait for the server's `Server.unity`, which the client build doesn't contain.
- **Tick:** `NetworkConfig.TickRate = 30` (ADR-002 / SYSTEM.md).
- **Graceful client exit:** clients run NGO shutdown to completion (≤1 s) before quitting, so the server frees the slot immediately instead of after a transport timeout.
- **Headless clients are bots:** `Bot.unity` built with the Server subtarget, running the same `ClientBootstrap` as `Client.unity`. Linux desktop (Player subtarget) builds segfault without a window system even under `-nographics`.
- **Endpoints:** clients take `-connect host:port` (IPv4 or DNS name, resolved once at connect time; Unity Transport needs a literal IP). Mobile endpoints arrive with join tickets (VR-080).

## Alternatives
- NGO 3.x: newer API, but not the version Unity verified for this editor.
- Custom handshake message after connect: unauthenticated clients would already hold a connection and a slot.
- NGO scene sync: couples client and server scene lists; our world isn't scene-based.

## Consequences
- Changing the payload layout or `SimulationConstants.ProtocolVersion` is a protocol change: old clients get `protocol_mismatch`.
- Join tickets (VR-080) will extend the payload; the exact-length rule then becomes a versioned format.

## Migration / rollback
Revert the VR-002 change set; nothing is persisted.
