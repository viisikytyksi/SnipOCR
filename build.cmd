@echo off
REM ============================================================
REM  SnipOCR portable build - no internet, no admin, no VS.
REM  Uses the C# compiler and Windows SDK metadata already on Windows.
REM ============================================================
setlocal

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319

REM --- locate Windows.winmd (adjust SDK version folder if needed) ---
set WINMD=
for /d %%D in ("%ProgramFiles(x86)%\Windows Kits\10\UnionMetadata\*") do (
    if exist "%%D\Windows.winmd" set WINMD=%%D\Windows.winmd
)
if "%WINMD%"=="" (
    echo [ERROR] Windows.winmd not found under Windows Kits\10\UnionMetadata.
    echo         Install the Windows 10 SDK, or point WINMD to a Windows.winmd.
    exit /b 1
)

set CONTRACT=
for /d %%V in ("%ProgramFiles(x86)%\Windows Kits\10\References\*") do (
  for /d %%C in ("%%V\Windows.Foundation.UniversalApiContract\*") do (
    if exist "%%C\Windows.Foundation.UniversalApiContract.winmd" set CONTRACT=%%C\Windows.Foundation.UniversalApiContract.winmd
  )
)
set FOUNDATION=
for /d %%V in ("%ProgramFiles(x86)%\Windows Kits\10\References\*") do (
  for /d %%C in ("%%V\Windows.Foundation.FoundationContract\*") do (
    if exist "%%C\Windows.Foundation.FoundationContract.winmd" set FOUNDATION=%%C\Windows.Foundation.FoundationContract.winmd
  )
)

if not exist "%~dp0build" mkdir "%~dp0build"

REM app.ico is used both as the exe icon and as an embedded resource (tray/windows).
REM Regenerate it with:  powershell -ExecutionPolicy Bypass -File src\make_icon.ps1

"%CSC%" -nologo -target:winexe -platform:anycpu -optimize+ ^
  -out:"%~dp0build\SnipOCR.exe" ^
  -win32icon:"%~dp0src\app.ico" ^
  -resource:"%~dp0src\app.ico",SnipOCR.app.ico ^
  -reference:"%FW%\System.dll" ^
  -reference:"%FW%\System.Core.dll" ^
  -reference:"%FW%\System.Drawing.dll" ^
  -reference:"%FW%\System.Windows.Forms.dll" ^
  -reference:"%FW%\System.Runtime.dll" ^
  -reference:"%FW%\System.Runtime.WindowsRuntime.dll" ^
  -reference:"%WINMD%" ^
  -reference:"%CONTRACT%" ^
  -reference:"%FOUNDATION%" ^
  "%~dp0src\SnipOCR.cs"

if errorlevel 1 ( echo [BUILD FAILED] & exit /b 1 )
echo [OK] Built build\SnipOCR.exe
endlocal
