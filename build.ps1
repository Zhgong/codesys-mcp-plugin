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

$csc = "C:\Program Files (x86)\Microsoft Visual Studio\18\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"
$common = "C:\Program Files\CODESYS 3.5.20.50\CODESYS\Common"
$framework = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"

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

$outDir = Join-Path $Root "bin\Release"
if (-not (Test-Path $outDir)) {
    New-Item -ItemType Directory -Force -Path $outDir | Out-Null
}
$outFile = Join-Path $outDir "CodesysMcp.dll"

Write-Host "Compiling CodesysMcp.dll (v$version) with Roslyn..."
$allArgs = @("/target:library", "/optimize+", "/out:`"$outFile`"") + $refArgs + $srcFiles

& $csc $allArgs

if ($LASTEXITCODE -ne 0 -or -not (Test-Path $outFile)) {
    Write-Error "Build CodesysMcp.dll FAILED!"
}

Write-Host "Build SUCCESS: $outFile" -ForegroundColor Green

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
