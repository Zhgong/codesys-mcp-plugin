$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$version = (Get-Content (Join-Path $Root "VERSION") -Raw).Trim()
$distDir = Join-Path $Root "dist"
$stageDir = Join-Path $distDir "codesys-plugin-v$version"
$zipFile = Join-Path $distDir "codesys-plugin-v$version.zip"

# Ensure latest build
& (Join-Path $Root "build.ps1")

if (Test-Path $stageDir) { Remove-Item -Recurse -Force $stageDir }
if (Test-Path $zipFile) { Remove-Item -Force $zipFile }

New-Item -ItemType Directory -Force -Path (Join-Path $stageDir "bin\Release") | Out-Null

Copy-Item (Join-Path $Root "bin\Release\CodesysMcp.dll") (Join-Path $stageDir "bin\Release\")
Copy-Item (Join-Path $Root "bin\Release\CodesysMcpCli.exe") (Join-Path $stageDir "bin\Release\")
Copy-Item (Join-Path $Root "codesys_mcp_session.py") $stageDir
Copy-Item (Join-Path $Root "start_codesys_ide_with_mcp.bat") $stageDir
Copy-Item (Join-Path $Root "run_codesys_mcp.ps1") $stageDir
Copy-Item (Join-Path $Root "launch_mcp.py") $stageDir
Copy-Item (Join-Path $Root "README.md") $stageDir
Copy-Item (Join-Path $Root "CHANGELOG.md") $stageDir
Copy-Item (Join-Path $Root "VERSION") $stageDir

Compress-Archive -Path "$stageDir\*" -DestinationPath $zipFile -Force
Remove-Item -Recurse -Force $stageDir

Write-Host "`nRelease package created: $zipFile" -ForegroundColor Cyan
Get-Item $zipFile | Select-Object FullName, Length, LastWriteTime
