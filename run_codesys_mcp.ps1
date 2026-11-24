param(
    [switch]$NoUI = $false,
    [string]$ProjectPath = ""
)

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$codesysExe = "C:\Program Files\CODESYS 3.5.20.50\CODESYS\Common\CODESYS.exe"
$profile = "CODESYS V3.5 SP20 Patch 5"
$scriptPath = Join-Path $Root "codesys_mcp_session.py"

Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host " Launching CODESYS with Native C# Stdio MCP Plugin" -ForegroundColor Cyan
Write-Host "==========================================================" -ForegroundColor Cyan
Write-Host "Executable : $codesysExe"
Write-Host "Profile    : $profile"
Write-Host "Bootstrap  : $scriptPath"
Write-Host "Mode       : $(if ($NoUI) { 'Headless (--noUI)' } else { 'Interactive CODESYS IDE (GUI)' })"
Write-Host "Named Pipe : \\.\pipe\codesys_mcp_pipe"
Write-Host "Stdio CLI  : $(Join-Path $Root 'bin\Release\CodesysMcpCli.exe')"

$argsList = @(
    "--profile=`"$profile`"",
    "--runscript=`"$scriptPath`""
)

if ($NoUI) {
    $argsList += "--noUI"
}

if ($ProjectPath -and (Test-Path $ProjectPath)) {
    $argsList += "`"$ProjectPath`""
}

Start-Process -FilePath $codesysExe -ArgumentList $argsList
Write-Host "`nCODESYS launched! Once initialized, AI clients can connect via CodesysMcpCli.exe (Stdio)." -ForegroundColor Green
