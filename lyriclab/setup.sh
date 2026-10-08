#!/usr/bin/env bash
# One-time environment setup for the vendored lyriclab aligner (see README.md).
# POSIX counterpart of setup.ps1 (Linux/macOS). Creates .venv beside this script with the
# pinned dependency set. torch 2.5.x pinned deliberately (2.6 breaks Demucs checkpoint
# loading); Python 3.11 pinned for wheel coverage.
#
# Always uv, never the player's own Python: uv (on PATH, else a pinned copy in .uv/, else that
# pinned copy downloaded now with curl or wget, the same version setup.ps1 pins) fetches a managed
# CPython 3.11 by itself, so whatever python3 the system has, or none, is irrelevant. Do NOT fall
# back to "any python3": torch 2.5.1 publishes wheels for CPython 3.9 to 3.12 only, so a 3.13+
# venv can never install it, while the uv-managed 3.11 works everywhere (backlog 349).
#
# Usage: setup.sh [cpu|cuda] [--plan-only] [--update]
#   cpu (default) or cuda (CUDA 12.1 torch wheels).
#   --plan-only prints which uv would be used (or downloaded) and exits without changing anything.
#   It decides by presence alone; a real run also checks that the chosen uv answers --version and
#   falls through to the next candidate when it does not, so a stale uv on PATH or a damaged .uv/
#   copy heals instead of failing every retry the same way.
#   --update, on a COMPLETED install (the sentinel present), brings the existing venv up to this
#   script's dependency set in place (the aligner dependencies, then the fused-evidence pair and
#   its weights), then exits. The torch line is not re-run and the venv and sentinel are never
#   removed, so it costs a few small downloads at most. This is how the game's Update button gives
#   an existing install the packages a newer aligner adds. A failure exits 1 with the usual
#   sentence but leaves the install as it was: the aligner still runs (version 10 runs its version
#   9 path without the pair). Without a sentinel, --update is an ordinary full setup.
#
# Fused evidence (aligner version 10, its English path): phonemizer and espeakng-loader are
# installed by their OWN uv call after the required set, best effort, and are deliberately NOT in
# the import check below, which is what gates the sentinel. The aligner falls back to its version 9
# path when they are missing, so a platform with no espeakng-loader wheel (or a resolver that
# rejects them) still gets a working aligner instead of a failed setup. Their 57 MB of weights are
# fetched the same way (align_lyrics.qmul_weights_path, into torch hub's checkpoint dir, sha256
# checked); when that fails the first fused import fetches them instead.
#
# Failure output: one plain sentence on stderr that starts "setup failed:", says what failed and
# what the player can do (retry, check the connection, or install later from Settings), then the
# raw detail on a few following lines. The game keeps only the last lines of the output for its
# log, so the sentence comes first and the detail stays short enough not to push it out; the
# failing tool's own output (a uv resolver error, a pip traceback) is already printed above it.
#
# ffmpeg: align_lyrics.py shells out to an `ffmpeg` on PATH. The imageio-ffmpeg wheel's
# bundled static build is linked into the venv's bin dir; the game prepends that dir to
# PATH when it runs the aligner.
#
# Setup sentinel (backlog 353): the LAST act of a fully successful run writes
# .venv/.typebeat-setup-ok (the pinned Python and torch versions, the device and a UTC timestamp).
# The game treats the aligner as installed only when that file exists beside the venv's python, so
# a run that died or was killed part way reads as "needs repair" instead of "installed". Nothing
# may be added after the sentinel write except the final message; tests/test_setup_sh.sh pins the
# ordering. A .venv WITHOUT the sentinel is treated as incomplete and rebuilt from scratch (the game
# probes such a venv's imports first and writes the sentinel itself when they load).
set -euo pipefail

cd "$(dirname "$0")"

