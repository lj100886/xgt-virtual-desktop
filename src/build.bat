@echo off
set ROOT=%~dp0..
set CSC=C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe
echo [1/4] XiaoGongZuoTai.exe ...
%CSC% /nologo /target:winexe /codepage:65001 /out:"%ROOT%\XiaoGongZuoTai.exe" /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll "%ROOT%\src\Main.cs"
echo [2/4] XgtCapture.exe ...
%CSC% /nologo /target:winexe /codepage:65001 /out:"%ROOT%\XgtCapture.exe" /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll "%ROOT%\src\XgtCapture.cs"
echo [3/4] XgtHelper.exe ...
%CSC% /nologo /target:winexe /codepage:65001 /out:"%ROOT%\XgtHelper.exe" /r:System.dll "%ROOT%\src\XgtHelper.cs"
echo [4/4] XgtAgent.exe (in-session input agent) -> C:\Users\Public\Xgt\
if not exist "C:\Users\Public\Xgt" mkdir "C:\Users\Public\Xgt"
tasklist /fi "imagename eq XgtAgent.exe" 2>nul | findstr /i /c:"XgtAgent.exe" >nul
if %errorlevel%==0 goto AGENTBUSY
%CSC% /nologo /target:winexe /codepage:65001 /out:"C:\Users\Public\Xgt\XgtAgent.exe" /r:System.dll /r:System.Drawing.dll "%ROOT%\src\XgtAgent.cs"
goto DONE
:AGENTBUSY
echo   [SKIP] XgtAgent.exe is currently running inside the virtual desktop (file locked).
echo          Log off that session first, or copy the exe manually.
:DONE
echo All built.

