$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$versionFile = Join-Path $Root "VERSION"
$version = if (Test-Path $versionFile) { (Get-Content $versionFile -Raw).Trim() } else { "1.1.0" }
$fileVersion = "$version.0"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " Building CodesysMcp v$version" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan

# Sync version to AssemblyInfo.cs and Program.cs
$asmInfoPath = Join-Path $Root "CodesysMcp\Properties\AssemblyInfo.cs"
if (Test-Path $asmInfoPath) {
    $asmContent = Get-Content $asmInfoPath -Raw
    $asmContent = $asmContent -replace 'AssemblyVersion\("[^"]+"\)', "AssemblyVersion(`"$fileVersion`")"
    $asmContent = $asmContent -replace 'AssemblyFileVersion\("[^"]+"\)', "AssemblyFileVersion(`"$fileVersion`")"
    $asmContent = $asmContent -replace 'AssemblyInformationalVersion\("[^"]+"\)', "AssemblyInformationalVersion(`"$version`")"
    Set-Content -Path $asmInfoPath -Value $asmContent -Encoding UTF8 -NoNewline
}

$cliSrcPath = Join-Path $Root "CodesysMcpCli\Program.cs"
if (Test-Path $cliSrcPath) {
    $cliContent = Get-Content $cliSrcPath -Raw
    $cliContent = $cliContent -replace 'AssemblyVersion\("[^"]+"\)', "AssemblyVersion(`"$fileVersion`")"
    $cliContent = $cliContent -replace 'AssemblyFileVersion\("[^"]+"\)', "AssemblyFileVersion(`"$fileVersion`")"
    Set-Content -Path $cliSrcPath -Value $cliContent -Encoding UTF8 -NoNewline
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

function Find-CodesysCommon {
    param([string]$repoRoot)
    $candidates = @(
        $env:CODESYS_COMMON_PATH,
        (Join-Path $repoRoot "lib"),
        "C:\Program Files\CODESYS 3.5.20.50\CODESYS\Common",
        "C:\Program Files\CODESYS 3.5.21.0\CODESYS\Common",
        "C:\Program Files (x86)\CODESYS 3.5.20.50\CODESYS\Common"
    )

    $codesysDirs = Get-ChildItem "C:\Program Files\CODESYS*" -ErrorAction SilentlyContinue
    if ($codesysDirs) {
        foreach ($cd in $codesysDirs) {
            $candidates += (Join-Path $cd.FullName "CODESYS\Common")
        }
    }

    foreach ($c in $candidates) {
        if ($c -and (Test-Path $c)) {
            $s3 = Join-Path $c "ScriptEngine3.dll"
            $cm = Join-Path $c "ComponentModel.dll"
            if ((Test-Path $s3) -and (Test-Path $cm)) {
                return $c
            }
        }
    }
    return $null
}

$csc = Find-Csc
$framework = Find-Framework
$common = Find-CodesysCommon -repoRoot $Root

$outDir = Join-Path $Root "bin\Release"
if (-not (Test-Path $outDir)) {
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
}
$outFile = Join-Path $outDir "CodesysMcp.dll"

if ($common) {
    Write-Host "CODESYS assemblies located at: $common" -ForegroundColor Gray
    $refs = @(
        (Join-Path $framework "mscorlib.dll"),
        (Join-Path $framework "System.dll"),
        (Join-Path $framework "System.Core.dll"),
        (Join-Path $framework "System.Web.Extensions.dll"),
        (Join-Path $framework "Microsoft.CSharp.dll"),
        (Join-Path $common "ScriptEngine3.dll"),
        (Join-Path $common "ComponentModel.dll"),
        (Join-Path $common "Core.dll"),
        (Join-Path $common "MessageStorage.dll")
    )

    $refArgs = $refs | ForEach-Object { "/reference:`"$_`"" }
    $srcFiles = Get-ChildItem -Path (Join-Path $Root "CodesysMcp") -Recurse -Filter "*.cs" | ForEach-Object { "`"$($_.FullName)`"" }

    Write-Host "Compiling CodesysMcp.dll (v$version) with Roslyn..."
    $allArgs = @("/target:library", "/optimize+", "/out:`"$outFile`"") + $refArgs + $srcFiles
    & $csc $allArgs

    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $outFile)) {
        Write-Error "Build CodesysMcp.dll FAILED!"
    }

    Write-Host "Build SUCCESS: $outFile" -ForegroundColor Green
} else {
    if (Test-Path $outFile) {
        Write-Host "CODESYS assemblies not found; using existing pre-compiled $outFile." -ForegroundColor Yellow
    } else {
        Write-Error "CODESYS assemblies not found (ScriptEngine3.dll, ComponentModel.dll) and no precompiled CodesysMcp.dll exists at $outFile."
    }
}

# Build CLI Proxy
$cliSrc = (Get-Item $cliSrcPath).FullName
$cliOut = Join-Path $outDir "CodesysMcpCli.exe"
$cliRefs = @(
    (Join-Path $framework "mscorlib.dll"),
    (Join-Path $framework "System.dll"),
    (Join-Path $framework "System.Core.dll")
) | ForEach-Object { "/reference:`"$_`"" }

Write-Host "Compiling CodesysMcpCli.exe (v$version)..."
$cliArgs = @("/target:exe", "/optimize+", "/out:`"$cliOut`"") + $cliRefs + @("`"$cliSrc`"")
& $csc $cliArgs

if ($LASTEXITCODE -eq 0 -and (Test-Path $cliOut)) {
    Write-Host "Build SUCCESS: $cliOut" -ForegroundColor Green
    Get-Item $outFile, $cliOut | Select-Object FullName, Length, LastWriteTime
} else {
    Write-Error "Build CodesysMcpCli.exe FAILED!"
}
