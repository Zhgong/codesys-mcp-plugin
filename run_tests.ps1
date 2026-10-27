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

function Find-Csc {
    if ($env:CSC_PATH -and (Test-Path $env:CSC_PATH)) {
        return $env:CSC_PATH
    }

    $cmd = Get-Command csc.exe -ErrorAction SilentlyContinue
    if ($cmd -and (Test-Path $cmd.Source)) {
        return $cmd.Source
    }

    $vswherePaths = @(
        "vswhere.exe",
        "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe",
        "${env:ProgramFiles}\Microsoft Visual Studio\Installer\vswhere.exe"
    )
    foreach ($vp in $vswherePaths) {
        $found = Get-Command $vp -ErrorAction SilentlyContinue
        $exe = if ($found) { $found.Source } elseif (Test-Path $vp) { $vp } else { $null }
        if ($exe) {
            $installPath = & $exe -latest -products * -requires Microsoft.Component.MSBuild -property installationPath
            if (-not $installPath) {
                $installPath = & $exe -latest -products * -property installationPath
            }
            if ($installPath) {
                $candidates = @(
                    (Join-Path $installPath "MSBuild\Current\Bin\Roslyn\csc.exe"),
                    (Join-Path $installPath "MSBuild\Current\Bin\csc.exe")
                )
                foreach ($c in $candidates) {
                    if (Test-Path $c) { return $c }
                }
            }
        }
    }

    $fallbacks = @(
        "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\Roslyn\csc.exe",
        "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\Roslyn\csc.exe",
        "C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\Roslyn\csc.exe",
        "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe",
        "C:\Program Files (x86)\Microsoft Visual Studio\2019\Enterprise\MSBuild\Current\Bin\Roslyn\csc.exe",
        "C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe",
        "C:\Program Files (x86)\Microsoft Visual Studio\2019\Community\MSBuild\Current\Bin\Roslyn\csc.exe",
        "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
        "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
    )
    foreach ($f in $fallbacks) {
        if (Test-Path $f) { return $f }
    }

    throw "Could not locate csc.exe (C# Compiler). Please install Visual Studio or Build Tools."
}

function Find-Framework {
    $frameworkPaths = @(
        "C:\Windows\Microsoft.NET\Framework64\v4.0.30319",
        "C:\Windows\Microsoft.NET\Framework\v4.0.30319"
    )
    foreach ($fp in $frameworkPaths) {
        if (Test-Path $fp) { return $fp }
    }
    throw "Could not locate .NET Framework 4.0/4.8 directory."
}

# 2. Build C# Tests
Write-Host "`n>>> [Step 2/3] Compiling and running C# Core Unit Tests..." -ForegroundColor Yellow
$csc = Find-Csc
$framework = Find-Framework
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
