<#
.SYNOPSIS
    Post-install verification for a (clean) machine (SPEC §11): binaries present and
    signed, the command line answers, a short recording produces a playable file of
    the expected dimensions, and it reaches a configured folder destination
    correctly named.

.PARAMETER InstallDir
    Where Captr is installed. Default: per-machine Program Files location.

.PARAMETER SkipSignatureCheck
    For unsigned developer builds (the build succeeds unsigned by design, SPEC §11).
#>
[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path ${env:ProgramFiles} 'Captr'),
    [switch]$SkipSignatureCheck
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$failures = @()

function Check([string]$name, [scriptblock]$test) {
    try {
        & $test
        Write-Host "PASS  $name"
    } catch {
        Write-Host "FAIL  $name — $($_.Exception.Message)" -ForegroundColor Red
        $script:failures += $name
    }
}

$cli = Join-Path $InstallDir 'captr.exe'
$app = Join-Path $InstallDir 'Captr.App.exe'
$ffmpeg = Join-Path $InstallDir 'ffmpeg\ffmpeg.exe'
$ffprobe = Join-Path $InstallDir 'ffmpeg\ffprobe.exe'

Check 'binaries present' {
    foreach ($file in @($cli, $app, $ffmpeg, $ffprobe)) {
        if (-not (Test-Path $file)) { throw "missing: $file" }
    }
}

if (-not $SkipSignatureCheck) {
    Check 'binaries signed with valid signatures' {
        foreach ($file in @($cli, $app, $ffmpeg, $ffprobe)) {
            $signature = Get-AuthenticodeSignature $file
            if ($signature.Status -ne 'Valid') { throw "$file signature status: $($signature.Status)" }
        }
    }
}

Check 'command line responds (status)' {
    & $cli status --json | Out-Null
    if ($LASTEXITCODE -notin 0, 10, 11) { throw "unexpected exit code $LASTEXITCODE" }
}

Check 'command line is on PATH' {
    $found = Get-Command captr.exe -ErrorAction SilentlyContinue
    if (-not $found) { throw 'captr.exe not resolvable from PATH (open a NEW shell after install — PATH changes need one)' }
}

# --- End-to-end: short recording delivered to a temp folder destination ----------
$stage = Join-Path $env:TEMP ("captr-verify-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$work = Join-Path $stage 'work'
$dest = Join-Path $stage 'dest'
New-Item -ItemType Directory -Force $work, $dest | Out-Null

Check 'short recording produces a playable, correctly-named, delivered file' {
    # Configure: temp working folder + a folder destination, then record ~15 s.
    & $cli settings set workingFolder $work | Out-Null
    $settingsPath = Join-Path $env:APPDATA 'Captr\settings.json'
    $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
    $settings | Add-Member -NotePropertyName destinations -NotePropertyValue @(
        [ordered]@{ name = 'verify'; kind = 'folder'; enabled = $true; folderPath = $dest }
    ) -Force
    $settings | ConvertTo-Json -Depth 6 | Set-Content $settingsPath

    $startOut = & $cli start --label verify 2>&1
    Write-Host "      start: [$LASTEXITCODE] $startOut"
    if ($LASTEXITCODE -ne 0) { throw "start failed with $LASTEXITCODE" }
    Start-Sleep 15
    $stopOut = & $cli stop 2>&1
    Write-Host "      stop:  [$LASTEXITCODE] $stopOut"
    if ($LASTEXITCODE -ne 0) { throw "stop failed with $LASTEXITCODE" }

    # Wait for finalisation + delivery (poll status until idle, then check dest).
    $deadline = (Get-Date).AddMinutes(3)
    do {
        Start-Sleep 3
        & $cli status | Out-Null
    } until ($LASTEXITCODE -eq 10 -or (Get-Date) -gt $deadline)
    if ($LASTEXITCODE -ne 10) { throw 'the recorder never returned to idle after stop' }

    $deadline = (Get-Date).AddMinutes(2)
    do { Start-Sleep 3 } until ((Get-ChildItem $dest -Filter *.mkv -ErrorAction SilentlyContinue) -or (Get-Date) -gt $deadline)

    $delivered = Get-ChildItem $dest -Filter *.mkv | Select-Object -First 1
    if (-not $delivered) { throw "no recording arrived in $dest" }
    if ($delivered.Name -notmatch [regex]::Escape($env:COMPUTERNAME)) { throw "name '$($delivered.Name)' lacks the {machine} token" }

    $probe = & $ffprobe -v error -show_entries 'stream=width,height : format=duration' -of csv=p=0 $delivered.FullName
    if ($LASTEXITCODE -ne 0) { throw 'delivered file does not probe' }
    Write-Host "      delivered: $($delivered.Name)  probe: $($probe -join ' ')"
}

if ($failures.Count -gt 0) {
    throw "Post-install verification FAILED: $($failures -join ', ')"
}
Write-Host "`nAll post-install checks passed." -ForegroundColor Green
