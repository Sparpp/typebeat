#!/usr/bin/env bash
# One-time environment setup for the vendored lyriclab aligner (see README.md).
# POSIX counterpart of setup.ps1 (Linux/macOS). Creates .venv beside this script with the
# pinned dependency set. torch 2.5.x pinned deliberately (2.6 breaks Demucs checkpoint
# loading); Python 3.11 pinned for wheel coverage.
#
# Usage: setup.sh [cpu|cuda]   (default cpu; cuda installs CUDA 12.1 torch wheels)
#
# ffmpeg: align_lyrics.py shells out to an `ffmpeg` on PATH. The imageio-ffmpeg wheel's
# bundled static build is linked into the venv's bin dir; the game prepends that dir to
# PATH when it runs the aligner.
set -euo pipefail

cd "$(dirname "$0")"

DEVICE="${1:-cpu}"
case "$DEVICE" in
    cpu)  TORCH_INDEX='https://download.pytorch.org/whl/cpu' ;;
    cuda) TORCH_INDEX='https://download.pytorch.org/whl/cu121' ;;
    *) echo "unknown device '$DEVICE' (expected cpu or cuda)" >&2; exit 1 ;;
esac

PY=".venv/bin/python"

if [ -x "$PY" ]; then
    echo 'lyriclab environment already present'
    exit 0
fi

if command -v uv >/dev/null 2>&1; then
    echo 'creating venv with uv...'
    uv venv .venv --python 3.11
    echo "installing torch ($DEVICE) — this is the big download..."
    uv pip install --python "$PY" --index-url "$TORCH_INDEX" torch==2.5.1 torchaudio==2.5.1
    echo 'installing aligner dependencies...'
    uv pip install --python "$PY" demucs==4.0.1 soundfile pyphen num2words tqdm imageio-ffmpeg
else
    echo 'uv not found — using python venv + pip'
    if command -v python3.11 >/dev/null 2>&1; then
        python3.11 -m venv .venv
    else
        python3 -m venv .venv
    fi
    "$PY" -m pip install --upgrade pip
    echo "installing torch ($DEVICE) — this is the big download..."
    "$PY" -m pip install --index-url "$TORCH_INDEX" torch==2.5.1 torchaudio==2.5.1
    echo 'installing aligner dependencies...'
    "$PY" -m pip install demucs==4.0.1 soundfile pyphen num2words tqdm imageio-ffmpeg
fi

if [ ! -x "$PY" ]; then
    echo 'venv creation failed' >&2
    exit 1
fi

echo 'provisioning ffmpeg into the venv...'
"$PY" -c "import imageio_ffmpeg, os; src = imageio_ffmpeg.get_ffmpeg_exe(); dst = '.venv/bin/ffmpeg'; os.path.lexists(dst) and os.remove(dst); os.symlink(src, dst)"

echo 'lyriclab environment ready'
