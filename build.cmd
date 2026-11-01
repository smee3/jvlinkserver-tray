@echo off
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set REFS=/r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll
set OPTS=/nologo /optimize+ /debug- %REFS%
cd /d "%~dp0"
if not exist bin mkdir bin

if exist src\App\Program.cs (
  rem The EXE icon is drawn by the same code as the tray icon, so no binary icon lives in the repository.
  "%CSC%" %OPTS% /target:exe /out:bin\MakeIcon.exe tools\MakeIcon.cs src\App\Icons.cs src\Core\*.cs
  if errorlevel 1 exit /b 1
  bin\MakeIcon.exe bin\JVLinkServerTray.ico
  if errorlevel 1 exit /b 1
  "%CSC%" %OPTS% /target:winexe /win32icon:bin\JVLinkServerTray.ico /out:bin\JVLinkServerTray.exe /recurse:src\*.cs
  if errorlevel 1 exit /b 1
)

set TESTSRC=src\Core\*.cs src\App\Icons.cs tests\*.cs
if exist src\Os set TESTSRC=%TESTSRC% src\Os\*.cs
"%CSC%" %OPTS% /target:exe /out:bin\JVLinkServerTray.Tests.exe %TESTSRC%
if errorlevel 1 exit /b 1

if exist tests\Fake\FakeJVLinkServer.cs (
  "%CSC%" %OPTS% /target:exe /out:bin\FakeJVLinkServer.exe tests\Fake\FakeJVLinkServer.cs
  if errorlevel 1 exit /b 1
)
exit /b 0
