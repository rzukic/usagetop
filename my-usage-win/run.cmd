@echo off
rem Launches the Release build, or builds it first if it is missing.
setlocal
set EXE=%~dp0src\AiUsage\bin\Release\net9.0-windows\AiUsage.exe

if not exist "%EXE%" (
    echo Building...
    dotnet build "%~dp0src\AiUsage\AiUsage.csproj" -c Release -v q --nologo || goto :failed
)

start "" "%EXE%" %* >nul 2>&1
exit /b 0

:failed
echo Build failed.
pause
exit /b 1
