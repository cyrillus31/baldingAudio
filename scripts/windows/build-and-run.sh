#!/usr/bin/env bash
set -euo pipefail

# WSL-friendly bash wrapper.
# Assumes the Windows repo is at X:\Projects\baldingAudio,
# which is typically mounted in WSL as /mnt/x/Projects/baldingAudio.

MODE="demo-flood"
PROJECT_ROOT="/mnt/x/Projects/baldingAudio"

usage() {
  echo "Usage: $0 [--mode demo-flood|demo|selftest]" >&2
  echo "         (or: $0 [demo-flood|demo|selftest])" >&2
}

if [[ $# -ge 1 ]]; then
  case "${1:-}" in
    demo-flood|demo|selftest)
      MODE="$1"; shift
      ;;
    --mode)
      MODE="${2:-}"; shift 2
      ;;
    -h|--help)
      usage; exit 0
      ;;
    *)
      usage; exit 2
      ;;
  esac
fi

if ! command -v dotnet >/dev/null 2>&1; then
  echo "dotnet not found. Install .NET SDK (dotnet) in this environment." >&2
  exit 1
fi

REPO="$PROJECT_ROOT"
if [[ ! -d "$REPO" ]]; then
  echo "Repo not found at: $REPO" >&2
  exit 1
fi

CSProj="$REPO/src/BaldingAudio.App/BaldingAudio.App.csproj"
OUT="$REPO/artifacts/win-x64"

echo "Publishing Windows x64 into $OUT ..."
dotnet publish "$CSProj" -c Release -r win-x64 --self-contained true -o "$OUT"

EXE="$OUT/baldingAudio.exe"
if [[ ! -f "$EXE" ]]; then
  echo "Build succeeded but exe not found: $EXE" >&2
  exit 1
fi

ARGS=()
case "$MODE" in
  demo) ARGS=(--demo) ;;
  demo-flood) ARGS=(--demo-flood) ;;
  selftest) ARGS=(--selftest) ;;
  *) echo "Unknown mode: $MODE" >&2; exit 2 ;;
esac

echo "Running: $EXE ${ARGS[*]}"
exec "$EXE" "${ARGS[@]}"
