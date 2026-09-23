## 1.3.0
- If you start falling again within a few seconds of the scream stopping, it now continues from where
  it left off instead of starting over. Clipping a ledge mid-fall no longer restarts the scream.
  Set with `ResumeWindowSeconds` (default 3, 0 = always restart). Works for your own playback, the
  voice-chat scream, and ModNetwork screams from others.

## 1.2.1
- Fixed the scream sounding sped up and high-pitched in voice chat. It was converted to the voice
  encoder's sample rate, but it is mixed in before Photon resamples the microphone, so it now matches
  the microphone's own rate.

## 1.2.0
- Screams are now mixed into your voice chat by default, so everyone nearby hears them through the
  game's proximity voice, even players without the mod. New `BroadcastMode` setting picks
  `VoiceChat`, `ModNetwork` (the 1.1 behaviour) or `Off`, replacing `ShareWithOthers`.
- Push-to-talk players transmit the scream during a fall with their real microphone muted.
- Echo cancellation is paused while screaming so it does not remove the scream.
- Fixed your own scream going silent after the first scene change.
- Fixed a stop fade that kept restarting instead of finishing.

## 1.1.1
- Point website_url at the source repository. In 1.1.0 it pointed back at this Thunderstore page,
  which crashed the Thunderstore Mod Manager view when the Website button was clicked.

## 1.1.0
- Screams are now heard by everyone else running the mod, positioned on the falling player and
  attenuated with the same curve as the game's proximity voice chat.
- New Multiplayer config section: sharing, hearing, remote volume, hearing distance, Doppler, event code.

## 1.0.0
- Initial release: scream on fall start, stop on land / death / grab / parachute.
