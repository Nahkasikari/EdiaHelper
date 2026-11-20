@echo off
rem Starts the EdiaHelper uninstaller with administrator rights (see Elevate.ps1).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Elevate.ps1" Uninstall.ps1
