@echo off
rem Builds EdiaHelper with the C# compiler that comes with Windows (.NET Framework 4.x).
rem Output: build\EdiaHelper.exe and build\EdiaHelper.ico
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
cd /d "%~dp0"
if not exist build mkdir build

"%CSC%" /nologo /target:exe /out:build\MakeIcon.exe /r:System.Drawing.dll src\MakeIcon.cs src\IconArt.cs || goto :error
build\MakeIcon.exe build\EdiaHelper.ico || goto :error
del build\MakeIcon.exe

"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /win32icon:build\EdiaHelper.ico /out:build\EdiaHelper.exe ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  src\EdiaHelper.cs src\Relay.cs src\Check.cs src\IconArt.cs src\NcsBackup.cs src\LastSeen.cs || goto :error

echo Build OK: build\EdiaHelper.exe
exit /b 0

:error
echo Build FAILED
exit /b 1
