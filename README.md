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

## How the multiplayer side works

Only the falling player's own client decides when a scream starts and stops. It then broadcasts that
decision with `PhotonNetwork.RaiseEvent` (default code 177, payload `[viewId, isStart]`, reliable, to
Others). Receivers resolve the character with `Character.GetCharacterWithPhotonID` and hand it to a
`ScreamVoice`.

`ScreamVoice` owns one AudioSource per screaming character. Remote ones are spatial with a flat custom
rolloff curve, and volume is driven manually with the same formula `CharacterVoiceHandler` uses for
proximity voice, so the falloff matches in-game speech. The local player's own scream is 2D so it is
always clear. A remote voice also self-stops if the character lands, dies, or the scream runs past a
minute, which covers an owner disconnecting mid-fall before the stop event arrives.
