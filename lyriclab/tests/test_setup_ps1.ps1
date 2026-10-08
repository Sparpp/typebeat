# Regression test for setup.ps1 against the Windows PowerShell 5.1 native-stderr landmine
# (backlog 349). No Pester: plain PowerShell, run by hand from any shell:
#
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\test_setup_ps1.ps1
#
# Exit code 0 when every case passes. Nothing here downloads or installs anything: setup.ps1 runs
# from scratch copies, either with -PlanOnly (it only reports which uv it would use) or, for the
# setup-sentinel cases (backlog 353), in full against a fake uv.cmd that builds a fake venv.
#
# Every child runs the way the game runs setup.ps1 (powershell.exe -File with stdout and stderr
# redirected) with a PATH holding only System32 plus a fake dir whose py.cmd / python.cmd write
# "No suitable Python runtime found" to stderr and exit 103, which is what the real py launcher
# does when the requested version is not installed.

$ErrorActionPreference = 'Stop'

$setup = Join-Path (Split-Path $PSScriptRoot -Parent) 'setup.ps1'
$work = Join-Path ([IO.Path]::GetTempPath()) ("lyriclab-setup-test-" + [Guid]::NewGuid().ToString('N'))
$fake = Join-Path $work 'fakebin'
$lab = Join-Path $work 'lab'
New-Item -ItemType Directory -Force -Path $fake, $lab | Out-Null
Copy-Item $setup (Join-Path $lab 'setup.ps1')

$failingPython = "@echo No suitable Python runtime found 1>&2`r`n@exit /b 103`r`n"
foreach ($name in 'py.cmd', 'python.cmd') {
    [IO.File]::WriteAllText((Join-Path $fake $name), $failingPython)
}

$powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$basePath = "$fake;$env:SystemRoot\System32;$env:SystemRoot\System32\WindowsPowerShell\v1.0"

function Invoke-Child([string]$script, [string]$extraArgs = '', [string]$pathPrefix = '', [hashtable]$extraEnv = @{}) {
    $psi = New-Object Diagnostics.ProcessStartInfo
    $psi.FileName = $powershell
    $psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$script`" $extraArgs"
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $psi.EnvironmentVariables['PATH'] = if ($pathPrefix) { "$pathPrefix;$basePath" } else { $basePath }
    foreach ($key in $extraEnv.Keys) { $psi.EnvironmentVariables[$key] = $extraEnv[$key] }
    $p = [Diagnostics.Process]::Start($psi)
    $out = $p.StandardOutput.ReadToEndAsync()
    $err = $p.StandardError.ReadToEndAsync()
    $p.WaitForExit()
    [pscustomobject]@{ ExitCode = $p.ExitCode; Output = $out.Result + $err.Result }
}

$failures = 0
function Check([string]$name, [bool]$ok, [string]$detail) {
    if ($ok) {
        Write-Output "PASS  $name"
    } else {
        Write-Output "FAIL  $name"
        Write-Output ($detail -split "`n" | ForEach-Object { "      $_" } | Out-String)
        $script:failures++
    }
}

