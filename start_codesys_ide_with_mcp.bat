@echo off
set "ROOT=%~dp0"
set "CODESYS_EXE=C:\Program Files\CODESYS 3.5.20.50\CODESYS\Common\CODESYS.exe"
set "PROFILE=CODESYS V3.5 SP20 Patch 5"
set "SCRIPT=%ROOT%codesys_mcp_session.py"

echo [CODESYS-MCP] Starting CODESYS IDE (GUI) with C# Stdio MCP Plugin...
echo [CODESYS-MCP] Pipe: \\.\pipe\codesys_mcp_pipe
echo [CODESYS-MCP] Stdio CLI: %ROOT%bin\Release\CodesysMcpCli.exe

start "" "%CODESYS_EXE%" --profile="%PROFILE%" --runscript="%SCRIPT%" %*
