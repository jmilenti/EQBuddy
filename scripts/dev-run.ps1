# Build the current tree and run it as a DEV build, side by side with the installed
# EQdps — the standing loop for trying a change against real play before releasing it.
#
#   pwsh scripts\dev-run.ps1            # build + launch
#   pwsh scripts\dev-run.ps1 -NoLaunch  # just build
#   pwsh scripts\dev-run.ps1 -Fresh     # start the dev settings over from the real ones
#
# Deliberately NOT an install: it never touches %LOCALAPPDATA%\Programs\EQdps, so the
# copy you play with keeps whatever the last release gave it, and a half-finished change
# can never take your working widget down with it.
#
# The dev copy gets its OWN settings folder (dist\dev\appdata) for the same reason in
# reverse: the released app DROPS settings keys it doesn't know about when it saves, so
# sharing a settings file would have the release quietly delete every new setting the dev
# build wrote. The folder is seeded from your real one the first time, so the dev build
# comes up with your layout rather than a blank slate.
param([switch]$NoLaunch, [switch]$Fresh)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$dev = Join-Path $repo 'dist\dev'
$appdata = Join-Path $dev 'appdata'
$exe = Join-Path $dev 'EQdps-dev.exe'

# Only ever stops the DEV copy: the installed EQdps.exe is a different process name, so
# a rebuild can't close the widget you are actually playing with.
Get-Process EQdps-dev -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

dotnet build "$repo\src\EQBuddy.Lite\EQBuddy.Lite.csproj" -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

New-Item -ItemType Directory -Force $dev | Out-Null
$out = "$repo\src\EQBuddy.Lite\bin\Release\net10.0-windows"
Copy-Item "$out\*" $dev -Recurse -Force
if (Test-Path $exe) { Remove-Item $exe -Force }
Rename-Item "$dev\EQdps.exe" 'EQdps-dev.exe'

if ($Fresh -and (Test-Path $appdata)) { Remove-Item $appdata -Recurse -Force }
if (-not (Test-Path $appdata)) {
    New-Item -ItemType Directory -Force $appdata | Out-Null
    $real = Join-Path $env:APPDATA 'EQBuddy'
    if (Test-Path $real) {
        # Settings and layout only. History, ledgers and spawn timers stay behind: the dev
        # build should look like yours, not fork your records.
        foreach ($f in 'settings.json', 'lite-ui.json', 'lite-sync.json') {
            if (Test-Path "$real\$f") { Copy-Item "$real\$f" $appdata }
        }
        Write-Host "Seeded dev settings from $real"
    }
    # The log janitor truncates and archives finished sessions. One app doing that is
    # housekeeping; two doing it at once, while you are reading the log, is a fight.
    $s = Join-Path $appdata 'settings.json'
    if (Test-Path $s) {
        $j = Get-Content $s -Raw | ConvertFrom-Json
        $j.TruncateLogs = $false
        $j.ArchiveLogs = $false
        $j | ConvertTo-Json -Depth 12 | Set-Content $s -Encoding utf8
    }
}

$version = ([xml](Get-Content "$repo\Directory.Build.props")).Project.PropertyGroup.Version
Write-Host "Dev build $version ready: $exe"
if ($NoLaunch) { return }

$env:EQBUDDY_APPDATA = $appdata
Start-Process $exe
Write-Host 'Launched. It runs alongside your installed EQdps and shares nothing with it.'
