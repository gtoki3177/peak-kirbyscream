# PEAK KirbyScream mod

A BepInEx plugin for [PEAK](https://store.steampowered.com/app/3527290/PEAK/) that plays a meme Kirby
falling scream from the moment you start falling until you land or die. The scream is mixed into your
voice chat, so everyone nearby hears it from you, with or without the mod.

**Thunderstore:** https://thunderstore.io/c/peak/p/toiletking/KirbyScream/

## Dev notes

BepInEx 5 plugin for PEAK. Source in `src/`, Thunderstore package files in `package/`.

## Build

```
dotnet build -c Release
```

Paths default to `D:\SteamLibrary\steamapps\common\PEAK` and the Thunderstore Mod Manager
`PEAK/profiles/Default` profile. Override with `-p:GameDir=... -p:ProfileDir=...`.

A Release build writes `dist/toiletking-KirbyScream-<version>.zip`, ready to upload to Thunderstore.

The mod itself is installed through Thunderstore Mod Manager like any other, so the normal loop is:
bump the version in `KirbyScream.csproj`, `package/manifest.json` and `src/Plugin.cs`, build, upload
the zip, then update the mod in the manager.

To try a change without publishing, deploy straight into the profile instead:

```
dotnet build -c Release -p:DeployToProfile=true
```

That overwrites the manager's copy of the mod folder, so the manager will keep showing whatever
version it last installed until you update it there again.

## Audio

Put the scream as `package/scream.ogg` (or `.wav` / `.mp3`) before building.
Anything in `package/` with those extensions is shipped and auto-detected at runtime.

## How detection works

`FallScreamController` polls `Character.localCharacter` every frame:

- **start** when moving down faster than `MinDownSpeed` and either the game's `CharacterMovement.FallTime()`
  (already discounts jumps and bounce pads) exceeds `MinFallTime`, or `data.fallSeconds > 0` (ragdoll fall);
- **stop** on `isGrounded`, `dead`, climbing anything, being carried, or parachute open;
- if airborne but no longer falling fast, it waits `StopGraceSeconds` before stopping.

## How voice-chat injection works (BroadcastMode = VoiceChat)

PEAK's voice chat is Photon Voice. `VoiceInjector` is added to the local player's `Recorder` object and
receives the Recorder's `PhotonVoiceCreated` message, or reads its private `voice` field if the stream
already exists. It installs a pre-processor (`FloatScreamProcessor` or `ShortScreamProcessor`,
matching the stream's sample type) on the `LocalVoiceAudio`.

Pre-processors run on Photon's audio thread before the level meter, voice detection, WebRTC DSP and
the Opus encoder. So the scream trips voice activation like real speech and is encoded as your voice.
`VoiceScreamMixer` holds the scream, pre-converted on the main thread to the stream's sample rate and
channel count, and mixes it in under a lock.

`PushData` drops everything while `TransmitEnabled` is false, which is the case for a push-to-talk
player not holding the key. A Harmony postfix on `CharacterVoiceHandler.PushToTalk` holds
transmission open during a scream. It sets `VoiceScreamMixer.MuteMic` from the game's own
`transmitting` decision first, so the real mic is only sent when the game would have sent it anyway.
The game's "not allowed to talk / blocked" check runs after the postfix and still wins. When the
scream ends, transmission is handed back before the mic is unmuted.

If the Recorder has a `WebRtcAudioDsp` with echo cancellation on, AEC is switched off for the
duration of the scream. Otherwise it can match the scream playing on the speakers and cancel it.

## How the mod-network path works (BroadcastMode = ModNetwork)

Only the falling player's own client decides when a scream starts and stops. It then broadcasts that
decision with `PhotonNetwork.RaiseEvent` (default code 177, payload `[viewId, isStart]`, reliable, to
Others). Receivers resolve the character with `Character.GetCharacterWithPhotonID` and hand it to a
`ScreamVoice`.

`ScreamVoice` owns one AudioSource per screaming character. Remote ones are spatial with a flat custom
rolloff curve, and volume is driven manually with the same formula `CharacterVoiceHandler` uses for
proximity voice, so the falloff matches in-game speech. The local player's own scream is 2D so it is
always clear. A remote voice also self-stops if the character lands, dies, or the scream runs past a
minute, which covers an owner disconnecting mid-fall before the stop event arrives.
