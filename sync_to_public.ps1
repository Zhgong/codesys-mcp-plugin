<#
.SYNOPSIS
    Safe Public Release Synchronization Tool
.DESCRIPTION
    Safely synchronizes approved commits from private development to the public repository.
    Enforces the Gatekeeper security checks before prompting for confirmation.
    Supports -FilterIgnored to automatically strip internal files (wiki/, roadmaps) during release.
#>
[CmdletBinding()]
param (
    [string]$Branch = "",
    [switch]$DryRun = $false,
    [switch]$SkipTests = $false,
    [switch]$IncludeTags = $false,
    [switch]$FilterIgnored = $false,
    [switch]$Squash = $false,
    [string]$SquashMessage = "",
    [switch]$Force = $false
)

$ErrorActionPreference = "Continue"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

if (-not $Branch) {
    $Branch = (git -C $ScriptDir branch --show-current).Trim()
}

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " [PUBLIC RELEASE SYNC] CODESYS MCP Plugin Release Tool" -ForegroundColor Cyan
Write-Host " Source Branch : $Branch" -ForegroundColor Gray
Write-Host " Target Remote : public (https://github.com/Zhgong/codesys-mcp-plugin.git)" -ForegroundColor Gray
Write-Host " Filter Mode   : $(if ($FilterIgnored) { 'AUTO-STRIP (.publicignore)' } else { 'STRICT CHECK' })" -ForegroundColor Gray
Write-Host " Squash Mode   : $(if ($Squash) { 'ENABLED (Single squashed release commit)' } else { 'DISABLED (Preserve commits)' })" -ForegroundColor Gray
Write-Host " Mode          : $(if ($DryRun) { 'DRY RUN (Preview Only)' } else { 'LIVE SYNC' })" -ForegroundColor Gray
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Ensure working tree is clean
$status = git -C $ScriptDir status --porcelain
if ($status) {
    Write-Host "[FAIL] Working tree has uncommitted changes! Please commit or stash before syncing to public." -ForegroundColor Red
    git -C $ScriptDir status -s
    exit 1
}

# 2. If FilterIgnored or Squash is requested, prepare clean public-release branch
$originalBranch = $Branch
$pushBranch = $Branch

if ($FilterIgnored -or $Squash) {
    Write-Host "`n>>> [RELEASE PREPARATION] Creating clean public release tree..." -ForegroundColor Cyan
    $releaseBranch = "public-release-$Branch"
    $hasPublicRef = (git -C $ScriptDir rev-parse --verify --quiet "public/$Branch")

    if ($Squash -and $hasPublicRef) {
        Write-Host "   - Squashing commits onto public/$Branch base..." -ForegroundColor Cyan
        git -C $ScriptDir checkout -B $releaseBranch "public/$Branch" 2>$null
        git -C $ScriptDir merge --squash $Branch 2>$null
    } else {
        # Check out release branch from current branch
        git -C $ScriptDir checkout -B $releaseBranch $Branch 2>$null
    }

    # Load publicignore patterns
    if ($FilterIgnored) {
        $publicIgnorePath = Join-Path $ScriptDir ".publicignore"
        if (Test-Path $publicIgnorePath) {
            $patterns = Get-Content $publicIgnorePath | Where-Object { $_ -and -not $_.StartsWith("#") }
            foreach ($pat in $patterns) {
                $p = $pat.Trim().TrimEnd("/")
                if (Test-Path (Join-Path $ScriptDir $p)) {
                    git -C $ScriptDir rm -r -f --quiet $p 2>$null
                    Write-Host "   - Stripped: $p" -ForegroundColor Yellow
                }
            }
        }
    }

    $filterStatus = git -C $ScriptDir status --porcelain
    if ($filterStatus) {
        $shortRev = (git -C $ScriptDir rev-parse --short $Branch).Trim()
        $commitMsg = if ($SquashMessage) {
            $SquashMessage
        } elseif ($Squash) {
            "release: sync squashed updates from $Branch ($shortRev)"
        } else {
            "chore(release): strip internal wiki and planning docs for public release"
        }
        git -C $ScriptDir commit -m $commitMsg --quiet
        Write-Host "   [DONE] Clean release commit created on $releaseBranch." -ForegroundColor Green
    }
    $pushBranch = $releaseBranch
}

try {
    # 3. Run Gatekeeper Checks
    $gateScript = Join-Path $ScriptDir "check_public_gate.ps1"
    $gateArgs = @("-Branch", $pushBranch, "-Remote", "public")
    if ($SkipTests) { $gateArgs += "-SkipTests" }

    & powershell -ExecutionPolicy Bypass -File $gateScript @gateArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Host "`n[ERROR] Gatekeeper validation failed! Sync to public repository has been aborted." -ForegroundColor Red
        if (-not $FilterIgnored) {
            Write-Host "[TIP] If the rejection was caused by internal docs (e.g. wiki/), run with -FilterIgnored to publish without them:" -ForegroundColor Yellow
            Write-Host "      powershell ./sync_to_public.ps1 -FilterIgnored`n" -ForegroundColor Yellow
        }
        exit 1
    }

    # 4. Review outgoing commits
    Write-Host "`n>>> Outgoing commits to be merged into public/${Branch}:" -ForegroundColor Cyan
    $outgoing = git -C $ScriptDir log "public/${Branch}..$pushBranch" --oneline
    if ($outgoing) {
        $outgoing | ForEach-Object { Write-Host "   $_" -ForegroundColor Green }
    } else {
        Write-Host "   (Public repository is already up to date with $Branch. No new commits to push.)" -ForegroundColor Yellow
        return
    }

    # 5. Check DryRun
    if ($DryRun) {
        Write-Host "`n[DRY RUN COMPLETE] Gate passed and outgoing commits verified. No changes were pushed to public." -ForegroundColor Yellow
        return
    }

    # 6. Prompt for explicit confirmation
    if (-not $Force) {
        Write-Host ""
        $confirmation = Read-Host "Are you sure you want to push these commits to the PUBLIC repository? (yes/no)"
        if ($confirmation -ne "yes" -and $confirmation -ne "y") {
            Write-Host "[ABORTED] Public sync cancelled by user." -ForegroundColor Yellow
            return
        }
    }

    # 7. Execute push
    Write-Host "`n>>> Pushing $pushBranch to public/$Branch..." -ForegroundColor Cyan
    $pushArgs = @("push")
    if ($SkipTests) { $pushArgs += "--no-verify" }
    if ($FilterIgnored) { $pushArgs += "--force-with-lease" }
    $pushArgs += @("public", "${pushBranch}:${Branch}")
    git -C $ScriptDir @pushArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Host "[ERROR] git push public failed!" -ForegroundColor Red
        exit 1
    }

    if ($IncludeTags) {
        Write-Host ">>> Pushing tags to public..." -ForegroundColor Cyan
        git -C $ScriptDir push public --tags
    }

    Write-Host "`n[SUCCESS] Successfully synced to the public repository!" -ForegroundColor Green
}
finally {
    if (($FilterIgnored -or $Squash) -and (git -C $ScriptDir branch --show-current).Trim() -ne $originalBranch) {
        git -C $ScriptDir checkout $originalBranch --quiet 2>$null
    }
}
