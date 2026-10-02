@echo off
rem Builds ClaudeUsageWidget.exe with the C# compiler bundled in Windows and installs it to %LOCALAPPDATA%\ClaudeUsageWidget
setlocal
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set OUT=%LOCALAPPDATA%\ClaudeUsageWidget
if not exist "%OUT%" mkdir "%OUT%"
taskkill /im ClaudeUsageWidget.exe /f >nul 2>&1
"%FW%\csc.exe" /nologo /target:winexe /optimize+ /out:"%OUT%\ClaudeUsageWidget.exe" ^
  /r:"%FW%\WPF\PresentationFramework.dll" /r:"%FW%\WPF\PresentationCore.dll" /r:"%FW%\WPF\WindowsBase.dll" ^
  /r:"%FW%\System.Xaml.dll" /r:"%FW%\System.Web.Extensions.dll" /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  "%~dp0ClaudeUsageWidget.cs" || exit /b 1
copy /y "%~dp0ClaudeUsageWidget.cs" "%OUT%\" >nul
copy /y "%~f0" "%OUT%\" >nul
echo Built "%OUT%\ClaudeUsageWidget.exe"
start "" "%OUT%\ClaudeUsageWidget.exe"
