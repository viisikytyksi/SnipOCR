@echo off
REM ============================================================
REM  SnipOCR portable build - no internet, no admin, no VS.
REM  Uses the C# compiler and Windows SDK metadata already on Windows.
REM
REM  Layout expected next to this script:
REM    lib\      onnxruntime.dll, Microsoft.ML.OnnxRuntime.dll, System.Memory etc.
REM    models\   PP-OCRv6_det_small.onnx / PP-OCRv6_rec_small.onnx
REM  Output:
REM    build\  runnable folder (exe + DLLs + rapidocr\)
REM    dist\   same runtime pieces, kept ready to copy to a USB stick
REM ============================================================
setlocal

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set LIBS=%~dp0lib
set MODELS=%~dp0models

REM --- locate Windows.winmd (adjust SDK version folder if needed) ---
set WINMD=
for /d %%D in ("%ProgramFiles(x86)%\Windows Kits\10\UnionMetadata\*") do (
    if exist "%%D\Windows.winmd" set WINMD=%%D\Windows.winmd
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

REM --- no SDK installed? fall back to the winmd copies in tools\sdk ---
if "%WINMD%"=="" if exist "%~dp0tools\sdk\Windows.winmd" (
    set WINMD=%~dp0tools\sdk\Windows.winmd
    set CONTRACT=%~dp0tools\sdk\Windows.Foundation.UniversalApiContract.winmd
    set FOUNDATION=%~dp0tools\sdk\Windows.Foundation.FoundationContract.winmd
)
if "%WINMD%"=="" (
    echo [ERROR] Windows.winmd not found: neither the Windows 10 SDK nor tools\sdk is available.
    echo         Install the Windows 10 SDK, or run tools\fetch_deps.ps1 on a networked PC.
    exit /b 1
)

if not exist "%LIBS%\Microsoft.ML.OnnxRuntime.dll" (
    echo [ERROR] %LIBS%\Microsoft.ML.OnnxRuntime.dll not found.
    echo         Run tools\fetch_deps.ps1 once on a networked PC to populate lib\.
    exit /b 1
)
if not exist "%MODELS%\PP-OCRv6_rec_small.onnx" (
    echo [ERROR] %MODELS%\PP-OCRv6_rec_small.onnx not found.
    exit /b 1
)

if not exist "%~dp0build" mkdir "%~dp0build"

REM app.ico is used both as the exe icon and as an embedded resource (tray/windows).
REM Regenerate it with:  powershell -ExecutionPolicy Bypass -File src\make_icon.ps1

"%CSC%" -nologo -target:winexe -platform:x64 -optimize+ ^
  -out:"%~dp0build\SnipOCR.exe" ^
  -win32icon:"%~dp0src\app.ico" ^
  -resource:"%~dp0src\app.ico",SnipOCR.app.ico ^
  -reference:"%FW%\System.dll" ^
  -reference:"%FW%\System.Core.dll" ^
  -reference:"%FW%\System.Drawing.dll" ^
  -reference:"%FW%\System.Windows.Forms.dll" ^
  -reference:"%FW%\System.Runtime.dll" ^
  -reference:"%FW%\System.Runtime.WindowsRuntime.dll" ^
  -reference:"%FW%\netstandard.dll" ^
  -reference:"%LIBS%\Microsoft.ML.OnnxRuntime.dll" ^
  -reference:"%LIBS%\System.Memory.dll" ^
  -reference:"%WINMD%" ^
  -reference:"%CONTRACT%" ^
  -reference:"%FOUNDATION%" ^
  "%~dp0src\AssemblyInfo.cs" "%~dp0src\SnipOCR.cs" "%~dp0src\RapidOcr.cs"

if errorlevel 1 ( echo [BUILD FAILED] & exit /b 1 )

REM --- runtime pieces next to the exe ---
copy /y "%LIBS%\*.dll" "%~dp0build\" >nul
if not exist "%~dp0build\rapidocr" mkdir "%~dp0build\rapidocr"
copy /y "%MODELS%\*.onnx" "%~dp0build\rapidocr\" >nul

REM --- keep the distribution folder runnable too ---
if not exist "%~dp0dist\rapidocr" mkdir "%~dp0dist\rapidocr"
copy /y "%~dp0build\SnipOCR.exe" "%~dp0dist\" >nul
copy /y "%LIBS%\*.dll" "%~dp0dist\" >nul
copy /y "%MODELS%\*.onnx" "%~dp0dist\rapidocr\" >nul

echo [OK] Built build\SnipOCR.exe (RapidOCR, Windows.Media.Ocr fallback)
endlocal