UV_VERSION='0.5.14'
PYTHON_VERSION='3.11'
TORCH_VERSION='2.5.1'
SENTINEL='.venv/.typebeat-setup-ok'
# The required packages besides torch, and the optional fused-evidence pair (see the header). The
# same lists, spelled the same way, are in setup.ps1; the game's tests pin both. Expanded unquoted,
# so each splits into one argument per package.
ALIGNER_PACKAGES='demucs==4.0.1 soundfile pyphen num2words tqdm imageio-ffmpeg'
FUSED_PACKAGES='phonemizer espeakng-loader'

# What the player can do about a failure. Anything that downloads can fail on the network; the
# rest can only be retried. Either way the game's Settings page offers the install again later.
RETRY_NETWORK='check your internet connection and retry, or install the aligner later from Settings'
RETRY_PLAIN='retry, or install the aligner later from Settings'

DEVICE='cpu'
PLAN_ONLY=0
UPDATE=0
# True while --update refreshes a completed install, which a failure must leave in place.
UPDATING=0
for arg in "$@"; do
    case "$arg" in
        cpu|cuda) DEVICE="$arg" ;;
        --plan-only) PLAN_ONLY=1 ;;
        --update) UPDATE=1 ;;
        *) echo "setup failed: unknown argument '$arg' (expected cpu, cuda, --plan-only or --update)" >&2; exit 1 ;;
    esac
done

case "$DEVICE" in
    cpu)  TORCH_INDEX='https://download.pytorch.org/whl/cpu' ;;
    cuda) TORCH_INDEX='https://download.pytorch.org/whl/cu121' ;;
esac

PY=".venv/bin/python"

