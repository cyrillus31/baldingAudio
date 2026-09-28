# Open problems

Reported by the user on 2026-09-28, from a session in **Bodycam** (borderless windowed,
overlay running, headphones).

All three have now been diagnosed against the log from that session
(`/mnt/c/Users/kirill/AppData/Roaming/baldingAudio/baldingAudio.log`, 1546 heartbeats
across three runs). Two are fixed; one needs a Windows setting.

Read this before proposing a display change. Two display designs have already been
rejected.

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
- **The `--selftest` suite is 21 checks** and runs headless on Linux: 13 DSP and display,
  6 Win32 interop, 2 capture handoff, all in seconds. Extend it rather than leaving a fix
  unverified — two of the three bugs above were only findable by adding to it.
