@echo off
call "%~dp0build-app.cmd" publish
exit /b %errorlevel%
