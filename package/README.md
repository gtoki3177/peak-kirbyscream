# KirbyScream

Plays the **meme** Kirby falling scream (you know the one) as soon as you start falling in PEAK,
and cuts it the instant you hit the ground, die, grab a wall/rope/vine, or your parachute opens.

Instead of waiting for a high fall velocity, this hooks the game's own fall timer, so it fires early
(about 0.3 s into a real fall by default) instead of once you are already plummeting.

## Multiplayer

Screams are shared over the network, so everyone hears a falling teammate from wherever they actually
are, using the same distance falloff as the game's proximity voice chat (audible out to roughly 200 m).

**Every player who wants to hear it needs the mod installed.** Players without it are unaffected and
hear nothing. Each listener plays their own copy of the sound file, so if a friend swapped in a
different clip, that is what they will hear.

## Config (`BepInEx/config/toiletking.peak.kirbyscream.cfg`)

| Key | Default | What it does |
| --- | --- | --- |
| `AudioFile` | *(empty)* | File next to the dll to play. Empty = first `.ogg` / `.wav` / `.mp3` found. |
| `Volume` | 0.6 | Volume 0-1. |
| `Loop` | true | Loop until the fall ends. |
| `UseGameSfxMixer` | true | Respect the in-game SFX volume slider. |
| `MinFallTime` | 0.3 | Seconds of free fall before screaming. Raise it if big jumps trigger it. |
| `MinDownSpeed` | 5 | Minimum downward speed (m/s). |
| `TriggerOnRagdollFall` | true | Also scream instantly when tripped / thrown / knocked off. |
| `ShareWithOthers` | true | Broadcast your screams so others hear them. |
| `HearOthers` | true | Play other players' screams, positioned on them. |
| `RemoteVolume` | 0.8 | Volume of other players' screams before distance falloff. |
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
