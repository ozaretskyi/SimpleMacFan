@echo off
rem Builds SimpleMacFan.exe with the C# compiler that ships with Windows (.NET Framework 4).
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /optimize /codepage:65001 /target:winexe ^
  /win32manifest:app.manifest /out:SimpleMacFan.exe ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll SimpleMacFan.cs
