@echo off
chcp 936 >nul
cd /d %~dp0
echo 正在编译 UsbGuard.next.exe ...
"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /platform:anycpu /optimize+ /langversion:5 /utf8output /warn:4 /out:"..\UsbGuard.next.exe" /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Management.dll /r:System.Web.Extensions.dll *.cs
if errorlevel 1 (
  echo.
  echo [X] 编译失败，请看上面的报错。
  pause
  exit /b 1
)
echo [OK] 已生成 程序文件\UsbGuard.next.exe