@echo off
rem Registers the MeshFit add-in with SOLIDWORKS. Run from an elevated (administrator) prompt.
setlocal
set REGASM=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe
if not exist "%REGASM%" (
  echo 64-bit RegAsm not found: %REGASM%
  exit /b 1
)
rem Files downloaded from the internet are blocked by Windows; unblock them first.
powershell -NoProfile -Command "Get-ChildItem -Path '%~dp0' -Recurse | Unblock-File" >nul 2>&1
"%REGASM%" /codebase "%~dp0MeshFit.SolidWorks.dll"
if errorlevel 1 (
  echo Registration failed. Make sure this prompt runs as administrator.
  exit /b 1
)
echo.
echo MeshFit registered. Start SOLIDWORKS and enable it under Tools ^> Add-Ins if it is not already active.
