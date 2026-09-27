# Next step

A guide to where this is going, not a checklist. The user has changed the design once
already after watching it run, so treat everything here as a starting position to be
argued with, and update this file when reality disagrees.

## Where it actually is

Working, verified end to end on Windows:

- WASAPI loopback capture → per-band, per-channel energy → direction.
- Direction is correct on synthetic 7.1 audio: front-left reads exactly −60°, rear
  right 150°, front 0°.
- The overlay draws on a real Windows desktop, on top of a real application, and the
  drawn pixels were measured and matched against the expected geometry.
- Nine self-test checks pass, including the ones that encode the current design
  requirements.

Not yet done:

- **Never tested against real game audio.** Everything is synthetic or demo. The sound
  classifier in particular is tuned against guesses.
- **Never tested with real capture.** The WASAPI path has not been exercised against a
  live multichannel endpoint, because none was available on the test machine.
- No xunit project, no docs beyond this file and `AGENTS.md`.

## The most valuable next step

**Run it in Battlefield 6 and find out whether it is any use.** Everything below is
worth less than that one observation.

Setup for that:

1. Windows Sound → Playback → set the device to **7.1**. This is the single thing that
   most affects whether the overlay works at all. On stereo, left/right is real and
   front/back is not measurable — the app says so in its tray tooltip rather than
   pretending.
2. BF6 → Sound → **Sound System: 7.1 Surround**.
3. Run BF6 in **borderless windowed**, never exclusive fullscreen. Exclusive fullscreen
   hides overlays at the OS level, so the app will simply not appear and it will look
   like a bug.
4. Do **not** run MSI Afterburner/RivaTuner, Logitech G Hub, or a VPN at the same time.
   These are not banned by us and are not evidence of anything; they show up in
   accounts that reported false-positive bans, so there is no reason to run them.

Then observe, in this order:

- Does a footstep produce a line at roughly the right bearing? (bearing accuracy)
- Does a line appear and fall away, or does it stick? (envelope behaviour)
- When you turn, does the line travel around the border? (view-relative panning)
- Are the lines too subtle, or about right? (`MaxLengthFraction`, `LineThicknessFraction`)

Expect the first run to need tuning, and expect the tuning to be about levels rather
than about direction, because direction is already verified.

## If the game test goes well

Then, roughly in order of value:

1. **Multi-threading risk.** A burst of audio must not delay drawing, and drawing must
   not stall capture. Worth measuring, because a stall shows up as lines freezing at
   exactly the moment they matter.
2. **Own-sound suppression.** The user fires constantly, and their own gunfire is
   currently displayed as loudly as an enemy's. This is opt-in and off by default
   because own and enemy footsteps cannot be reliably told apart from the mix alone.
   Gunshots might be separable, since the player's own are centred and clipped.
3. **Classifier validation.** `SoundClass` is currently a guess from band ratios. The
   demo shows it mislabelling broadband transients as `Other`. Now that colour is
   uniform this is harmless, but it is what would drive per-class colour later.
4. **Device switching from the tray menu.** Currently a log line telling you to edit
   `config.json`. WASAPI can enumerate and open any endpoint, so this is easy.
5. **Multi-monitor.** `--monitor foreground` exists but is barely tested. Worth it if
   the user plays on more than one screen.

## If it goes badly

The most likely failure is direction being wrong because the audio path is not what we
assumed. Diagnose in this order, cheapest first:

1. Run `--selftest` on Windows. If it fails there but passes on Linux, the problem is
   the published binary, not the logic.
2. Check the tray tooltip. It names the layout and whether front/back is reliable. If
   the device is stereo, that is the answer.
3. Compare against the two prior-art projects (`VisualAudioOverlay`, `CanetisRadar2`).
   If they work and we do not on the same setup, we are doing something wrong in the
   channel mapping, not in the analysis.

## Deliberately not planned

- **Reading the game camera.** Banned. Covered in `AGENTS.md`.
- **Per-process loopback.** `VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK` would isolate one
  game from system audio, and it is the single highest-risk thing available here. It is
  a documented public API, but it is also the closest thing to "targeting the game",
  and it is not worth it for the benefit. Endpoint loopback is enough.
- **Any form of detection avoidance.** Javelin is a kernel anti-cheat. There is nothing
  to evade and pretending otherwise is how people get banned. The safety here comes from
  doing nothing suspicious, not from hiding.
- **In-overlay text.** GDI text into a premultiplied DIB is a mess, and it would put
  our own rendering on screen where a game anti-cheat is most likely to object to.
  Diagnostics go to the tray tooltip and the log file.

## A note on how this got here

The first design was built to a written spec, verified numerically, and was wrong. The
user watched it run and said, in effect, that the whole spatial metaphor was not what
they had in mind — they wanted something at the edges of the screen that pulses.

The lesson worth keeping: **a passing self-test proves the maths, not the idea.** Four of
the five original checks passed while the display was still fundamentally not the right
shape. Verify the thing the user will actually look at, on the machine they will look
at it on, before spending effort on it.
