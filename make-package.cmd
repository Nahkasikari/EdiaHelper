@echo off
rem Builds EdiaHelper and puts an installable package folder on the Desktop (see make-package.ps1).
setlocal
call "%~dp0build.cmd" || exit /b 1
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0make-package.ps1" || exit /b 1
