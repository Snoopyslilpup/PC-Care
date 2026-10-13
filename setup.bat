@echo off
REM =====================================================================
REM  Whatever IT PC Care - double-click this to install everything and
REM  launch the app. No manual setup required.
REM ---------------------------------------------------------------------
REM   setup.bat             Install (if needed), build (first time), LAUNCH
REM   setup.bat rebuild     Force a clean rebuild, then launch
REM   setup.bat build       Just build the sellable .exe (no launch)
REM   setup.bat installer  Build the app, auto-get Inno Setup, compile the
REM                        installer, and OPEN it so you see the wizard
REM   setup.bat keygen "Name" BASIC            Issue a lifetime key
REM   setup.bat keygen "Name" PRO 2027-01-31   Issue a subscription key
REM =====================================================================
cd /d "%~dp0"

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup.ps1" %*
set RC=%errorlevel%

if not "%RC%"=="0" (
  echo.
  echo Setup reported a problem ^(code %RC%^). See the messages above.
)
echo.
pause
exit /b %RC%
