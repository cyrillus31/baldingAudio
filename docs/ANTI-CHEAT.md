# Anti-cheat posture

This is the constraint that outranks every other design decision in this repository.
The user's goal is not "an overlay that works"; it is "an overlay that works and does
not get them banned from Battlefield 6".

## The threat

**EA Javelin** is kernel-level. It loads at boot, requires Secure Boot, and needs
TPM 2.0 for BF6. It is not a user-mode scanner that can be reasoned about as "it looks
for X" — it runs below the operating system.

That is why "we don't do anything detectable" is the wrong frame. **We are not trying to
be undetectable. We are trying to be un-interesting.** The safety comes from doing
nothing that a kernel anti-cheat has a reason to object to, not from hiding.

## What this project does

Only two things, both ordinary public APIs:

1. **WASAPI loopback capture** — reads the mix going to the audio endpoint. This is the
   same mechanism a microphone-level meter uses. It reads our own output device; it does
   not touch any other process.
2. **A topmost layered window** — a plain window, click-through, with
   `WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_NOACTIVATE |
   WS_EX_TOOLWINDOW`. It draws pixels over the desktop.

Plus a `RegisterHotKey` message-only window so the user can toggle it.

## What this project must never do

Each of these is a ban on its own, and none of them are needed to make the overlay
work:

| Never | Why |
| --- | --- |
| Kernel driver / `.sys` | Direct cause of bans. Never acceptable. |
| DLL injection, `SetWindowsHookEx` into a game, remote thread | Textbook. |
| Read another process's memory | Textbook. |
| Read the game camera or object positions | Textbook, **and unnecessary** — see below. |
| Synthetic input, macros, `SendInput` | Explicitly targeted by Javelin. |
| Obfuscation, packing, anti-debug, VM detection | Evidence of intent, and intent is what is judged. |
| Bypassing or spoofing anti-cheat | The worst possible outcome. |
| Per-process loopback | A documented API, but see below. |

## The camera question, settled

It looks like this overlay needs the player's facing direction to stay view-relative.
It does not.

**The game already pans audio into the player's frame of reference.** A sound at the
player's left comes out of their left speakers; as they turn, the panning slides toward
the front speakers. The captured endpoint mix is therefore view-relative *already*, and
turning the view moves the pan, which the overlay measures, which moves the line around
the screen border. No camera access is involved, and none is needed.

Every prior-art project in this space works the same way. `VisualAudioOverlay` says so
plainly: "A true 7.1 / 8-channel device enables full 360 degree detection. A stereo
device can only resolve left vs. right."

There is no public Windows API that reports another process's camera position anyway.
`ISpatialAudioClient` is an output sink — its nine methods contain no metadata
readback — and `IAudioClient3` has no spatial methods. If a future contributor proposes
reading the camera, the answer is that it is both a ban and unnecessary.

## The real risk: policy, not detection

The genuine exposure is not that Javelin can see the overlay. It is two other things.

**1. EA's User Agreement, §7.C, "Unauthorized Third-Party Program".** Three clauses
apply, and the third is the grey zone:

> (iii) "intercepts, 'mines', or otherwise collects information from or through the game"

We are not intercepting the game's data stream. We are reading the audio device. But
this clause is broad enough that a hostile reading is available, and the user has to
accept that risk consciously. It is the one thing no amount of careful engineering
removes.

**2. False positives on overlay-adjacent software.** EA states its bar as "effective
against cheaters without impacting legitimate play styles or accessibility tools", with
a claimed false-positive rate under 1%. Accounts that reported gameplay-enhancement
bans in late 2025 ran: **MSI Afterburner + RivaTuner, Logitech G Hub, a VPN, and XMP
changes.** No plain overlay was implicated in any of them. One user was unbanned on
appeal.

The practical consequence: **do not run MSI Afterburner, RivaTuner, Logitech G Hub, or
a VPN alongside this overlay.** They are not banned and they are not evidence of
anything. They simply appear in real ban reports, and there is no reason to run them
when the goal is not getting banned.

## Operational requirements

- **Borderless windowed, never exclusive fullscreen.** Exclusive fullscreen hides
  overlays at the OS level, so the app will not appear. This is a Windows compositor
  behaviour, not a bug in the app, and it means the app cannot be used in a
  fullscreen-only game. BF6 supports borderless windowed, so there is no trade-off.
- **Multichannel output.** 7.1 is required for front/back. On stereo the overlay still
  works and left/right is accurate, but front/back is not measurable. The app says this
  in its tray tooltip rather than pretending otherwise. On a 7.1 setup, front/back comes
  from comparing inter-channel level differences, which is why the layout needs a real
  multichannel endpoint and a virtual cable (VB-CABLE) if the headset is 7.1-only.
- **No in-overlay text.** Diagnostics go to the tray tooltip and a log file. Putting our
  own text rendering on screen over a game is more surface than the overlay needs.

## The honest summary

This application reads its own audio output and draws a few lines on the screen. There
is no version of that which is obviously hostile to an anti-cheat, and there is no
version which is a guaranteed pass either — EA's clause (iii) is broad, and a
false-positive ban is a real category of outcome even for software doing nothing wrong.

What can be done is done: no driver, no injection, no memory reads, no synthetic input,
no evasion, no per-process targeting, and no camera access. The remaining risk is a
policy judgement made by someone else, and the user should know that going in.
