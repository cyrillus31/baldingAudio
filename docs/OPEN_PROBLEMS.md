# Open problems

Reported by the user on 2026-09-28, from a session in **Bodycam** (borderless windowed,
overlay running, headphones).

All three have now been diagnosed against the log from that session
(`/mnt/c/Users/kirill/AppData/Roaming/baldingAudio/baldingAudio.log`, 1546 heartbeats
across three runs). Two are fixed; one needs a Windows setting.

Read this before proposing a display change. Two display designs have already been
rejected.

---

## Third field round, 2026-09-28 — the display model itself was wrong

> "it works fine in terms that it shows on both sides and an overlay stays consistently on
> top of the game. we need to commit and push this version. but the problem is that it's
> just too much clutter. i see the waves on both left and right sides at the same time
> with identical intesities. i tried blowing up granades on the right side of me and did
> not see any difference."
>
> "if there's a granade that blows up in my face i imagine there won't be much of a
> difference in a souond between my left and right channles and therefore nothing or
> close to nothing should be reflected by the app on the overlay"

Both symptoms had one cause, and it was fault 5's fix. Drawing a cue with an unreadable
side on **both** edges at once was meant to be honest, and it was not: it throws the
side away and lights both edges at equal intensity for every sound the estimator is
unsure about. A grenade to the right and a grenade in your face became the same picture.
The user reported exactly that, and reported it as two separate problems.

### 7. A bar now means "off to one side, and this is how far off" — CHANGED

Not a new design so much as the honest reading of what a two-channel endpoint can
measure. The old model drew a *sound* and pointed at where it was. On stereo the only
reliable measurement is the difference in level between the ears, so the bar now shows
that difference and nothing else:

- **Which edge**: the sign of the balance between the channels.
- **Length**: how lopsided it is, from **zero**.
- **Below a small floor: nothing at all.** An even mix is in front of you.
- **Loudness moved to brightness**, so length and alpha answer different questions.
- Measured **per frequency band**, so a right-panned footstep shows through balanced
  music. Measuring the whole mix averages the two together and shows nothing.

**The cost, stated plainly:** a sound dead ahead or directly behind now draws nothing,
because neither has a left/right difference. A gunshot in your face is invisible. The
user asked for this explicitly and it is the correct behaviour for the measurement
being made, but it is a real loss and not a rounding error.

**The trade that was refused:** the user considered remapping the height axis to lateral
angle so a stereo endpoint would use the full band. They rejected it. The height axis is
untouched, "front at top" still holds, and the geometry is unchanged.

### 8. Clutter was measured, not guessed

> "i havent noticed any intesity increased at the granade blow up cuz there was alwasy
> clutter even when it was relatively silent"

The display floor is −72 dBFS. Over 6415 heartbeats from the field log:

| Floor | Heartbeats with sound above it |
| --- | --- |
| −72 dBFS (current) | **97.8%** |
| −50 dBFS | 78.6% |
| −45 dBFS | 62.5% |
| −35 dBFS | 46.9% |
| −30 dBFS | 32.5% |

So the level gate was admitting almost everything and filtering essentially nothing.
Median event peak is −37 dBFS and the adaptive floor sits at −90 for most of a session,
which means the floor is not tracking and a relative gate is not available either.

**This is deliberately not changed in the same commit as fault 7.** Raising the level
gate is the obvious lever and the wrong first move: it trades directly against the
original complaint that quiet distant sounds did not show. The balance gate added in
fault 7 is the principled filter — it removes clutter that is *balanced* (music, ambient,
anything in front of you) without touching the level at all, which is why it should be
measured in a real game first.

`Style.BalanceFloor` and `Tuning.DisplayFloorDb` are both live in
`%APPDATA%\baldingAudio\config.json`, so neither needs a rebuild to tune.

---

## Second field round, 2026-09-28 — three more faults, all fixed

The three problems above were closed by reasoning about the log, and three of the
conclusions were wrong. A second session with the running binary on the user's machine
found the real ones. **Read this before trusting any of the diagnoses above**, and read
it as the argument for watching a running program rather than reading its output.

### 4. A loud sound could draw nothing at all — FIXED

> "when i play music it just shows on the left side"

and, from the same session, gunfire and explosions "were not shown at all".

**The spectrum was loud and the screen was blank, and nothing in the code connected the
two.** Over one stretch of play the direction spectrum measured 0.4–0.8 on **109 of 119**
one-second heartbeats, and a line was drawn on **27**. `EventTracker.Follow` could only
*update* tracks that already existed; a track was only ever created by an onset. Onsets
fired 7 times in two minutes. So a continuous sound — music, a sustained burst of
firearms — fired one onset, kept sounding, and drew nothing for as long as it played.

