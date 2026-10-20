@echo off
rem Build with the .NET Framework compiler that ships with Windows (nothing to install)
cd /d "%~dp0"
set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
set WM=%WINDIR%\System32\WinMetadata
"%FW%\csc.exe" /nologo /target:winexe /optimize /codepage:65001 /out:DragConvert.exe /win32icon:src\app.ico ^
  /lib:"%FW%\WPF" /r:PresentationCore.dll /r:WindowsBase.dll /r:System.Xaml.dll /r:UIAutomationClient.dll /r:UIAutomationTypes.dll ^
  /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:System.Core.dll /r:System.Drawing.dll ^
  /r:"%FW%\System.Runtime.dll" /r:"%FW%\System.Threading.Tasks.dll" /r:"%FW%\System.Runtime.WindowsRuntime.dll" ^
  /r:"%WM%\Windows.Foundation.winmd" /r:"%WM%\Windows.Storage.winmd" /r:"%WM%\Windows.Data.winmd" ^
  src\*.cs
