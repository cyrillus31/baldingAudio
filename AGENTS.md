# AGENTS.md

Working notes for this repository. Read this before changing anything.

## What this is

A Windows overlay that shows *where* a sound came from, as short horizontal lines running
inward from the left and right edges of the screen. It exists because the user is deaf in
one ear and needs to catch footsteps and gunfire in competitive shooters, mainly
Battlefield 6, without being banned.

The single most important constraint is **the second one**. Everything else in this file
is negotiable; that is not. See `docs/ANTI-CHEAT.md`.

## Hard constraints

1. **No ban risk from anything we build.** No kernel driver, no DLL injection, no
   reading another process's memory, no synthetic input, no obfuscation or
   anti-detection, no hooking. These are not style preferences — each one is a
   plausible route to an EA Javelin ban, and Javelin is kernel-level.
2. **Audio only.** Direction comes from measuring the audio mix. There is no public
   Windows API that reports another process's camera or object positions, and
   `ISpatialAudioClient` is an output sink with no metadata readback. Do not go looking
   for one; it does not exist.
3. **We do not read the game camera, and must not.** This is the subtle one. It looks
   like the overlay would need the player's yaw to stay view-relative. It does not: the
   game already pans audio into the player's frame, so the captured mix is
   view-relative for free. Turning the view moves the pan, the overlay measures the pan,
   and lines travel around the border by themselves. Any proposal to "improve" this by
   reading the camera is a ban, not an improvement.

## Architecture

Two projects, and the split is load-bearing:

- `src/BaldingAudio.Core` — `net8.0`, **no Windows reference at all**. DSP, direction
  estimation, the overlay layout, and the software rasteriser live here. It builds and
  its tests run on Linux, which is how we test without a game.
- `src/BaldingAudio.App` — `net8.0-windows`, WinForms. The only thing that touches Win32
  is WASAPI loopback capture, a layered topmost window, and a hotkey window.

The WASAPI and window declarations were moved out of Core specifically to keep that
claim true. If you add something to Core that needs `user32` or `mmdeviceapi`, it
belongs in App.

### Threads

| Thread | Does |
| --- | --- |
| capture | reads WASAPI packets, runs `SpatialAnalyzer` inline, publishes the spectrum |
| UI (WinForms timer) | owns the overlay window, drives `EventTracker`, draws, calls `Present()` |

Analysis runs on the capture thread so a burst of audio can never delay drawing.
Drawing runs on the UI thread because `UpdateLayeredWindow` belongs to the window that
created it. `SpectrumExchange` double-buffers the direction spectrum between them.

`UpdateLayeredWindow` must be called on the thread that created the window. Do not
"optimise" the render loop onto a background thread.

## The display model

Two side scales. Lines run **inward from the left and right edges**, and height on
screen means front/behind.

- The **sign of the azimuth** picks the edge: negative starts at the left edge and runs
  right, positive starts at the right edge and runs left. Exactly 0 (dead ahead) goes
  right, having no side of its own.
- **|azimuth| picks the height**: 0° = top of the band (straight ahead), 90° = middle
  (to the side), 180° = bottom (behind). Closer to the top is more in front, closer to
  the bottom is more behind.
- **Every line is horizontal.** Never anchored to the top or bottom edge, so nothing
  projects inward from those.
- **Length = loudness**, capped at 1/8 of screen width.
- **Thickness is constant**, so it reads as a line.
- **Nothing is drawn when nothing is sounding.**
- The band is inset by `FieldInsetFraction`, so the extremes sit clear of the top and
  bottom edges. On 2560×1440: front y=193, side y=720, behind y=1247.

Both halves of the direction survive. This encodes a bearing as (sign, |azimuth|) rather
than (sin, cos) — a different decomposition, not a coarser one.

The user has watched three designs run and rejected two. The current one is confirmed
good: "front at top feels right". Trust it over anything written earlier.

### Rejected designs, and why

Do not bring these back without asking.

1. **A compass on the screen border**, with radial lines reaching inward. Rejected: it
   put rear flankers in the bottom-left and bottom-right **corners**, which is exactly
   where the game's minimap and ammo counter live.
2. **A 2D field inside the screen**, lines floating around the middle, x = sin and
   y = cos. Rejected: "nothing is going on, i just see lines around the center of the
   screen. i don't like it. they should come from the edges inwards."

Corner clearance is a real concern the user raised and then deliberately deferred:
"forget about ui for now". `SideInsetFraction` and `FieldRadiusYFraction` exist so it
can be addressed as config, not a rewrite. The user does **not** want the band pulled
toward the middle for now.

## Open problems

**`docs/OPEN_PROBLEMS.md` is the current list. Read it before starting anything.** Three
are outstanding as of 2026-09-28, all reported by the user from a session in Bodycam:

1. **The overlay sometimes stops appearing over the game**, and has to be re-raised by
   alt-tabbing back to the game. Diagnosis not started. If the log keeps ticking while it
   is invisible, it is a windowing fault with the analysis healthy — likely, and the
   cheap case. Establish that before changing anything.
2. **No sense of 3D — the lines did not move.** Two candidate causes, and the log from
   that session distinguishes them in one line: bearings stuck at 0° means the old ITD
   bug (already fixed in `5104192`), small but varying bearings means the device never
   became 7.1. The first line of the log says `stereo (2ch)` or `7.1 (8ch)` outright.
