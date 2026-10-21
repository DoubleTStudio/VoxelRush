# VR-071 device benchmark procedure

Status: harness ready; **no device results yet** (no Android/iOS device available in this environment).

## Run
Android (APK from `tools/build.sh android`, installed via `adb install -r build/android/VoxelRush.apk`):
```
adb shell am start -n com.doubletstudio.voxelrush/com.unity3d.player.UnityPlayerActivity -e unity "-benchmarkSeconds 300 -benchmarkWarmup 10"
# thermal soak with adaptation on (30 min):
adb shell am start -n com.doubletstudio.voxelrush/com.unity3d.player.UnityPlayerActivity -e unity "-benchmarkSeconds 1800"
# one tier, no adaptation, for the tier table:
adb shell am start -n com.doubletstudio.voxelrush/com.unity3d.player.UnityPlayerActivity -e unity "-benchmarkSeconds 300 -qualityTier Low"
adb logcat -s Unity | grep -E "event=(quality_tier|benchmark_result|quality_tier_changed)"
adb pull /sdcard/Android/data/com.doubletstudio.voxelrush/files/   # benchmark-*.json
```
iOS: run the Xcode project with the same arguments in the scheme; results land in the app's Documents container. Thermal headroom is not available on iOS yet (frame-time adaptation only).

## Record per device
Model, OS, GPU, memory, cores, tier classified and used, p50/p95/p99 frame ms, mean fps, % over budget, tier changes, thermal headroom start/end, GC collections, allocated MB, duration. QUALITY_GATES targets: low tier 30 fps, high tier 60 fps when sustainable.

## Results
| Device | Tier | Duration | p95 ms | Mean fps | Over budget | Thermal end | Tier changes |
|---|---|---|---|---|---|---|---|
| _none yet_ | | | | | | | |