Worse, `Follow` searched only ±30° around a line while the stereo bearing moves tens of
degrees between frames (the log shows `-8, 8, -52, 52, 128`). A line that did exist lost
its source and was retired in 0.45 s.

The fix: the sound field can now *start* a line, not only sustain one. Two thresholds,
because they are different decisions — `FollowThreshold` (0.06) to keep a line that is
already on screen, `SpectrumStartThreshold` (0.18) to put a new one there, and
`StartWindowDegrees` (75) to decide whether a loud bearing and a line are the same source.
The wide window is not a fudge; the narrow one is what stranded lines that were plainly
still sounding.

### 5. A centred source was assigned a side at random — FIXED

> "if music plays in both headphonse you somewhat randomly decide where to show it on left
> or on the right, but that should not happen. if you don't know where to show the sound
> show it on both side for for now."

`StereoItd` only admitted "I don't know" when the lateral angle was under 5° **and** the
level difference under 3 dB. Real music sits just outside that box with whichever sign
the noise favoured, and the display turns the sign of the bearing into "start at the left
edge" or "start at the right edge". So the same centred sound landed on one edge at
random, which is what "always on the left" is when the noise is biased.

Now the estimator says so: a lateral angle under `AmbiguousLateralDegrees` (25°) is
reported as having **no side**, and `OverlayLayout` draws such a cue from **both** edges at
the same height. Negating the azimuth mirrors the edge and leaves `|azimuth|` alone, so
the pair cannot be read as two separate bearings. A source genuinely off to one side is
unaffected, and multichannel never mirrors at all — every speaker there really does have a
side.

This is honest rather than clever: the distance from ahead is still shown, and the one
thing the signal cannot tell us is simply not claimed.

### 6. The overlay was pausing itself — FIXED

> "the overlay sometimes disappears from the game, i have to go back to desktop"

**This was the `Ctrl+Alt+B` hotkey firing, not a windowing fault.** The log from that
session shows four `paused`/`resumed` pairs at 12:23:44, 12:24:42, 12:24:45 and
12:24:48, then a final `paused` with no resume after it. Capture was healthy throughout:
502 heartbeats, zero re-opens, zero stalls, zero failures. Something in the game or in
peripheral software was sending that combination.

And pausing called `EventTracker.Clear`, so a pause wiped every line and was pixel-for-
pixel identical to a crash. That is why it read as a fault for so long.

Fixed in three parts: pause now **freezes** the display instead of clearing it, the status
line says `PAUSED`, and all four hotkeys moved to `Ctrl+Alt+Shift+…` — a three-modifier
chord is not a thing a game sends by accident. If one ever does collide again, the
overlay will hold still rather than vanish.

**Worth keeping from this round:** the topmost-window reasoning was right all along and was
never the problem. `WS_EX_TOPMOST` at creation, `SetWindowPos(HWND_TOPMOST, …)` re-asserted
every frame at 60 Hz, `WS_EX_TRANSPARENT`, `WS_EX_NOACTIVATE`. Checked before looking
anywhere else, and it held up.

---

## 1. The overlay sometimes stops appearing over the game — FIXED (`b5e998d`)

> "the overlay sometimes disappears from the game, i have to go back to desktop and open
> the game again for it to appear."

**This was not a windowing fault.** The documented test for it — "if the heartbeat keeps
ticking, the analysis is fine and this is entirely a windowing problem" — was structurally
invalid, because the heartbeat is driven by the UI timer and cannot see the capture thread
at all. It would have kept ticking just as happily with no audio behind it. That is
exactly the reasoning that left this undiagnosed.

**What the log shows.** At `01:32:50.342` the capture loop logged
`0x88890004` — `AUDCLNT_E_DEVICE_INVALIDATED` — and never ran again. Over the following
31 seconds the frame count went 18511 → 19728 while the event count stayed frozen at 11.
The capture thread had died; the process had not.

**Why the error was unreadable.** Three HRESULT constants were wrong.
`DEVICE_INVALIDATED` was written as `0x88890008`, which is `UNSUPPORTED_FORMAT`, and
`EXCLUSIVE_MODE_NOT_ALLOWED` as `0x8889001A`, which is not an AUDCLNT code at all. The old
self-check passed regardless, because it compared the declarations against a table
holding the same wrong values — transcription checked against transcription.

**The fix, in three parts:**

- The capture loop classifies the HRESULT and re-opens the endpoint with a backoff instead
  of treating every exception as fatal. It also detects the quieter failure of a stream
  that stays open and stops delivering packets, which is the same blank overlay.
- The heartbeat now names the capture state on every line and warns once on a transition
  into failure, so a dead capture thread is visible from the log.
