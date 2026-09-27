# Open problems

Reported by the user on 2026-09-28, from a session in **Bodycam** (borderless windowed,
overlay running, headphones). None of these are fixed. They are recorded here so they
survive the session, in the user's own framing where possible.

Read this before proposing a display change. Two display designs have already been
rejected, and problem 1 is a correctness bug that makes the overlay silently useless.

---

## 1. The overlay sometimes stops appearing over the game

> "the overlay sometimes disappears from the game, i have to go back to desktop and open
> the game again for it to appear."

**Severity: high.** This is worse than a cosmetic fault. The overlay is not visibly
broken — it is *absent*, and nothing on screen says why. A user who has not read this
document would reasonably conclude the app had crashed or the audio had stopped.

**What is not yet known.** Every part of the diagnosis. Specifically unanswered:

- Does the process stay alive and keep logging while the overlay is invisible?
- Do lines still exist in `EventTracker` at that moment, i.e. is this purely a windowing
  fault with the analysis still healthy?
- Does it correlate with the game gaining or losing focus, with the game minimising or
  restoring, with a resolution or display-mode change, or with the game switching to
  fullscreen?
- Is it the layered window losing its `UpdateLayeredWindow` backing, the topmost flag
  being dropped, the window being occluded, or the window being recreated and never
  re-shown?

**Where to look.** `src/BaldingAudio.App/Overlay/` — the layered topmost window and the
WinForms render loop. Two known traps in this area, both already hit once:

- `WS_POPUP` creates a window that is *hidden*. Without
  `ShowWindow(hwnd, SW_SHOWNOACTIVATE)` the overlay exists, has the right styles, and
  composites nothing. `IsWindowVisible` returning false was the only symptom then.
- `UpdateLayeredWindow` must be called on the thread that created the window. Do not
  move the render loop to a background thread.

**First thing to do:** capture a log while it is invisible. If the heartbeat keeps
ticking, the analysis is fine and this is entirely a windowing problem, which is the
cheap and likely case. If the heartbeat stops, the capture thread died and that is a
different bug entirely. Do not start fixing before knowing which.

---

## 2. The lines did not move — no sense of 3D

> "also there was no 3d, the lines did not move around the screen but were staying mostly
> at the center-top. maybe that's cuz i was not able to configure 7.1 in my headphonse
> device."

**Two candidate causes, and they must be told apart before anything is changed.**

**(a) The device never became 7.1.** The user could not find a 7.1 option for their
headphones. If the endpoint stayed stereo, then only the left/right axis is measurable
at all, and the front/back half of the display is a guess. This is the expected
behaviour on stereo, not a bug — see "the honest stereo limit" below.

**(b) Bearings collapsed to near zero.** "Staying mostly at the center-top" is the exact
signature of a bearing stuck at 0°, which is drawn at the top of the band. That was a
real bug, fixed in `5104192`: `AzimuthFromItd` bisected a one-sided model, so every
right-side sound was reported as 0°. **If this session ran before that build was
deployed, this is simply the old bug and it is already fixed.** If it ran after, then
(a) is the explanation and the bearings are genuinely small.

**Which is it?** Read the log from that session. `loudest <bearing> level <n>` gives the
measured bearing directly; a value sitting at 0° means (b), small but varying values
mean (a). The log also prints the channel layout in its first line — `stereo (2ch)` or
`7.1 (8ch)` settles it outright.

**The honest stereo limit.** A stereo mix carries exactly *one* left/right balance at
any instant, so the overlay can show one line that travels, not a compass of several
sources at once. That is a real limit of the signal, not of the code. Note the user
expects both sides at once (see `docs/next_step.md`); that is achievable in principle by
decomposing the two channels into virtual sources, but the decomposition is ambiguous —
scaling one source by *k* and another by 1/*k* leaves the mix bit-identical.

Setting Windows to 7.1 works with headphones, because Windows applies its own virtual
surround, and it makes this whole question moot. It has not been achieved on this
machine yet.

---

## 3. Lines need a fill and an outline, and dark lines are too dim

> "i think balck or darks lines is a bad idea cuz they are poorly visible. the bar should
> consist of at least two colors, fill + outline which have to be similar but slighly
> off. i don't need black bards with white outline but something similar"

**The requirement, as stated.** Each line is drawn in **two colours**:

- a **fill** and an **outline**
- the two must be **similar to each other but slightly offset** — close in hue, not one
  light colour on a dark one
- explicitly **not** black bars with a white outline. That is named as the thing to
  avoid, so near-black fills and near-white outlines are both wrong.
- the point is **visibility**. Dark or low-contrast lines disappear against a dark game
  scene, which is most of a shooter.

**How it fits the code.** `OverlayRenderer.DrawLines` currently makes one call to
`PixelBuffer.FillCapsule` per line, at a single colour and a single alpha, and lines are
deliberately flat — the border fade and bright tip cap were both removed in `2a9edda`
because both ends of a line now mean the same thing. A second colour is a different
matter: it is an outline around the same shape, not a gradient along it, so removing the
fade costs nothing here.

`PixelBuffer` will need either a stroke primitive or a second pass, since `FillCapsule`
fills rather than outlines. Widening the capsule by the outline width and drawing the
fill on top of it is the cheap version and avoids a new primitive.

**Still to decide, and worth asking rather than assuming:**

- Is the outline a fixed width in pixels, or proportional to line thickness?
- Does the outline replace the alpha fade entirely, or combine with it?
- The per-class colours in the user's `config.json` are already six distinct hues
  (footstep green, gunshot red, explosion orange, vehicle purple, voice grey, other
  white). Does "similar but slightly off" mean *within a class*, so a green footstep gets
  a darker green outline? That is the reading that fits the existing palette, but
  colour-by-pitch is also queued in `docs/next_step.md` and would change what the base
  colour means.

**This does not change the geometry.** Worth stating plainly, because two display
changes have already been rejected: this is a paint change on the existing side scales.
Front stays at top. Do not use this as a reason to revisit the layout.
