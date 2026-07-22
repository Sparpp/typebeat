# One-time environment setup for the vendored lyriclab aligner (see README.md).
# Creates .venv beside this script with the pinned dependency set. The game runs this from the
# "install local auto-aligner" action (settings / first-run setup); it can also be run by hand.
# torch 2.5.x pinned deliberately (2.6 breaks Demucs checkpoint loading); Python 3.11
# pinned for wheel coverage.
#
# Self-sufficient on a clean machine: when neither uv nor Python 3.11 is present, a pinned uv
# build (single static binary, ~15 MB) is downloaded beside this script and used — uv then
# fetches a managed CPython 3.11 on its own, so players never install Python themselves.
#
# -Device cpu  (default): CPU-only torch wheels (~200 MB download).
# -Device cuda: CUDA 12.1 torch wheels (~2.5 GB download) - alignment runs on an NVIDIA GPU
#               (align_lyrics.py --device cuda). Requires a reasonably recent NVIDIA driver.
#
# ffmpeg: align_lyrics.py shells out to an `ffmpeg` on PATH for audio decode. Most machines
# don't have one, so the imageio-ffmpeg wheel's bundled static build is copied into the venv's
# Scripts dir as ffmpeg.exe; the game prepends that dir to PATH when it runs the aligner.

param(
    [ValidateSet('cpu', 'cuda')]
    [string]$Device = 'cpu'
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (Test-Path '.venv\Scripts\python.exe') {
    Write-Output 'lyriclab environment already present'
    exit 0
}

$torchIndex = if ($Device -eq 'cuda') { 'https://download.pytorch.org/whl/cu121' } else { 'https://download.pytorch.org/whl/cpu' }

# Prefer an existing uv; otherwise bootstrap a pinned copy locally (no admin, no PATH changes).
$uvExe = $null
$uvCmd = Get-Command uv -ErrorAction SilentlyContinue
if ($uvCmd) {
    $uvExe = $uvCmd.Source
} elseif (Test-Path '.uv\uv.exe') {
    $uvExe = (Resolve-Path '.uv\uv.exe').Path
} else {
    $hasPy311 = $false
    $py = Get-Command py -ErrorAction SilentlyContinue
    if ($py) {
        & py -3.11 -c 'pass' 2>$null
        if ($LASTEXITCODE -eq 0) { $hasPy311 = $true }
    }

    if (-not $hasPy311) {
        Write-Output 'downloading uv (manages its own Python 3.11)...'
        $uvVersion = '0.5.14'
        $zip = Join-Path $env:TEMP "uv-$uvVersion.zip"
        Invoke-WebRequest -Uri "https://github.com/astral-sh/uv/releases/download/$uvVersion/uv-x86_64-pc-windows-msvc.zip" -OutFile $zip
        New-Item -ItemType Directory -Force -Path '.uv' | Out-Null
        Expand-Archive -Path $zip -DestinationPath '.uv' -Force
        Remove-Item $zip -ErrorAction SilentlyContinue
        $uvExe = (Resolve-Path '.uv\uv.exe').Path
    }
}

if ($uvExe) {
    Write-Output 'creating venv with uv...'
    & $uvExe venv .venv --python 3.11
    Write-Output "installing torch ($Device) - this is the big download..."
    & $uvExe pip install --python .venv\Scripts\python.exe --index-url $torchIndex torch==2.5.1 torchaudio==2.5.1
    Write-Output 'installing aligner dependencies...'
    & $uvExe pip install --python .venv\Scripts\python.exe demucs==4.0.1 soundfile pyphen num2words tqdm imageio-ffmpeg
} else {
    Write-Output 'using system python venv + pip'
    py -3.11 -m venv .venv
    & .venv\Scripts\python.exe -m pip install --upgrade pip
    Write-Output "installing torch ($Device) - this is the big download..."
    & .venv\Scripts\python.exe -m pip install --index-url $torchIndex torch==2.5.1 torchaudio==2.5.1
    Write-Output 'installing aligner dependencies...'
    & .venv\Scripts\python.exe -m pip install demucs==4.0.1 soundfile pyphen num2words tqdm imageio-ffmpeg
}

if (-not (Test-Path '.venv\Scripts\python.exe')) {
    Write-Error 'venv creation failed'
    exit 1
}

Write-Output 'provisioning ffmpeg into the venv...'
& .venv\Scripts\python.exe -c "import imageio_ffmpeg, shutil; shutil.copy(imageio_ffmpeg.get_ffmpeg_exe(), r'.venv\Scripts\ffmpeg.exe')"

if ($Device -eq 'cuda') {
    Write-Output 'verifying CUDA is usable by torch...'
    & .venv\Scripts\python.exe -c "import torch, sys; sys.exit(0 if torch.cuda.is_available() else 1)"
    if ($LASTEXITCODE -ne 0) {
        Write-Output 'WARNING: torch cannot see a CUDA device (driver too old?) - alignment will fall back to CPU'
    }
}

Write-Output 'lyriclab environment ready'