# The failure report (see the header): fail WHAT [ADVICE] [DETAIL...] prints the sentence, then up
# to five detail lines, on stderr. Drops any half-built venv so the next attempt starts clean (the
# sentinel is inside .venv, so it can never outlive a failed run), then exits 1. Under --update
# the venv is a completed install that still works, so it is kept.
fail() {
    local what="$1" advice="${2:-$RETRY_PLAIN}" shown=0 line
    echo "setup failed: $what; $advice." >&2
    shift
    if [ $# -gt 0 ]; then shift; fi
    for line in "$@"; do
        [ -n "$line" ] || continue
        [ "$shown" -lt 5 ] || break
        echo "  detail: $line" >&2
        shown=$((shown + 1))
    done
    [ "$PLAN_ONLY" -eq 1 ] || [ "$UPDATING" -eq 1 ] || rm -rf .venv
    exit 1
}

# True when $1 runs and answers --version; says which way it went, for the log.
uv_runs() {
    local version
    if version="$("$1" --version 2>&1)"; then
        echo "uv at $1: $version"
        return 0
    fi
    echo "uv at $1 does not run: $version"
    return 1
}

# The fused-evidence pair, then its weights through the aligner's own qmul_weights_path (so the
# URL, the file name and the sha256 live in align_lyrics.py alone). Best effort (see the header):
# each failure is one WARNING line, and the return status says whether the packages went in. The
# weights are skipped without them, and the first import that needs them fetches them itself.
install_fused_evidence() {
    local rc=0
    echo 'installing the fused-evidence packages...'
    # shellcheck disable=SC2086
    "$UV" pip install --python "$PY" $FUSED_PACKAGES || rc=$?
    if [ "$rc" -ne 0 ]; then
        echo "WARNING: the fused-evidence packages could not be installed (exit code $rc); the aligner runs its version 9 path without them"
        return 1
    fi
    echo 'fetching the fused-evidence weights (57 MB, once)...'
    "$PY" -c "import align_lyrics; align_lyrics.qmul_weights_path()" || rc=$?
    if [ "$rc" -ne 0 ]; then
        echo "WARNING: the fused-evidence weights could not be fetched (exit code $rc); the first import that needs them fetches them instead"
    fi
    return 0
}

if [ "$PLAN_ONLY" -eq 0 ] && [ -f "$SENTINEL" ]; then
    if [ "$UPDATE" -eq 0 ]; then
        echo 'lyriclab environment already present'
        exit 0
    fi
    UPDATING=1
fi

# torch 2.5.1 publishes no macOS x86_64 wheel (the last one was 2.2), so on an Intel Mac, or on an
# Apple Silicon one running this under Rosetta, the torch install can never succeed. Said before
# anything is downloaded or removed, so a retry does not fetch uv and a CPython only to fail the same
# way. --plan-only still prints the plan (it changes nothing).
if [ "$PLAN_ONLY" -eq 0 ] && [ "$(uname -s)" = Darwin ] && [ "$(uname -m)" = x86_64 ]; then
    if [ "$(sysctl -n sysctl.proc_translated 2>/dev/null || echo 0)" = 1 ]; then
        fail 'torch 2.5.1, which the aligner needs, has no build for Intel Macs, and this setup runs under Rosetta' \
            "run the Apple Silicon build of the game and install the aligner from Settings again; until then imports use your lyrics' line stamps"
    fi
    fail 'torch 2.5.1, which the aligner needs, has no build for Intel Macs' \
        "the local auto-aligner needs an Apple Silicon Mac, and imports use your lyrics' line stamps instead"
fi

if [ "$PLAN_ONLY" -eq 0 ] && [ "$UPDATING" -eq 0 ] && [ -e .venv ]; then
    echo 'removing an incomplete environment left by an earlier setup...'
    rm -rf .venv || fail 'the incomplete environment left by an earlier setup could not be removed' \
        'close anything that might be using the aligner folder and retry, or install the aligner later from Settings'
fi

# Prefer an existing uv; otherwise the pinned local copy; otherwise download that copy. A real run
# only keeps a candidate that actually runs (see --plan-only in the header).
UV=''
if command -v uv >/dev/null 2>&1 && { [ "$PLAN_ONLY" -eq 1 ] || uv_runs "$(command -v uv)"; }; then
    UV="$(command -v uv)"
elif [ -x .uv/uv ]; then
    if [ "$PLAN_ONLY" -eq 1 ] || uv_runs "$PWD/.uv/uv"; then
        UV="$PWD/.uv/uv"
    else
        echo 'the pinned uv copy is damaged, downloading it again...'
        rm -rf .uv
    fi
fi

if [ -z "$UV" ]; then
    case "$(uname -s)" in
        Linux)  OS_TRIPLE='unknown-linux-musl' ;; # static build: no glibc version dependency
        Darwin) OS_TRIPLE='apple-darwin' ;;
        *) fail "no uv build for $(uname -s)" 'install uv (https://docs.astral.sh/uv/) and retry' ;;
    esac
    case "$(uname -m)" in
        x86_64|amd64)  ARCH='x86_64' ;;
        aarch64|arm64) ARCH='aarch64' ;;
        *) fail "no uv build for $(uname -m)" 'install uv (https://docs.astral.sh/uv/) and retry' ;;
    esac
    UV_URL="https://github.com/astral-sh/uv/releases/download/$UV_VERSION/uv-$ARCH-$OS_TRIPLE.tar.gz"
fi

if [ "$PLAN_ONLY" -eq 1 ]; then
    if [ -n "$UV" ]; then echo "plan: use uv at $UV"; else echo "plan: download uv $UV_VERSION from $UV_URL"; fi
    exit 0
fi