- The HRESULTs are stored as the code numbers `audioclient.h` actually writes and the
  values are derived, so there is only one transcription to get wrong. A new check asserts
  the recoverable set, including the one that occurred.

---

## 2. The lines did not move — cause (a), needs a Windows setting

> "also there was no 3d, the lines did not move around the screen but were staying mostly
> at the center-top. maybe that's cuz i was not able to configure 7.1 in my headphonse
> device."

**The log's first line settles it: `capture started: stereo (2ch), 44100 Hz`.** The
endpoint never became 7.1, so only the left/right axis is measurable and the front/back
half of the display is a guess. Not cause (b) — the old ITD bug fixed in `5104192` is not
what happened here.

**No code change can fix this.** The device's channel count is the only honest source of
how many channels can be measured, and asking a stereo-configured device for 7.1 fails
with `AUDCLNT_E_DEVICE_INVALIDATED`. The user needs to set Windows Sound → Playback →
Properties → Advanced → Spatial Sound to 7.1. That works with headphones, because Windows
applies its own virtual surround, and it makes this question moot.

**A second, separate fault was found and fixed while investigating this (`772df1f`).** The
log showed 69.5% of heartbeats reporting `loudest silent` against a median peak of
**-28 dBFS** — with events still being emitted. The audio was there; the spectrum was
not arriving. `SpectrumExchange` swapped its buffers in the wrong order and published a
stale zero-filled buffer on alternating frames, so the UI saw silence roughly half the
time. That is very likely why the lines appeared not to move: the tracker had no live
spectrum to follow a bearing with, so lines sat where their first event landed. **This
fix may resolve much of problem 2 on its own, and it is worth re-testing in-game before
concluding that 7.1 is required.**

