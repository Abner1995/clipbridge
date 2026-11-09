@echo off
rem CreatFlow dev launcher - STANDALONE edition (client only, local SQLite).
rem Requires the STANDALONE tree: NewApp.csproj WITH CREATFLOW_STANDALONE
rem (edition/standalone). On online branches this exits with a clear error.
rem Usage: scripts\dev-standalone.cmd [options] [-- client-args]
setlocal
where python >nul 2>nul
if errorlevel 1 goto use_py
python "%~dp0dev.py" --edition standalone %*
goto done
:use_py
py -3 "%~dp0dev.py" --edition standalone %*
:done
set "rc=%errorlevel%"
rem double-click friendly: keep the window open on launcher errors (1/2),
rem but not on Ctrl+C (130) or a normal client exit.
if %rc% GEQ 1 if %rc% LSS 3 pause
exit /b %rc%
