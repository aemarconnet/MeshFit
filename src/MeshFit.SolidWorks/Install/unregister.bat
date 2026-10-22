@echo off
rem Removes the MeshFit add-in registration. Run from an elevated (administrator) prompt.
set REGASM=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\RegAsm.exe
"%REGASM%" /unregister "%~dp0MeshFit.SolidWorks.dll"