if [ -z "$UV" ]; then
    if command -v curl >/dev/null 2>&1; then
        FETCH=(curl -fsSL -o)
    elif command -v wget >/dev/null 2>&1; then
        FETCH=(wget -q -O)
    else
        fail 'uv could not be downloaded because neither curl nor wget is installed' 'install curl (or uv) and retry'
    fi
    echo "downloading uv $UV_VERSION (manages its own Python 3.11)..."
    UV_TMP="$(mktemp -d "${TMPDIR:-/tmp}/typebeat-uv.XXXXXX")" || fail 'a temporary folder for the uv download could not be created'
    trap 'rm -rf "$UV_TMP"' EXIT
    "${FETCH[@]}" "$UV_TMP/uv.tar.gz" "$UV_URL" || fail "downloading uv $UV_VERSION failed" "$RETRY_NETWORK" "${FETCH[0]} exit code $?" "$UV_URL"
    mkdir -p "$UV_TMP/unpacked"
    tar -xzf "$UV_TMP/uv.tar.gz" -C "$UV_TMP/unpacked" || fail "unpacking uv $UV_VERSION failed" "$RETRY_NETWORK" "$UV_URL"
    # The tarball holds uv-<target>/uv and uvx today; search rather than assume that layout.
    UV_FOUND="$(find "$UV_TMP/unpacked" -type f -name uv | head -n 1)" || true
    [ -n "$UV_FOUND" ] || fail 'the downloaded uv archive did not contain uv' "$RETRY_NETWORK" "$UV_URL"
    mkdir -p .uv
    if ! mv -f "$UV_FOUND" .uv/uv || ! chmod +x .uv/uv; then
        fail 'uv could not be put in place' "$RETRY_PLAIN" "$PWD/.uv/uv"
    fi
    UV="$PWD/.uv/uv"
    uv_runs "$UV" || fail 'the downloaded uv does not run' "$RETRY_NETWORK" "$UV_URL"
fi
echo "using uv at $UV"

# --update on a completed install: the dependency lines again (uv leaves what is already satisfied,
# torch included, alone) and the weights, then out. Nothing below this block runs. Unlike a full
# setup, the update reports the fused-evidence pair failing: installing it is what it is for.
if [ "$UPDATING" -eq 1 ]; then
    echo 'updating the aligner dependencies in the existing environment...'
    # shellcheck disable=SC2086
    "$UV" pip install --python "$PY" $ALIGNER_PACKAGES || fail 'updating the aligner dependencies failed' "$RETRY_NETWORK" "exit code $?; the tool's own output is above"
    install_fused_evidence || fail 'installing the fused-evidence packages failed' "$RETRY_NETWORK" 'the aligner keeps working on its version 9 path'
    echo 'lyriclab environment updated'
    exit 0
fi

echo 'creating venv with uv...'
"$UV" venv .venv --python "$PYTHON_VERSION" || fail "creating the Python $PYTHON_VERSION environment failed" "$RETRY_NETWORK" "exit code $?; the tool's own output is above"
echo "installing torch ($DEVICE), this is the big download..."
"$UV" pip install --python "$PY" --index-url "$TORCH_INDEX" "torch==$TORCH_VERSION" "torchaudio==$TORCH_VERSION" || fail 'installing torch failed' "$RETRY_NETWORK" "exit code $?; the tool's own output is above"
echo 'installing aligner dependencies...'
# shellcheck disable=SC2086
"$UV" pip install --python "$PY" $ALIGNER_PACKAGES || fail 'installing the aligner dependencies failed' "$RETRY_NETWORK" "exit code $?; the tool's own output is above"

[ -x "$PY" ] || fail 'the Python environment was not created' "$RETRY_PLAIN" "no $PY after uv venv"

echo 'provisioning ffmpeg into the venv...'
"$PY" -c "import imageio_ffmpeg, os; src = imageio_ffmpeg.get_ffmpeg_exe(); dst = '.venv/bin/ffmpeg'; os.path.lexists(dst) and os.remove(dst); os.symlink(src, dst)" || fail 'provisioning ffmpeg failed' "$RETRY_PLAIN" "exit code $?; the tool's own output is above"

echo 'verifying the installed packages import...'
"$PY" -c "import torch, torchaudio, demucs, soundfile, pyphen, num2words" || fail 'the installed packages do not import' "$RETRY_PLAIN" "exit code $?; the tool's own output is above"

install_fused_evidence || true

# LAST act, after every step above checked out: the sentinel the game reads as "installed".
printf 'python=%s\ntorch=%s\ndevice=%s\ncreated=%s\n' "$PYTHON_VERSION" "$TORCH_VERSION" "$DEVICE" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" > "$SENTINEL" || fail 'writing the setup sentinel failed'
echo 'lyriclab environment ready'