try {
    # Control: the pre-349 probe really does die on the fake py. Without this the cases below
    # could pass vacuously (a fake that never reaches stderr would hide the landmine).
    $old = Join-Path $work 'old_probe.ps1'
    [IO.File]::WriteAllText($old, @'
$ErrorActionPreference = 'Stop'
$py = Get-Command py -ErrorAction SilentlyContinue
if ($py) {
    & py -3.11 -c 'pass' 2>$null
    if ($LASTEXITCODE -eq 0) { $hasPy311 = $true }
}
Write-Output 'probe survived'
'@)
    $r = Invoke-Child $old
    Check 'control: the old 2>$null probe dies on a py that writes stderr' (
        $r.ExitCode -ne 0 -and $r.Output -match 'NativeCommandError' -and $r.Output -notmatch 'probe survived') $r.Output

    # The safe probe form the setup.ps1 header prescribes, should one ever be needed again.
    $safe = Join-Path $work 'safe_probe.ps1'
    [IO.File]::WriteAllText($safe, @'
$ErrorActionPreference = 'Stop'
cmd /c "py -3.11 -c pass >nul 2>nul"
Write-Output "probe survived LASTEXITCODE=$LASTEXITCODE"
'@)
    $r = Invoke-Child $safe
    Check 'control: the cmd /c probe survives with the exit code' (
        $r.ExitCode -eq 0 -and $r.Output -match 'probe survived LASTEXITCODE=103') $r.Output

    $labSetup = Join-Path $lab 'setup.ps1'

    # No uv anywhere and a broken py on PATH: must plan the pinned uv download, not die.
    $r = Invoke-Child $labSetup '-PlanOnly'
    Check 'setup.ps1: failing py, no uv -> plans the pinned uv download' (
        $r.ExitCode -eq 0 -and $r.Output -match 'plan: download uv \d+\.\d+\.\d+ from https://github\.com/astral-sh/uv/releases/download/.+/uv-x86_64-pc-windows-msvc\.zip' -and
        $r.Output -notmatch 'NativeCommandError|No suitable Python') $r.Output

    # A previously downloaded pinned copy is reused.
    New-Item -ItemType Directory -Force -Path (Join-Path $lab '.uv') | Out-Null
    [IO.File]::WriteAllText((Join-Path $lab '.uv\uv.exe'), 'placeholder')
    $r = Invoke-Child $labSetup '-PlanOnly'
    Check 'setup.ps1: failing py, pinned .uv\uv.exe -> uses it' (
        $r.ExitCode -eq 0 -and $r.Output -match 'plan: use uv at .*\\\.uv\\uv\.exe') $r.Output

    # uv on PATH wins over the pinned copy.
    $uvDir = Join-Path $work 'uvbin'
    New-Item -ItemType Directory -Force -Path $uvDir | Out-Null
    Copy-Item (Join-Path $env:SystemRoot 'System32\where.exe') (Join-Path $uvDir 'uv.exe')
    $r = Invoke-Child $labSetup '-PlanOnly' $uvDir
    Check 'setup.ps1: failing py, uv on PATH -> uses it' (
        $r.ExitCode -eq 0 -and $r.Output -match ('plan: use uv at ' + [regex]::Escape((Join-Path $uvDir 'uv.exe')))) $r.Output

    # Static audit: no code line invokes the py launcher or redirects stderr (2>$null, 2>&1).
    $bad = Get-Content $setup | Where-Object { $_ -notmatch '^\s*#' } |
        Where-Object { $_ -match '2>\s*(\$null|&1)' -or $_ -match '(^|[\s&])py(\.exe)?\s+-' }
    Check 'setup.ps1: no py launcher call and no redirected native stderr' (-not $bad) ($bad -join "`n")

    # ---- Setup sentinel (backlog 353): full runs against a FAKE uv, nothing downloaded. ----
    # uv.cmd answers 'venv' by creating .venv\Scripts\python.exe as a copy of doskey.exe (which
    # exits 0 whatever it is given, so every python -c step "succeeds") or, with FAKE_PY_BROKEN,
    # of where.exe (which exits 1, so the first python step fails the way a venv without torch
    # does). 'pip' succeeds unless FAKE_UV_FAIL_TORCH is set and the call installs torch,
    # FAKE_UV_FAIL_DEPS is set and it installs the aligner dependencies (demucs), or
    # FAKE_UV_FAIL_FUSED is set and it installs the fused-evidence pair (phonemizer). Every call is
    # logged to uv-calls.txt in the lab (the redirect comes first so a trailing digit in the
    # arguments cannot read as a handle number).
    $fakeUv = Join-Path $work 'fakeuv'
    New-Item -ItemType Directory -Force -Path $fakeUv | Out-Null
    [IO.File]::WriteAllText((Join-Path $fakeUv 'uv.cmd'), (@(
        '@echo off'
        '>>uv-calls.txt echo %*'
        'if /i "%~1"=="venv" goto venv'
        'if /i "%~1"=="pip" goto pip'
        'exit /b 0'
        ':venv'
        'if not exist .venv\Scripts mkdir .venv\Scripts'
        'set "FAKE_PY=%SystemRoot%\System32\doskey.exe"'
        'if defined FAKE_PY_BROKEN set "FAKE_PY=%SystemRoot%\System32\where.exe"'
        'copy /y "%FAKE_PY%" .venv\Scripts\python.exe >nul'
        'exit /b %errorlevel%'
        ':pip'
        'if not defined FAKE_UV_FAIL_DEPS goto fused'
        'echo %* | findstr /c:"demucs" >nul'
        'if not errorlevel 1 exit /b 1'
        ':fused'
        'if not defined FAKE_UV_FAIL_FUSED goto torch'
        'echo %* | findstr /c:"phonemizer" >nul'
        'if not errorlevel 1 exit /b 1'
        ':torch'
        'if not defined FAKE_UV_FAIL_TORCH exit /b 0'
        'echo %* | findstr /c:"torch==" >nul'
        'if errorlevel 1 exit /b 0'
        'exit /b 1'
    ) -join "`r`n") + "`r`n")

    function New-Lab([string]$name) {
        $dir = Join-Path $work $name
        New-Item -ItemType Directory -Force -Path $dir | Out-Null
        Copy-Item $setup (Join-Path $dir 'setup.ps1')
        $dir
    }

    $sentinelName = '.venv\.typebeat-setup-ok'

    # A clean, fully successful run writes the sentinel, with the pinned versions and the device.
    $l = New-Lab 'run-ok'
    $r = Invoke-Child (Join-Path $l 'setup.ps1') '' $fakeUv
    $sentinelPath = Join-Path $l $sentinelName
    $content = if (Test-Path $sentinelPath) { [IO.File]::ReadAllText($sentinelPath) } else { '' }
    Check 'sentinel: a successful setup writes it' (
        $r.ExitCode -eq 0 -and $r.Output -match 'lyriclab environment ready' -and
        (Test-Path (Join-Path $l '.venv\Scripts\python.exe')) -and
        $content -match '(?m)^python=3\.11$' -and $content -match '(?m)^torch=2\.5\.1$' -and
        $content -match '(?m)^device=cpu$' -and $content -match '(?m)^created=\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ$') ($r.Output + "`nsentinel: " + $content)

    # The full run installs the fused-evidence pair (aligner version 10) by its own call after the
    # aligner dependencies, never with torch, and fetches the weights before the sentinel.
    $calls = @(Get-Content (Join-Path $l 'uv-calls.txt'))
    $pips = @($calls | Where-Object { $_ -match '^pip install' -and $_ -notmatch 'torch==' })
    Check 'fused: a full setup installs the pair by its own call after the aligner dependencies' (
        $pips.Count -eq 2 -and $pips[0] -match 'demucs==4\.0\.1 soundfile pyphen num2words tqdm imageio-ffmpeg$' -and
        $pips[1] -match ' phonemizer espeakng-loader$' -and
        $r.Output -match 'fetching the fused-evidence weights') (($calls -join "`n") + "`n" + $r.Output)

    # With the sentinel in place a re-run is a no-op.
    Remove-Item (Join-Path $l 'uv-calls.txt')
    $r = Invoke-Child (Join-Path $l 'setup.ps1') '' $fakeUv
    Check 'sentinel: present -> already present, nothing rebuilt' (
        $r.ExitCode -eq 0 -and $r.Output -match 'already present' -and $r.Output -notmatch 'creating venv' -and
        -not (Test-Path (Join-Path $l 'uv-calls.txt'))) $r.Output

    # -Update on that completed install (the game's Update button): both dependency calls again and
    # the weights, in the venv as it stands. No venv is created, torch is not reinstalled, and the
    # venv (a canary in it) and the sentinel are untouched.
    $sentinelPath = Join-Path $l $sentinelName
    $before = [IO.File]::ReadAllText($sentinelPath)
    [IO.File]::WriteAllText((Join-Path $l '.venv\canary.txt'), 'the existing environment')
    $r = Invoke-Child (Join-Path $l 'setup.ps1') '-Update' $fakeUv
    $calls = @(if (Test-Path (Join-Path $l 'uv-calls.txt')) { Get-Content (Join-Path $l 'uv-calls.txt') })
    $pips = @($calls | Where-Object { $_ -match '^pip install' })
    Check 'update: -Update installs both dependency calls in place, nothing rebuilt' (
        $r.ExitCode -eq 0 -and $r.Output -match 'lyriclab environment updated' -and $r.Output -match 'fetching the fused-evidence weights' -and
        $r.Output -notmatch 'creating venv|already present' -and
        $pips.Count -eq 2 -and $pips[0] -match 'imageio-ffmpeg$' -and $pips[1] -match ' phonemizer espeakng-loader$' -and
        -not ($pips -match 'torch') -and -not ($calls -match '^venv') -and
        (Test-Path (Join-Path $l '.venv\canary.txt')) -and [IO.File]::ReadAllText($sentinelPath) -eq $before) (($calls -join "`n") + "`n" + $r.Output)

    # A failed update keeps the completed install, whichever call failed: exit 1 with the usual
    # sentence, the venv and its sentinel left as they were (the aligner keeps working).
    foreach ($case in @(
            @{ Env = 'FAKE_UV_FAIL_DEPS'; Says = 'updating the aligner dependencies failed' },
            @{ Env = 'FAKE_UV_FAIL_FUSED'; Says = 'installing the fused-evidence packages failed' })) {
        $r = Invoke-Child (Join-Path $l 'setup.ps1') '-Update' $fakeUv @{ $case.Env = '1' }
        Check "update: a failed -Update ($($case.Env)) keeps the venv and its sentinel" (
            $r.ExitCode -eq 1 -and $r.Output -match ('setup failed: ' + $case.Says) -and
            (Test-Path (Join-Path $l '.venv\canary.txt')) -and [IO.File]::ReadAllText($sentinelPath) -eq $before) $r.Output
    }

    # The pair is optional in a full setup: when it cannot be installed (no espeakng-loader wheel
    # for the platform) the setup still completes, says so in one WARNING line, and skips the weights.
    $l = New-Lab 'run-no-fused'
    $r = Invoke-Child (Join-Path $l 'setup.ps1') '' $fakeUv @{ FAKE_UV_FAIL_FUSED = '1' }
    Check 'fused: a full setup without the pair still completes' (
        $r.ExitCode -eq 0 -and $r.Output -match 'lyriclab environment ready' -and (Test-Path (Join-Path $l $sentinelName)) -and
        $r.Output -match 'WARNING: the fused-evidence packages could not be installed' -and
        $r.Output -notmatch 'fetching the fused-evidence weights') $r.Output

    # -Update with no completed install is an ordinary full setup.
    $l = New-Lab 'run-update-fresh'
    $r = Invoke-Child (Join-Path $l 'setup.ps1') '-Update' $fakeUv
    Check 'update: -Update without a sentinel runs the full setup' (
        $r.ExitCode -eq 0 -and $r.Output -match 'creating venv' -and $r.Output -match 'lyriclab environment ready' -and
        (Test-Path (Join-Path $l $sentinelName))) $r.Output

    # A venv with python.exe but NO sentinel (a pre-349 half install) is rebuilt, not trusted.
    $l = New-Lab 'run-stale'
    New-Item -ItemType Directory -Force -Path (Join-Path $l '.venv\Scripts') | Out-Null
    [IO.File]::WriteAllText((Join-Path $l '.venv\Scripts\python.exe'), 'stale')
    [IO.File]::WriteAllText((Join-Path $l '.venv\canary.txt'), 'from the broken install')
    $r = Invoke-Child (Join-Path $l 'setup.ps1') '' $fakeUv
    Check 'sentinel: a sentinel-less venv is removed and rebuilt' (
        $r.ExitCode -eq 0 -and $r.Output -match 'removing an incomplete environment' -and
        -not (Test-Path (Join-Path $l '.venv\canary.txt')) -and (Test-Path (Join-Path $l $sentinelName))) $r.Output

    # A failed torch install: exit 1, one plain line, no venv and so no sentinel.
    $l = New-Lab 'run-torch-fails'
    $r = Invoke-Child (Join-Path $l 'setup.ps1') '' $fakeUv @{ FAKE_UV_FAIL_TORCH = '1' }
    Check 'sentinel: absent, and the venv removed, when torch fails to install' (
        $r.ExitCode -eq 1 -and $r.Output -match 'setup failed: installing torch failed' -and
        -not (Test-Path (Join-Path $l '.venv'))) $r.Output

    # A venv whose python cannot import anything: same outcome.
    $l = New-Lab 'run-python-broken'
    $r = Invoke-Child (Join-Path $l 'setup.ps1') '' $fakeUv @{ FAKE_PY_BROKEN = '1' }
    Check 'sentinel: absent, and the venv removed, when the venv python fails' (
        $r.ExitCode -eq 1 -and $r.Output -match 'setup failed:' -and
        -not (Test-Path (Join-Path $l '.venv'))) $r.Output

    # The failure cleanup removes the sentinel with the venv, so the runs above cannot see WHERE in
    # the script it is written; what the ordering protects against is a run KILLED part way (the
    # game kills the process tree on cancel, and no cleanup runs). So pin it statically: the
    # sentinel lives inside .venv, and it is written after the last native command, with only the
    # final message after it.
    $code = @(Get-Content $setup | Where-Object { $_ -notmatch '^\s*#' -and $_.Trim() -ne '' })
    $write = -1; $lastNative = -1
    for ($i = 0; $i -lt $code.Count; $i++) {
        if ($code[$i] -match 'WriteAllText\(.*\$sentinel') { $write = $i }
        if ($code[$i] -match 'Invoke-Native\s|&\s*\$(py|uvExe)\b') { $lastNative = $i }
    }
    $after = @(if ($write -ge 0) { $code[($write + 1)..($code.Count - 1)] | Where-Object { $_.Trim() -notmatch '^(\}|\} catch \{|Stop-Setup \$_\.Exception\.Message)$' } })
    Check 'sentinel: inside .venv, written after the last native command, then only the final message' (
        ($code -match "^\`$sentinel = '\.venv\\") -and $write -gt $lastNative -and $lastNative -ge 0 -and
        $after.Count -eq 1 -and $after[0] -match "Write-Output 'lyriclab environment ready'") ("write=$write lastNative=$lastNative after:`n" + ($after -join "`n"))
} finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}

if ($failures) {
    Write-Output "$failures case(s) failed"
    exit 1
}
Write-Output 'all setup.ps1 cases passed'
exit 0
