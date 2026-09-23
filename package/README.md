# KirbyScream

Plays the **meme** Kirby falling scream (you know the one) as soon as you start falling in PEAK,
and cuts it the instant you hit the ground, die, grab a wall/rope/vine, or your parachute opens.

Instead of waiting for a high fall velocity, this hooks the game's own fall timer, so it fires early
(about 0.3 s into a real fall by default) instead of once you are already plummeting.

## Multiplayer

By default your scream is **mixed into your voice chat**, so everyone near you hears it coming
from you through the game's own proximity voice, **even players who do not have the mod**.
Distance, direction and muffling all work exactly like your real voice.

- It needs working voice chat. With no microphone, or before voice has connected, it falls back
  to `ModNetwork` for that scream.
- Push-to-talk players: your voice channel is opened for the fall only, with your real
  microphone muted, so only the scream goes out.
- It sounds like voice chat: mono and compressed, like someone yelling into their mic.
- On speakers, echo cancellation is paused while you scream so it does not erase the scream.
  Headphones avoid any echo.
- People without the mod cannot turn it off except by muting you. Be kind in public lobbies.

Set `BroadcastMode = ModNetwork` for the 1.1 behaviour: a network message that only players with
the mod hear, each using their own sound file. `Off` keeps the scream to yourself.

## Config (`BepInEx/config/toiletking.peak.kirbyscream.cfg`)

| Key | Default | What it does |
| --- | --- | --- |
| `AudioFile` | *(empty)* | File next to the dll to play. Empty = first `.ogg` / `.wav` / `.mp3` found. |
| `Volume` | 0.6 | Volume 0-1. |
| `Loop` | true | Loop until the fall ends. |
| `ResumeWindowSeconds` | 3 | Fall again within this many seconds and the scream continues where it stopped. 0 = always restart. |
| `UseGameSfxMixer` | true | Respect the in-game SFX volume slider. |
| `MinFallTime` | 0.3 | Seconds of free fall before screaming. Raise it if big jumps trigger it. |
| `MinDownSpeed` | 5 | Minimum downward speed (m/s). |
| `TriggerOnRagdollFall` | true | Also scream instantly when tripped / thrown / knocked off. |
| `BroadcastMode` | VoiceChat | `VoiceChat`, `ModNetwork` or `Off`. See Multiplayer above. |
| `VoiceChatVolume` | 0.5 | Loudness of the scream in your voice chat. Lower it if it clips. |
| `ForceTransmitWithPushToTalk` | true | Open the voice channel during a fall for push-to-talk users, real mic muted. |
| `PauseEchoCancellation` | true | Pause echo cancellation while screaming so the scream is not filtered out. |
| `HearOthers` | true | Play screams from players using `ModNetwork`, positioned on them. |
| `RemoteVolume` | 0.8 | Volume of `ModNetwork` screams before distance falloff. |
| `MaxHearingDistance` | 1000 | Far end of the falloff curve. Silent at roughly a fifth of this. |
| `FalloffNearDistance` | 10 | Full volume inside this radius. |
| `DopplerLevel` | 0 | Pitch shift from the faller's speed. 0 = off. |
| `NetworkEventCode` | 177 | Photon event code. Change only on a clash, and keep the lobby in sync. |
| `StopFadeSeconds` | 0.05 | Fade length on stop. 0 = hard cut. |
| `StopGraceSeconds` | 0.35 | Delay before stopping when airborne but no longer falling fast. |
| `StopWhenParachuteOpens` | true | |
| `StopWhenClimbing` | true | |
| `DebugLog` | false | Log start/stop reasons to the console. |

## Swap the sound

Drop any `.ogg`, `.wav` or `.mp3` into the mod folder and set `AudioFile` (or just leave a single file there).

Client-side only; other players do not need it.
