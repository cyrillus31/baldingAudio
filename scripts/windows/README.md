# Windows helper scripts

These scripts build the Windows app in-place and run it (no manual copying of the exe).

## Main workflow: build + run on Windows (repo already at X:\Projects)

If you already have the repo at `X:\Projects\baldingAudio`, run:

```powershell
.
\scripts\windows\build-and-run.bat demo-flood
```

## Also available: WSL/Linux bash wrapper

If your WSL has the Windows drive mounted at `/mnt/x`, run:

```bash
./scripts/windows/build-and-run.sh demo-flood
```

## Notes
- The build requires the .NET SDK on the Windows machine.
