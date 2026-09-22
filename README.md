# PEAK KirbyScream mod (dev notes)

BepInEx 5 plugin for PEAK. Source in `src/`, Thunderstore package files in `package/`.

## Build

```
dotnet build -c Release
```

Paths default to `D:\SteamLibrary\steamapps\common\PEAK` and the Thunderstore Mod Manager
`PEAK/profiles/Default` profile. Override with `-p:GameDir=... -p:ProfileDir=...`.

A Release build also:

1. copies the dll + `package/*` into the profile's `BepInEx/plugins/toiletking-KirbyScream/` for testing;
2. writes `dist/toiletking-KirbyScream-<version>.zip`, ready to upload to Thunderstore.

## Audio

Put the scream as `package/scream.ogg` (or `.wav` / `.mp3`) before building.
Anything in `package/` with those extensions is shipped and auto-detected at runtime.

## How detection works

`FallScreamController` polls `Character.localCharacter` every frame:

- **start** when moving down faster than `MinDownSpeed` and either the game's `CharacterMovement.FallTime()`
  (already discounts jumps and bounce pads) exceeds `MinFallTime`, or `data.fallSeconds > 0` (ragdoll fall);
- **stop** on `isGrounded`, `dead`, climbing anything, being carried, or parachute open;
- if airborne but no longer falling fast, it waits `StopGraceSeconds` before stopping.
