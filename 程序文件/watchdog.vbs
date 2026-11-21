' USB Guard v2.1.0 watchdog: protected stop flag + exact executable path.
Option Explicit
Dim fso, sh, dir, exe, flag, running
Set fso = CreateObject("Scripting.FileSystemObject")
Set sh = CreateObject("WScript.Shell")
dir = fso.GetParentFolderName(WScript.ScriptFullName)
exe = dir & "\UsbGuard.exe"
flag = dir & "\usb-guard-disabled.flag"
If fso.FileExists(flag) Then WScript.Quit 0
If Not fso.FileExists(exe) Then WScript.Quit 1

Function IsCurrentGuardRunning()
  Dim wmi, items, item, found
  found = False
  On Error Resume Next
  Set wmi = GetObject("winmgmts:\\.\root\cimv2")
  Set items = wmi.ExecQuery("SELECT ProcessId, ExecutablePath FROM Win32_Process WHERE Name='UsbGuard.exe'")
  For Each item In items
    If Not IsNull(item.ExecutablePath) Then
      If StrComp(CStr(item.ExecutablePath), exe, vbTextCompare) = 0 Then found = True
    End If
  Next
  If Err.Number <> 0 Then found = False
  Err.Clear
  On Error GoTo 0
  IsCurrentGuardRunning = found
End Function

running = IsCurrentGuardRunning()
If Not running Then
  sh.Run """" & exe & """", 0, False
  On Error Resume Next
  sh.LogEvent 4, "UsbGuard: watchdog requested current-path guard restart"
  WScript.Sleep 4000
  If Not IsCurrentGuardRunning() Then sh.LogEvent 1, "UsbGuard: current-path guard not running after restart; check Application\UsbGuard"
  On Error GoTo 0
End If
WScript.Quit 0