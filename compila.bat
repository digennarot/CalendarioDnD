@echo off
rem Compila Calendario.cs in Calendario.exe usando il compilatore C# incluso in Windows (.NET Framework 4)
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
"%CSC%" /nologo /target:winexe /out:Calendario.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Security.dll "%~dp0Calendario.cs"
if errorlevel 1 (echo Compilazione fallita. & pause) else (echo Fatto: Calendario.exe)
