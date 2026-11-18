' USB Guard watchdog - hardened
' This file lives in a protected folder. Standard users cannot edit or move it.
Option Explicit
Dim fso, sh, dir, exe, wmi, col, p, running
Set fso = CreateObject("Scripting.FileSystemObject")
Set sh  = CreateObject("WScript.Shell")

dir = fso.GetParentFolderName(WScript.ScriptFullName)
exe = dir & "\UsbGuard.exe"
If Not fso.FileExists(exe) Then WScript.Quit

' No stop flag check: a writable marker can no longer disable the watchdog forever.
running = False
On Error Resume Next
Set wmi = GetObject("winmgmts:\\.\root\cimv2")
Set col = wmi.ExecQuery("SELECT ProcessId FROM Win32_Process WHERE Name='UsbGuard.exe'")
For Each p In col
  running = True
Next
If Err.Number <> 0 Then running = False
Err.Clear
On Error GoTo 0

If Not running Then
  sh.Run """" & exe & """", 1, False
  On Error Resume Next
  sh.LogEvent 4, "UsbGuard: watchdog restarted guard"
  ' 等 4 秒再确认一次，若仍没起来就记一条明确告警，便于定位
  Set wmi = GetObject("winmgmts:\\.\root\cimv2")
  Set col = wmi.ExecQuery("SELECT ProcessId FROM Win32_Process WHERE Name='UsbGuard.exe'")
  Dim up : up = False
  For Each p In col
    up = True
  Next
  If Not up Then
    sh.LogEvent 1, "UsbGuard: guard still not running 4s after restart (possible crash or blocked; check Event Log Application\UsbGuard)"
  End If
  On Error GoTo 0
End If
