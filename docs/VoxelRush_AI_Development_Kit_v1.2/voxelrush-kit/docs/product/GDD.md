# Game Design Document (GDD)
## Vision
VoxelRush: race, construct shortcuts, demolish obstacles and explore a shared procedural block world. Distinct original art; no Minecraft intellectual property or assets.
## Audience / platforms
Mobile players on Android/iOS, landscape, short 5–12 minute sessions plus longer sandbox play. Touch-first and optional gamepad later.
## Core loop
Join world → explore/drive → collect basic materials → construct ramps/roads/bridges → challenge race → race through checkpoints → earn cosmetic progression → improve route.
## Modes
- Free roam: editable sandbox, persisted after durable commit.
- Build & Race: race organizer defines course; authorized edits in preparation; snapshot track at race start.
- Checkpoint race: ordered gates and server validated crossings.
- Time trial: optional later.
- Demolition arena and team building: later.
## MVP scope
2–4 connected players first, then 8. One biome, one car archetype, 12 block types, 3 ramp variants, checkpoints, collision destruction, small explosions, lobby, reconnect, persistence.
## Player experience
Join as guest → select sandbox → spawn → tutorial drive → build first ramp → smash weak wall → invite player → start race. Tutorial requires no account creation for local prototype.
## Success metrics (initial hypotheses)
First playable in <3 minutes after install; 95% successful joins in test environment; no lost acknowledged edits; 30 FPS supported low tier; 30Hz server tick with 8 players.
## Design constraints
No pay-to-win; protected race infrastructure; permissioned edits; child-friendly art and default chat disabled in MVP.
## Out of scope
Unlimited structural collapse, real-time terraforming across infinite public shards, real-money economy, user-uploaded models.
## Open product decisions
World ownership/social model, material gathering depth, racing camera modes, monetization, public moderation policy. Defaults: private rooms + one shared test sandbox, no monetization in MVP.
