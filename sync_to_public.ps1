<#
.SYNOPSIS
    Safe Public Release Synchronization Tool
.DESCRIPTION
    Safely synchronizes approved commits from private development to the public repository.
    Enforces the Gatekeeper security checks before prompting for confirmation.
#>
[CmdletBinding()]
param (
    [string]$Branch = "",
    [switch]$DryRun = $false,
    [switch]$SkipTests = $false,
    [switch]$IncludeTags = $false
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not $Branch) {
    $Branch = (git -C $ScriptDir branch --show-current).Trim()
}

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " [PUBLIC RELEASE SYNC] CODESYS MCP Plugin Release Tool" -ForegroundColor Cyan
Write-Host " Source Branch : $Branch" -ForegroundColor Gray
Write-Host " Target Remote : public (https://github.com/Zhgong/codesys-mcp-plugin.git)" -ForegroundColor Gray
Write-Host " Mode          : $(if ($DryRun) { 'DRY RUN (Preview Only)' } else { 'LIVE SYNC' })" -ForegroundColor Gray
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Ensure working tree is clean
$status = git -C $ScriptDir status --porcelain
if ($status) {
    Write-Host "[FAIL] Working tree has uncommitted changes! Please commit or stash before syncing to public." -ForegroundColor Red
    git -C $ScriptDir status -s
    exit 1
}

# 2. Run Gatekeeper Checks
$gateScript = Join-Path $ScriptDir "check_public_gate.ps1"
$gateArgs = @("-Branch", $Branch, "-Remote", "public")
if ($SkipTests) { $gateArgs += "-SkipTests" }

& powershell -ExecutionPolicy Bypass -File $gateScript @gateArgs
if ($LASTEXITCODE -ne 0) {
    Write-Host "`n[ERROR] Gatekeeper validation failed! Sync to public repository has been aborted." -ForegroundColor Red
    exit 1
}

# 3. Review outgoing commits
Write-Host "`n>>> Outgoing commits to be merged into public/${Branch}:" -ForegroundColor Cyan
$outgoing = git -C $ScriptDir log "public/${Branch}..HEAD" --oneline
if ($outgoing) {
    $outgoing | ForEach-Object { Write-Host "   $_" -ForegroundColor Green }
} else {
    Write-Host "   (Public repository is already up to date with $Branch. No new commits to push.)" -ForegroundColor Yellow
    exit 0
}

# 4. Check DryRun
if ($DryRun) {
    Write-Host "`n[DRY RUN COMPLETE] Gate passed and outgoing commits verified. No changes were pushed to public." -ForegroundColor Yellow
    exit 0
}

# 5. Prompt for explicit confirmation
Write-Host ""
$confirmation = Read-Host "Are you sure you want to push these commits to the PUBLIC repository? (yes/no)"
if ($confirmation -ne "yes" -and $confirmation -ne "y") {
    Write-Host "[ABORTED] Public sync cancelled by user." -ForegroundColor Yellow
    exit 0
}

# 6. Execute push
Write-Host "`n>>> Pushing $Branch to public..." -ForegroundColor Cyan
git -C $ScriptDir push public "$Branch"
if ($LASTEXITCODE -ne 0) {
    Write-Host "[ERROR] git push public failed!" -ForegroundColor Red
    exit 1
}

if ($IncludeTags) {
    Write-Host ">>> Pushing tags to public..." -ForegroundColor Cyan
    git -C $ScriptDir push public --tags
}

Write-Host "`n[SUCCESS] Successfully synced $Branch to the public repository!" -ForegroundColor Green
