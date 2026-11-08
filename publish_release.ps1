<#
.SYNOPSIS
    Automated GitHub Release publisher for codesys-mcp-plugin.

.DESCRIPTION
    Builds the latest binaries, runs tests (optional), packages release zip,
    extracts version notes from CHANGELOG.md, and creates a GitHub Release.
    - Target Private: Publishes a Pre-release to Zhgong/codesys-mcp-plugin-private (Default)
    - Target Public:  Publishes a Latest Release to Zhgong/codesys-mcp-plugin (Requires explicit -Force or confirmation)

.PARAMETER Target
    Destination repository: 'Private' (default) or 'Public'.

.PARAMETER SkipTests
    Skip running unit & protocol tests before publishing.

.PARAMETER Draft
    Create the release as a draft instead of immediately publishing.

.PARAMETER Force
    Required for Target Public to prevent accidental public releases.
#>

[CmdletBinding()]
param(
    [ValidateSet("Private", "Public")]
    [string]$Target = "Private",

    [switch]$SkipTests,
    [switch]$Draft,
    [switch]$Force,
    [string]$Tag = "",
    [string]$Title = ""
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path

# 1. Target repository and policy check
$PrivateRepo = "Zhgong/codesys-mcp-plugin-private"
$PublicRepo  = "Zhgong/codesys-mcp-plugin"

if ($Target -eq "Public") {
    $Repo = $PublicRepo
    $IsPreRelease = $false
    Write-Host "`n[!] TARGET IS PUBLIC REPOSITORY: $PublicRepo" -ForegroundColor Yellow
    if (-not $Force) {
        $confirm = Read-Host "Are you sure you want to publish a public release? Type 'YES' to continue"
        if ($confirm -ne "YES") {
            Write-Host "Aborted by user." -ForegroundColor Red
            exit 1
        }
    }
} else {
    $Repo = $PrivateRepo
    $IsPreRelease = $true
}

# 2. Get current version
$versionFile = Join-Path $Root "VERSION"
if (-not (Test-Path $versionFile)) {
    Write-Error "VERSION file not found at $versionFile"
}
$version = (Get-Content $versionFile -Raw).Trim()
if ([string]::IsNullOrWhiteSpace($Tag)) {
    $Tag = "v$version"
}
if ([string]::IsNullOrWhiteSpace($Title)) {
    if ($IsPreRelease) {
        $Title = "v$version (Pre-release)"
    } else {
        $Title = "v$version"
    }
}

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " Publishing Release: $Tag -> $Repo" -ForegroundColor Cyan
Write-Host " Type: $(if ($IsPreRelease) { 'Pre-release' } else { 'Latest Release' })" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 3. Run Tests
if (-not $SkipTests) {
    Write-Host "`n[Step 1/5] Running tests..." -ForegroundColor Green
    $testScript = Join-Path $Root "run_tests.ps1"
    if (Test-Path $testScript) {
        & powershell -ExecutionPolicy Bypass -File $testScript
        if ($LASTEXITCODE -ne 0) {
            Write-Error "Tests failed! Aborting release."
        }
    }
} else {
    Write-Host "`n[Step 1/5] Skipping tests (-SkipTests specified)" -ForegroundColor Gray
}

# 4. Package binaries
Write-Host "`n[Step 2/5] Packaging release binaries..." -ForegroundColor Green
$packageScript = Join-Path $Root "package_release.ps1"
& powershell -ExecutionPolicy Bypass -File $packageScript
if ($LASTEXITCODE -ne 0) {
    Write-Error "Packaging failed! Aborting release."
}

$distZip = Join-Path $Root "dist\codesys-mcp-plugin-v$version.zip"
if (-not (Test-Path $distZip)) {
    Write-Error "Release package not found: $distZip"
}
Write-Host "Verified release asset: $distZip" -ForegroundColor Green

# 5. Extract release notes from CHANGELOG.md
Write-Host "`n[Step 3/5] Extracting release notes from CHANGELOG.md..." -ForegroundColor Green
$changelogFile = Join-Path $Root "CHANGELOG.md"
$notesContent = ""
if (Test-Path $changelogFile) {
    $changelog = Get-Content $changelogFile -Raw
    $pattern = '(?s)## \[' + [regex]::Escape($version) + '\][^\r\n]*\r?\n(.*?)(?=\r?\n## \[|\Z)'
    if ($changelog -match $pattern) {
        $notesContent = $matches[1].Trim()
    }
}

if ([string]::IsNullOrWhiteSpace($notesContent)) {
    $notesContent = "Release $Tag"
}

$tempNotesFile = [System.IO.Path]::GetTempFileName()
Set-Content -Path $tempNotesFile -Value $notesContent -Encoding UTF8
Write-Host "Extracted notes preview:" -ForegroundColor DarkGray
Write-Host ($notesContent | Out-String).Trim() -ForegroundColor DarkGray

# 6. Ensure Git Tag exists locally and on remote
Write-Host "`n[Step 4/5] Checking Git Tag $Tag..." -ForegroundColor Green
$existingTags = git -C $Root tag -l $Tag
if (-not $existingTags) {
    Write-Host "Creating local tag: $Tag" -ForegroundColor Yellow
    git -C $Root tag -a $Tag -m "Release $Tag"
}

$remoteName = if ($Target -eq "Public") { "public" } else { "origin" }
Write-Host "Pushing tag $Tag to $remoteName..." -ForegroundColor Green
git -C $Root push $remoteName $Tag --force
if ($LASTEXITCODE -ne 0) {
    Write-Warning "Could not push tag directly to $remoteName (or already up to date)."
}

# 7. Create GitHub Release via gh CLI
Write-Host "`n[Step 5/5] Creating GitHub Release via gh CLI..." -ForegroundColor Green
$ghArgs = @(
    "release", "create", $Tag,
    $distZip,
    "--repo", $Repo,
    "--title", $Title,
    "--notes-file", $tempNotesFile
)

if ($IsPreRelease) {
    $ghArgs += "--prerelease"
} else {
    $ghArgs += "--latest"
}

if ($Draft) {
    $ghArgs += "--draft"
}

& gh @ghArgs
if ($LASTEXITCODE -ne 0) {
    # If release already exists, attempt to update/upload asset
    Write-Warning "gh release create failed. Checking if release already exists..."
    & gh release upload $Tag $distZip --repo $Repo --clobber
}

Remove-Item -Force $tempNotesFile -ErrorAction SilentlyContinue

Write-Host "`n==========================================================" -ForegroundColor Cyan
Write-Host " Release successfully processed for $Repo ($Tag)!" -ForegroundColor Green
Write-Host " View online at: https://github.com/$Repo/releases/tag/$Tag" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan
