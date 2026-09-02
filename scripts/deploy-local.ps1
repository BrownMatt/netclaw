# Build Netclaw from source and install it over the local copy.
#
# This is the "deploy from source" loop for local development:
#   1. Stop the running daemon (and any lingering netclaw/netclawd processes
#      that would hold a lock on the installed .exe files).
#   2. Publish the CLI + daemon + GUI as self-contained single-file win-x64
#      binaries, using the SAME flags as scripts/build/publish-binaries.sh so
#      the locally installed binary behaves exactly like a released one.
#   3. Copy netclaw.exe + netclawd.exe + netclaw-gui.exe into the install dir
#      (%LOCALAPPDATA%\Programs\netclaw by default) and ensure it is on PATH.
#   4. Optionally restart the daemon.
#
# Unlike the release installers, `all` here includes the GUI: this script only
# ever targets the Windows developer machine, and the installed GUI is the
# single-file publish proof for the win-x64 GUI component.
#
# Usage:
#   pwsh -File scripts/deploy-local.ps1                 # full deploy
#   pwsh -File scripts/deploy-local.ps1 -NoRestart      # don't restart daemon
#   pwsh -File scripts/deploy-local.ps1 -Component cli  # publish only the CLI
#   pwsh -File scripts/deploy-local.ps1 -Component gui  # publish only the GUI

param(
    [ValidateSet("all", "cli", "daemon", "gui")]
    [string]$Component = "all",

    [string]$InstallDir = "",

    # Skip restarting the daemon after install.
    [switch]$NoRestart
)

$ErrorActionPreference = "Stop"

$RepoRoot   = Split-Path -Parent $PSScriptRoot
$Rid        = "win-x64"
$PublishDir = Join-Path $RepoRoot "publish"

if (-not $InstallDir) {
    $InstallDir = Join-Path $env:LOCALAPPDATA "Programs\netclaw"
}

$CliExe    = Join-Path $InstallDir "netclaw.exe"
$DaemonExe = Join-Path $InstallDir "netclawd.exe"
$GuiExe    = Join-Path $InstallDir "netclaw-gui.exe"

Write-Host "Netclaw local deploy" -ForegroundColor Cyan
Write-Host "  Repo:        $RepoRoot"
Write-Host "  Install dir: $InstallDir"
Write-Host "  Component:   $Component"
Write-Host ""

# ── Step 1: stop the running copy ───────────────────────────────────────────
# A running daemon holds a file lock on netclawd.exe, so the copy in step 3
# would fail with "file in use" unless we stop it first. A GUI-only deploy
# leaves the daemon alone and only closes a running GUI.
$touchesCore = $Component -in @("all", "cli", "daemon")
Write-Host "==> Stopping running copy..." -ForegroundColor Yellow

if ($touchesCore -and (Test-Path $CliExe)) {
    try {
        & $CliExe daemon stop | Out-Host
    } catch {
        Write-Host "  Graceful stop failed (continuing): $_"
    }
}

# Force-kill anything still holding the binaries: by process name, and (for a
# core deploy) any process whose image lives in the install dir, which covers
# a CLI launched from there.
$procNames = @()
if ($touchesCore) { $procNames += @("netclaw", "netclawd") }
if ($Component -in @("all", "gui")) { $procNames += "netclaw-gui" }
$procs = @(Get-Process -Name $procNames -ErrorAction SilentlyContinue)
if ($touchesCore) {
    $procs += @(Get-Process -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($InstallDir, [StringComparison]::OrdinalIgnoreCase) })
}
$procs = $procs | Sort-Object Id -Unique

foreach ($p in $procs) {
    try {
        Write-Host "  Killing $($p.ProcessName) (PID $($p.Id))"
        $p.Kill()
        $p.WaitForExit(10000) | Out-Null
    } catch {
        Write-Host "  Could not kill PID $($p.Id) (continuing): $_"
    }
}

# ── Step 2: publish from source ─────────────────────────────────────────────
# Flags mirror scripts/build/publish-binaries.sh (win-x64 arm): self-contained
# single-file, native libs embedded, single-file compression on.
$publishFlags = @(
    "-c", "Release",
    "-r", $Rid,
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true"
)

function Publish-Component {
    param([string]$Name, [string]$Project)
    $outDir = Join-Path $PublishDir $Name
    Write-Host "==> Publishing $Name ($Rid)..." -ForegroundColor Yellow
    dotnet publish (Join-Path $RepoRoot $Project) @publishFlags -o $outDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $Name (exit $LASTEXITCODE)" }
}

if ($Component -eq "all" -or $Component -eq "cli") {
    Publish-Component "cli" "src\Netclaw.Cli\Netclaw.Cli.csproj"
}
if ($Component -eq "all" -or $Component -eq "daemon") {
    Publish-Component "daemon" "src\Netclaw.Daemon\Netclaw.Daemon.csproj"
}
if ($Component -eq "all" -or $Component -eq "gui") {
    Publish-Component "gui" "src\Netclaw.Gui\Netclaw.Gui.csproj"
}

# ── Step 3: install the binaries ────────────────────────────────────────────
Write-Host "==> Installing to $InstallDir..." -ForegroundColor Yellow
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

if ($Component -eq "all" -or $Component -eq "cli") {
    Copy-Item (Join-Path $PublishDir "cli\netclaw.exe") $CliExe -Force
    Write-Host "  Installed netclaw.exe"
}
if ($Component -eq "all" -or $Component -eq "daemon") {
    Copy-Item (Join-Path $PublishDir "daemon\netclawd.exe") $DaemonExe -Force
    Write-Host "  Installed netclawd.exe"
}
if ($Component -eq "all" -or $Component -eq "gui") {
    Copy-Item (Join-Path $PublishDir "gui\netclaw-gui.exe") $GuiExe -Force
    Write-Host "  Installed netclaw-gui.exe ($GuiExe)"
}

# ── Step 4: ensure install dir is on the user PATH ──────────────────────────
$userPath = [Environment]::GetEnvironmentVariable("PATH", "User")
$onPath = ($userPath -split ';' | ForEach-Object { $_.TrimEnd('\') }) -contains $InstallDir.TrimEnd('\')
if (-not $onPath) {
    [Environment]::SetEnvironmentVariable("PATH", "$InstallDir;$userPath", "User")
    Write-Host "  Added $InstallDir to user PATH (restart terminals to pick it up)"
}

# ── Step 5: restart the daemon ──────────────────────────────────────────────
if (-not $NoRestart -and ($Component -eq "all" -or $Component -eq "daemon")) {
    Write-Host "==> Starting daemon..." -ForegroundColor Yellow
    try {
        & $CliExe daemon start | Out-Host
    } catch {
        Write-Host "  Daemon start failed: $_"
    }
}

Write-Host ""
Write-Host "Deploy complete." -ForegroundColor Green
& $CliExe version | Out-Host
