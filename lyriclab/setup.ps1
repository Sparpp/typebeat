# One-time environment setup for the vendored lyriclab aligner (see README.md).
# Creates .venv beside this script with the pinned dependency set. The game runs this
# automatically on first import when no environment is found; it can also be run by hand.
# torch 2.5.x pinned deliberately (2.6 breaks Demucs checkpoint loading); Python 3.11
# pinned for wheel coverage.

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (Test-Path '.venv\Scripts\python.exe') {
    Write-Output 'lyriclab environment already present'
    exit 0
}

$uv = Get-Command uv -ErrorAction SilentlyContinue

if ($uv) {
    Write-Output 'creating venv with uv...'
    uv venv .venv --python 3.11
    Write-Output 'installing torch (cpu) — this is the big download...'
    uv pip install --python .venv\Scripts\python.exe --index-url https://download.pytorch.org/whl/cpu torch==2.5.1 torchaudio==2.5.1
    Write-Output 'installing aligner dependencies...'
    uv pip install --python .venv\Scripts\python.exe demucs==4.0.1 soundfile pyphen num2words tqdm
} else {
    Write-Output 'uv not found — using python venv + pip'
    $py = Get-Command py -ErrorAction SilentlyContinue
    if ($py) { py -3.11 -m venv .venv } else { python -m venv .venv }
    & .venv\Scripts\python.exe -m pip install --upgrade pip
    Write-Output 'installing torch (cpu) — this is the big download...'
    & .venv\Scripts\python.exe -m pip install --index-url https://download.pytorch.org/whl/cpu torch==2.5.1 torchaudio==2.5.1
    Write-Output 'installing aligner dependencies...'
    & .venv\Scripts\python.exe -m pip install demucs==4.0.1 soundfile pyphen num2words tqdm
}

if (-not (Test-Path '.venv\Scripts\python.exe')) {
    Write-Error 'venv creation failed'
    exit 1
}

Write-Output 'lyriclab environment ready'
