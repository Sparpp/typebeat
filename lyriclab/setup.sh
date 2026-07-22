#!/usr/bin/env bash
# One-time environment setup for the vendored lyriclab aligner (see README.md).
# POSIX counterpart of setup.ps1 (Linux/macOS). Creates .venv beside this script with the
# pinned dependency set. The game runs this automatically on first import when no environment
# is found; it can also be run by hand. torch 2.5.x pinned deliberately (2.6 breaks Demucs
# checkpoint loading); Python 3.11 pinned for wheel coverage.
set -euo pipefail

cd "$(dirname "$0")"

PY=".venv/bin/python"

if [ -x "$PY" ]; then
    echo 'lyriclab environment already present'
    exit 0
fi

if command -v uv >/dev/null 2>&1; then
    echo 'creating venv with uv...'
    uv venv .venv --python 3.11
    echo 'installing torch (cpu) — this is the big download...'
    uv pip install --python "$PY" --index-url https://download.pytorch.org/whl/cpu torch==2.5.1 torchaudio==2.5.1
    echo 'installing aligner dependencies...'
    uv pip install --python "$PY" demucs==4.0.1 soundfile pyphen num2words tqdm
else
    echo 'uv not found — using python venv + pip'
    if command -v python3.11 >/dev/null 2>&1; then
        python3.11 -m venv .venv
    else
        python3 -m venv .venv
    fi
    "$PY" -m pip install --upgrade pip
    echo 'installing torch (cpu) — this is the big download...'
    "$PY" -m pip install --index-url https://download.pytorch.org/whl/cpu torch==2.5.1 torchaudio==2.5.1
    echo 'installing aligner dependencies...'
    "$PY" -m pip install demucs==4.0.1 soundfile pyphen num2words tqdm
fi

if [ ! -x "$PY" ]; then
    echo 'venv creation failed' >&2
    exit 1
fi

echo 'lyriclab environment ready'
