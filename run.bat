@echo off
REM Developer launcher: always rebuilds current source, then runs that build.
REM build-app.cmd stops any resident Debug instance that would lock stale files.
call "%~dp0build-app.cmd" run
exit /b %errorlevel%
