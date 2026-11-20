@echo off
rem Starts the EdiaHelper installer with administrator rights (see Elevate.ps1).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Elevate.ps1" Install.ps1
