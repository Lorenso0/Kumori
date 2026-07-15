@echo off
call "%~dp0build-app.cmd" run
exit /b %errorlevel%
