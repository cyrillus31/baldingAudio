# AGENTS.md

Working notes for this repository. Read this before changing anything.

## What this is

A Windows overlay that shows *where* a sound came from, as short lines on the border of
the screen. It exists because the user is deaf in one ear and needs to catch footsteps
and gunfire in competitive shooters, mainly Battlefield 6, without being banned.

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

A compass drawn on the border of the screen.

- A cue's **azimuth** picks a point on the border: 0° = top, +90° = right, 180° =
  bottom, −90° = left. In between lands in between.
- A line runs **inward from the border toward the centre**.
- **Length = loudness**, capped at 1/8 of screen width.
- **Thickness is constant**, so it reads as a line.
- **Nothing is drawn when nothing is sounding.**
- All cues are **red**. Colour carries no information yet, by decision.

On a 16:9 screen the corners are reached near **150°**, not 135°, so rear flankers land
in the bottom-left and bottom-right corners. That is intentional, not a bug.

The user has watched this run and rejected the first design. Trust the current one over
anything written earlier. `docs/next_step.md` has the reasoning.

## Build and test

```bash
export PATH="$HOME/.dotnet:$PATH"

# Build the platform-agnostic core, on Linux.
dotnet build src/BaldingAudio.Core/BaldingAudio.Core.csproj -c Release

# Self-test: renders synthetic 7.1 audio and checks the reported direction,
# plus the layout and the moving-line behaviour. Nine checks.
dotnet run --project src/BaldingAudio.App/BaldingAudio.App.csproj -- --selftest

# Publish the Windows app, also from Linux.
dotnet publish src/BaldingAudio.App/BaldingAudio.App.csproj \
  -c Release -r win-x64 --self-contained true -o artifacts/win-x64
```

There is no xunit test project yet. The self-test in
`src/BaldingAudio.Core/Diagnostics/SelfTest.cs` is the test suite for now, and it runs
headless on Linux. It is the fastest way to check a change, so extend it rather than
leaving a fix unverified.

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
- `--demo-flood` every cue parked on screen at once, so the whole compass is visible
- `--selftest` run the checks and exit

## Gotchas found the hard way

- **`Biquad.Configure` must divide by `a0`.** Omitting it puts the poles outside the
  unit circle at low frequencies and the filter self-oscillates to `NaN` within a few
  thousand samples. This silently killed every direction estimate at one point.
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
