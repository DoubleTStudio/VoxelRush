# Controls, accessibility and assets
## Driving landscape UI
Left steer buttons/virtual wheel, right gas/brake/boost, center build switch; speed/lap/checkpoints top. Multi-touch, safe areas, adjustable button size and sensitivity. Input System action map: Steer(float), Throttle(float), Brake(float), Boost(bool), Handbrake(bool), BuildMode(bool), Look(Vector2).
## Build mode
Orbit camera, block hotbar, ghost placement preview, rotate, place, remove, cancel. Ghost is local; server result is authoritative. Invalid placement feedback must explain reason.
## Accessibility
Color-blind safe checkpoint markers, scalable UI, vibration toggle, reduced particle setting, optional tilt controls later.
## Visual identity
Original bright low-poly/voxel materials; readable racing silhouettes; avoid copyrighted Minecraft textures/models. One biome initial: grassy hills, rocks, trees.
## Asset manifest contract
asset_id, source_path, export_path, license, author, attribution, texture_size, triangles, materials, collision_type, LODs, platform_status. Source in Blender/MagicaVoxel, exports FBX/GLB as compatible, Unity import settings version controlled.
## Minimum MVP assets
1 car + 3 skins, 12 block types, 3 ramps, checkpoint/boost pad, 10 decorations, engine/collision/build/destruction SFX, UI icons. Placeholders permitted; license provenance mandatory.
