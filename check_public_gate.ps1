<#
.SYNOPSIS
    Public Repository Gatekeeper Verification Script
.DESCRIPTION
    Validates that changes intended for the public repository meet security and quality standards:
    1. Checks for forbidden paths (wiki/, roadmap/, docs/internal/, *.key, etc.)
    2. Scans diffs for sensitive tokens, passwords, and private API keys
    3. Runs the automated test suite (run_tests.ps1)
#>
[CmdletBinding()]
param (
    [string]$Branch = "",
    [string]$Remote = "public",
    [switch]$SkipTests = $false,
    [string]$RulesFile = ".public_gate_rules.json"
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# 1. Determine active branch if not specified
if (-not $Branch) {
    $Branch = (git -C $ScriptDir branch --show-current).Trim()
}

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " [PUBLIC GATEKEEPER] Running Security & Quality Gate" -ForegroundColor Cyan
Write-Host " Target Remote : $Remote" -ForegroundColor Gray
Write-Host " Branch        : $Branch" -ForegroundColor Gray
Write-Host "==========================================================" -ForegroundColor Cyan

# 2. Load rules
$RulesPath = Join-Path $ScriptDir $RulesFile
if (-not (Test-Path $RulesPath)) {
    Write-Host "[ERROR] Gate rules configuration not found at: $RulesPath" -ForegroundColor Red
    exit 1
}

$rules = Get-Content $RulesPath -Raw | ConvertFrom-Json
$forbiddenGlobs = $rules.forbiddenPaths
$secretRegexes = $rules.secretPatterns

$violations = @()

# 3. Determine base reference for diff
$remoteRef = "$Remote/$Branch"
$hasRemoteRef = (git -C $ScriptDir rev-parse --verify --quiet $remoteRef)

if ($hasRemoteRef) {
    Write-Host "--> Comparing diff against: $remoteRef" -ForegroundColor Gray
    $committedFiles = git -C $ScriptDir diff --name-only "$remoteRef..HEAD"
    $stagedFiles = git -C $ScriptDir diff --name-only --cached
    $changedFiles = @($committedFiles) + @($stagedFiles) | Where-Object { $_ } | Select-Object -Unique
    $diffText = (git -C $ScriptDir diff "$remoteRef..HEAD") + "`n" + (git -C $ScriptDir diff --cached)
} else {
    Write-Host "--> Remote branch $remoteRef not found. Inspecting entire tree..." -ForegroundColor Yellow
    $changedFiles = git -C $ScriptDir ls-tree -r --name-only HEAD
    $diffText = git -C $ScriptDir diff
}

# ----------------------------------------------------
# Gate 1: Forbidden Paths Inspection (wiki/, roadmap/, etc.)
# ----------------------------------------------------
Write-Host "`n>>> [Gate 1/3] Checking forbidden directories and file paths..." -ForegroundColor Cyan

function Match-ForbiddenPattern ($filePath, $pattern) {
    $normalized = $filePath.Replace("\", "/")
    $normPattern = $pattern.Replace("\", "/")

    if ($normPattern.EndsWith("/**")) {
        $prefix = $normPattern.Substring(0, $normPattern.Length - 3)
        if ($normalized.StartsWith($prefix + "/") -or $normalized -eq $prefix) {
            return $true
        }
    }

    if ($normPattern.StartsWith("**/")) {
        $sub = $normPattern.Substring(3)
        if ($normalized.EndsWith("/$sub") -or $normalized -like "*$sub*") {
            return $true
        }
    }

    if ($normalized -like $normPattern) {
        return $true
    }

    return $false
}

$offendingPaths = @()
foreach ($file in $changedFiles) {
    if (-not $file) { continue }
    foreach ($glob in $forbiddenGlobs) {
        if (Match-ForbiddenPattern $file $glob) {
            $offendingPaths += [PSCustomObject]@{
                File = $file
                Rule = $glob
            }
            break
        }
    }
}

if ($offendingPaths.Count -gt 0) {
    Write-Host "[FAIL] Forbidden internal files detected! Public release CANNOT include these files:" -ForegroundColor Red
    foreach ($item in $offendingPaths) {
        Write-Host "   - $($item.File) (matched rule: $($item.Rule))" -ForegroundColor Red
    }
    $violations += "Forbidden paths detected: $($offendingPaths.Count) file(s)"
} else {
    Write-Host "   [PASS] No forbidden internal files (wiki/, roadmaps, credentials) detected." -ForegroundColor Green
}

# ----------------------------------------------------
# Gate 2: Secret & Sensitive Token Scanner
# ----------------------------------------------------
Write-Host "`n>>> [Gate 2/3] Scanning for sensitive tokens, passwords, and private keys..." -ForegroundColor Cyan

$secretViolations = @()
if ($diffText) {
    $lines = $diffText -split "`r?`n"
    foreach ($line in $lines) {
        if ($line.StartsWith("+") -and -not $line.StartsWith("+++")) {
            $content = $line.Substring(1)
            foreach ($pattern in $secretRegexes) {
                if ($content -match $pattern) {
                    $secretViolations += [PSCustomObject]@{
                        Line = $content.Trim()
                        Pattern = $pattern
                    }
                    break
                }
            }
        }
    }
}

if ($secretViolations.Count -gt 0) {
    Write-Host "[FAIL] Potential secrets or tokens detected in staged/outgoing commits:" -ForegroundColor Red
    foreach ($s in $secretViolations) {
        Write-Host "   - Potential Secret: $($s.Line)" -ForegroundColor Red
    }
    $violations += "Secrets detected in diff: $($secretViolations.Count) violation(s)"
} else {
    Write-Host "   [PASS] No secrets, tokens, or private credentials found in diff." -ForegroundColor Green
}

# ----------------------------------------------------
# Gate 3: Automated Test Suite (run_tests.ps1)
# ----------------------------------------------------
if (-not $SkipTests -and $rules.requireAllTestsPass) {
    Write-Host "`n>>> [Gate 3/3] Executing automated regression test suite..." -ForegroundColor Cyan
    $testScript = Join-Path $ScriptDir "run_tests.ps1"
    if (Test-Path $testScript) {
        & powershell -ExecutionPolicy Bypass -File $testScript -SkipBuild
        if ($LASTEXITCODE -ne 0) {
            Write-Host "[FAIL] Test suite failed! Exit code: $LASTEXITCODE" -ForegroundColor Red
            $violations += "Automated test suite failed with exit code $LASTEXITCODE"
        } else {
            Write-Host "   [PASS] All core unit tests and protocol tests passed." -ForegroundColor Green
        }
    } else {
        Write-Host "   [WARN] run_tests.ps1 not found, skipping automated test gate." -ForegroundColor Yellow
    }
} else {
    Write-Host "`n>>> [Gate 3/3] Automated test suite skipped (-SkipTests flag)." -ForegroundColor Yellow
}

# ----------------------------------------------------
# Final Verdict
# ----------------------------------------------------
Write-Host "`n==========================================================" -ForegroundColor Cyan
if ($violations.Count -gt 0) {
    Write-Host " [PUBLIC GATEKEEPER RESULT: REJECTED]" -ForegroundColor Red
    Write-Host " The following security/quality checks failed:" -ForegroundColor Red
    foreach ($v in $violations) {
        Write-Host "   * $v" -ForegroundColor Red
    }
    Write-Host " Push to $Remote has been BLOCKED." -ForegroundColor Red
    Write-Host "==========================================================" -ForegroundColor Cyan
    exit 1
} else {
    Write-Host " [PUBLIC GATEKEEPER RESULT: PASSED]" -ForegroundColor Green
    Write-Host " All security, secret scanning, and test gates passed." -ForegroundColor Green
    Write-Host " Safe to push to $Remote." -ForegroundColor Green
    Write-Host "==========================================================" -ForegroundColor Cyan
    exit 0
}
