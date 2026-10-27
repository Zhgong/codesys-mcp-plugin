param(
    [switch]$IncludeE2E,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " Running Complete Test Suite for codesys-mcp-plugin" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# 1. Build if not skipped
if (-not $SkipBuild) {
    Write-Host "`n>>> [Step 1/3] Building codesys-mcp-plugin binaries..." -ForegroundColor Yellow
    & (Join-Path $Root "build.ps1")
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Build failed!"
    }
}

# 2. Build C# Tests
Write-Host "`n>>> [Step 2/3] Compiling and running C# Core Unit Tests..." -ForegroundColor Yellow
$csc = "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"
$framework = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
$mcpDll = (Resolve-Path (Join-Path $Root "bin\Release\CodesysMcp.dll")).Path
$testOut = Join-Path $Root "bin\Release\CodesysMcpTests.exe"

$refs = @(
    (Join-Path $framework "mscorlib.dll"),
    (Join-Path $framework "System.dll"),
    (Join-Path $framework "System.Core.dll"),
    (Join-Path $framework "System.Web.Extensions.dll"),
    (Join-Path $framework "Microsoft.CSharp.dll"),
    $mcpDll
) | ForEach-Object { "/reference:`"$_`"" }

$srcFiles = Get-ChildItem -Path (Join-Path $Root "CodesysMcpTests") -Filter "*.cs" | ForEach-Object { "`"$($_.FullName)`"" }
$cscArgs = @("/target:exe", "/optimize+", "/out:`"$testOut`"") + $refs + $srcFiles

& $csc $cscArgs
if ($LASTEXITCODE -ne 0) {
    Write-Error "Compiling CodesysMcpTests.exe failed!"
}

# Execute C# unit tests
& $testOut
$csharpExit = $LASTEXITCODE
if ($csharpExit -ne 0) {
    Write-Error "C# Unit Tests failed!"
}

# 3. Run Python Integration Tests
Write-Host "`n>>> [Step 3/3] Running Python Protocol Integration Tests..." -ForegroundColor Yellow
$origLoc = Get-Location
try {
    Set-Location $Root
    if ($IncludeE2E) {
        python -m unittest discover tests -v
    } else {
        python -m unittest tests/test_cli_protocol.py -v
    }
    $pythonExit = $LASTEXITCODE
} finally {
    Set-Location $origLoc
}

Write-Host "`n==========================================================" -ForegroundColor Cyan
if ($csharpExit -eq 0 -and $pythonExit -eq 0) {
    Write-Host " ALL TEST SUITES PASSED SUCCESSFULLY! " -ForegroundColor Green
    Write-Host "==========================================================" -ForegroundColor Cyan
    exit 0
} else {
    Write-Host " TEST FAILURES DETECTED! " -ForegroundColor Red
    Write-Host "==========================================================" -ForegroundColor Cyan
    exit 1
}
