@echo off
REM Runs the licensing test harness. Needs the .NET 8 SDK on PATH.
REM Exit code 0 = all passed, 1 = something failed.
setlocal
cd /d "%~dp0"
echo Running Whatever IT PC Care licensing tests...
echo.
dotnet run -c Release
set RESULT=%ERRORLEVEL%
echo.
if "%RESULT%"=="0" (
  echo All licensing tests PASSED.
) else (
  echo Some licensing tests FAILED ^(exit code %RESULT%^).
)
echo.
pause
exit /b %RESULT%