**The honest stereo limit, if 7.1 is not set.** A stereo mix carries exactly one
left/right balance at any instant, so the overlay can show one line that travels, not a
compass of several sources at once. That is a limit of the signal, not of the code. The
user expects both sides at once (see `docs/next_step.md`); that is achievable in
principle by decomposing the two channels into virtual sources, but the decomposition is
ambiguous — scaling one source by *k* and another by 1/*k* leaves the mix bit-identical.

---

## 3. Lines need a fill and an outline — FIXED (`eb9e51b`)

> "i think balck or darks lines is a bad idea cuz they are poorly visible. the bar should
> consist of at least two colors, fill + outline which have to be similar but slighly
> off. i don't need black bards with white outline but something similar"

**Done, and it turned up a real rendering bug on the way.**

Each line is now drawn twice: the same capsule shape at a slightly larger radius in a
lighter version of its own colour, then the fill over it. The rim is the fill interpolated
toward white, so it keeps the hue — a red line gets a pink-red rim, not a white frame.
Two similar colours, as asked, and explicitly not the black-bar-with-white-outline that
was named as the thing to avoid.

The answers to the open questions in the previous revision of this document:

- **Outline width** is proportional to line thickness (`OutlineWidthFraction`, 0.55), so it
  scales with resolution rather than vanishing on a small screen or dominating a large one.
- **It replaces the alpha fade entirely** rather than combining with it. The fade was
  already removed in `2a9edda`, because both ends of a line mean the same thing now and a
  ramp between them would imply a direction that is not there.
- **"Similar but slightly off" is applied within a class**, so each of the user's six
  per-class colours gets an outline derived from itself. That fits the existing palette
  and leaves colour-by-pitch (`docs/next_step.md`) free to change what the base colour
  means later.

**The geometry is untouched.** Front stays at top. The only layout change is that the band
radius now accounts for the outline when it checks the margin from the top and bottom
edges, because the promise is about painted pixels and the rim extends past the fill.

**The bug found on the way.** `PixelBuffer.Blend` hard-set the destination alpha to `0xFF`
while writing colour premultiplied for a much lower alpha. Every anti-aliased edge pixel
therefore claimed to be fully opaque with colour dimmed for a coverage it did not have,
and GDI composited that as a solid dark ring — the exact halo that premultiplication
exists to prevent. A line's own alpha had no effect on how opaque it looked. The `over`
operator needs both halves:

```
ao = as + ab * (1 - as)
co = cs + cb * (1 - as)
```

---

## Worth knowing for next time

- **The display floor was left at -72 dBFS deliberately.** The log made it look like the
  threshold was too high, but the median peak was -28 dBFS — 44 dB above the floor. The
  spectrum was not below the floor, it was empty. Loosening the floor would have made
  noise visible instead of sounds.
- **A fault in the capture-to-UI handoff reports as a broken analyser.** That is how
  problem 2's real cause was misread as a DSP problem. The self-test now has a third
  group for it (`AppSelfCheck`) alongside the DSP checks and the Win32 checks, so this
  class of fault is covered rather than inferred from a log line.
- **The `--selftest` suite is 35 checks** and runs headless on Linux: 20 DSP and display,
  6 Win32 interop, 8 capture handoff and config, all in seconds. Extend it rather than leaving a fix
  unverified — three of the four bugs above were only findable by adding to it.
- **A field diagnosis is a hypothesis, not a finding.** Four of the diagnoses in this
  document were wrong on the first pass: the ITD correlation window, the display floor,
  the "loud but invisible" gate, and the vanishing overlay. In every case the log said
  something that supported the wrong answer, and running the program on the user's machine
  said something else. The `peak` field in a heartbeat is a trap in particular: it is
  sticky — written only when an event is emitted — so `peak -19 dBFS` beside
  `loudest silent` describes two different moments, not one contradictory reading.
- **Flags crossing a thread boundary need a check at the boundary.** The "side unknown"
  flag for fault 5 is set on the capture thread and read on the UI thread, and
  `Snapshot` rebuilds the spectrum from bins alone. The headless checks publish and read
  the same object, so they passed while the real overlay silently kept the old behaviour.
  The check now lives in `AppSelfCheck`, next to the exchange it protects.

---

## Third field round, 2026-09-28 — the sensitivity was set in the wrong unit

> "in a quire room we are still showing too much. we need to decrease sensibility and maybe
> provide even sliders for this setup... nevertheless i am thinking that sensibility should be
> toned down and the lowest visible different should be moved higher."

### 6. The side threshold was 0.87 dB, not 0.10 — FIXED

`BalanceFloor` was a raw balance, defaulted to `0.10`, and documented as "roughly 0.9 dB
between the ears". The number was right and the *scale* was wrong. The balance is an
**energy** ratio, because the analyser compares mean squares, so

    balance = (g - 1) / (g + 1)   for   g = 10^(dB/10)

and 0.10 is 0.87 dB. That is below the noise of a quiet room, which is precisely the
reported symptom. The amplitude form, which is what the default was written against, puts
3 dB at 0.17 rather than 0.33 — a factor of two in the sensitivity of the one number the
user tunes.

Now expressed as `BalanceFloorDb`, defaulting to **3 dB**, and calibrated against the
running app rather than against algebra:

| pan across the endpoint | reported balance |
| --- | --- |
| 1 dB | 0.11 |
| 2 dB | 0.23 |
| 3 dB | 0.33 |
| 4 dB | 0.43 |
| 6 dB | 0.60 |
| 9 dB | 0.78 |

Verified on the user's machine with panned test signals and the 3 dB floor in place: 1 dB
and 2 dB draw nothing, 3 dB and above draw exactly one line, always on the correct edge,
with no wrong-side reading in 40 samples.

The unit change alone is not the interesting part. The interesting part is what it exposed:

### 7. One setting had two names, and the file disagreed with the app — FIXED

`BalanceFloorDb` was added as a second property over the same value, and
`System.Text.Json` serialises both and applies them **in document order**. The config on
the user's machine held both:

```json
"balanceFloor": 0.1,
"balanceFloorDb": 0.871501757189002
```

so the app ran at **0.87 dB** while the file, the config UI and every reading of the raw
number said 0.1. Raising the default to 3 dB appeared to change nothing, and the reason
was this. Two names for one number is how a threshold ends up disagreeing with itself,
and the disagreement is invisible until someone tries to move the number.

`BalanceFloor` is now `[JsonIgnore]`d; only `BalanceFloorDb` reaches disk. A legacy
`balanceFloor` key is ignored — it only ever held 0.1, the value being replaced.

### 8. The log reported tracks, not lines — FIXED

The heartbeat's `lines` column was `_tracker.Visible.Count`, the number of events being
tracked. A track under the threshold is tracked and deliberately not drawn, so a centred
sound logged `lines 5` while the screen was empty. During the field round this was read as
evidence of clutter that did not exist. Now `5 drawn / 5 tracked`, and
`OverlayRenderer.LastLineCount` reports what was actually painted.

### Still open

**A settings UI.** The user asked for sliders, live tweaking, and a test facility that
plays gunshot-like sounds left and right over military background noise. Not built yet.
The threshold is currently only reachable by editing `config.json`, which is a worse way
to find a value you have to hear.

**`DisplayFloorDb` is still at −72 dBFS.** Measured earlier to admit 97.8% of field
heartbeats, so the level gate filters essentially nothing. Deliberately unchanged: raising
it trades directly against the original "quiet distant sounds don't show" complaint, and
the balance gate is the principled filter. Decide from a real game, not from the log.