3. **Lines need a fill plus an outline, in two similar colours, for visibility.** Dark
   lines disappear against a dark scene. Explicitly *not* black bars with a white
   outline. This is a paint change on the existing layout — not a reason to revisit the
   geometry, which has been rejected twice.

## Build and test

```bash
export PATH="$HOME/.dotnet:$PATH"

# Build the platform-agnostic core, on Linux.
dotnet build src/BaldingAudio.Core/BaldingAudio.Core.csproj -c Release

# Self-test: renders synthetic 7.1 audio and checks the reported direction,
# plus the layout, the moving-line behaviour and the capture handoff.
dotnet run --project src/BaldingAudio.App/BaldingAudio.App.csproj -- --selftest

# Publish the Windows app, also from Linux.
dotnet publish src/BaldingAudio.App/BaldingAudio.App.csproj \
  -c Release -r win-x64 --self-contained true -o artifacts/win-x64
```

There is no xunit test project yet. The self-test in
`src/BaldingAudio.Core/Diagnostics/SelfTest.cs` is the test suite for now, and it runs
headless on Linux. It is the fastest way to check a change, so extend it rather than
leaving a fix unverified.

`--selftest` runs three groups. The DSP and display checks come from Core. The Win32
interop checks are in `src/BaldingAudio.App/Audio/InteropSelfCheck.cs`, because the
Win32 declarations live in App and Core must stay free of them. The capture-handoff
checks are in `src/BaldingAudio.App/AppSelfCheck.cs`, covering the seam between the
capture thread and the UI thread, which is where a fault is reported as a broken
analyser. 31 checks total, and all of them run headless on Linux. The interop group
needs no audio device, so a wrong constant is caught in a second rather than on the
user's machine.

**A check that cannot fail is worse than no check.** Every fix here is verified by
reverting it and watching the check go red, and that has caught three real problems:
an outline check that passed with the outline removed, a handoff check that passed
against the old buffer swap, and an ITD check that passed on noise while the
estimator was wrong for every periodic sound. Choose the test signal to match the
bug, not the code.

## Running it on Windows

WSL2 interop runs Windows executables natively, so no Windows-side .NET SDK is needed.

```bash
cp artifacts/win-x64/baldingAudio.exe /mnt/c/temp/...
powershell.exe -NoProfile -Command "& 'C:\\temp\\...\\baldingAudio.exe' --demo-flood"
```

**A Windows exe must be copied off the WSL UNC path to `/mnt/c/...` before it will
run.** Running it from `\\wsl$\...` fails.

Display flags:

- `--demo` scripted cues, no audio needed
- `--demo-flood` every cue parked on screen at once, so the whole field is visible
- `--selftest` run the checks and exit

**`--demo` and `--demo-flood` bypass the audio pipeline entirely** - they inject events
directly and never run `SpatialAnalyzer`. They are for looking at geometry only. To
check that the overlay *reacts*, run with no flag and play something. Use `--demo-flood`
to judge layout, and no flag to judge behaviour; they will not tell you the same thing.

## Gotchas found the hard way

- **`Biquad.Configure` must divide by `a0`.** Omitting it puts the poles outside the
  unit circle at low frequencies and the filter self-oscillates to `NaN` within a few
  thousand samples. This silently killed every direction estimate at one point.
- **Never write a COM constant, IID or struct layout from memory.** Every fault that
  stopped capture from starting was one: two invented interface IIDs,
  `AUDCLNT_STREAMFLAGS_LOOPBACK` set to the `CROSSPROCESS` value, and a
  `WAVEFORMATEXTENSIBLE` with no `Pack`. None of them fail where you would expect. A
  wrong IID makes `IMMDevice.Activate` return `E_NOINTERFACE`, and the marshaller
  reports that as `InvalidCastException` — so the stack trace points at the marshaller
  and nowhere near the typo. A wrong flag gives `E_INVALIDARG`. A wrong layout returns
  channel counts like 32843. All of it is checked by `InteropSelfCheck`, which runs
  under `--selftest` on Linux; check the SDK header before changing any of it.
- **`GetDevicePeriod` is in 100-nanosecond units, not frames.** 101587 is a normal
  answer meaning 10.16 ms. Reading it as a frame count produces absurd periods.
- **Initialise with the endpoint's own mix format.** Asking a stereo device for 7.1
  fails with `AUDCLNT_E_DEVICE_INVALIDATED` (0x88890008). The device's channel count
  is also the only honest source of how many channels can be measured.
- **`WS_POPUP` creates a window hidden.** Without `ShowWindow(hwnd,
  SW_SHOWNOACTIVATE)` the overlay exists, has the right styles, and composites nothing.
  `IsWindowVisible` returning false was the only symptom.
- **PowerShell's `Add-Type` compiles C# 3.** No `var`, no string interpolation, no `out
  var`. For any real pixel analysis, dump **raw BGRA** from PowerShell and analyse it in
  Python — no image decoder needed.
- **A whole-screen diff is useless for verification** when the window underneath is
  alive. Diff *two frames of the same static scene* instead: the only thing that
  changes is the overlay, so the difference is exactly what the overlay drew.
- **`NotifyIcon.Text` throws above 63 characters.** Truncate it.

## Git

Work on `feature/overlay-v1` until the user has confirmed it in a real game. Commits use
`-c user.name="baldingAudio" -c user.email="dev@localhost"`. Push after each working
increment; this project is built in short unattended stretches and unpushed work has
been lost.
