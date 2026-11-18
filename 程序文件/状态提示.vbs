' USB Guard status-notify hidden launcher (ASCII-safe)
' Runs the status script inside a headless conhost (bypasses Windows Terminal).
' v2-rev5: script body is pure ASCII; the Chinese script name is built with ChrW
'          so it stays correct on non-Chinese Windows code pages.
Option Explicit
Dim fso, sh, dir, psName, ps, exe, psExe, cmd
Set fso = CreateObject("Scripting.FileSystemObject")
Set sh  = CreateObject("WScript.Shell")

dir = fso.GetParentFolderName(WScript.ScriptFullName)
' U+72B6 U+6001 U+63D0 U+793A = the 4 Chinese chars of the status script name
psName = ChrW(&H72B6) & ChrW(&H6001) & ChrW(&H63D0) & ChrW(&H793A) & ".ps1"
ps = dir & "\" & psName
If Not fso.FileExists(ps) Then WScript.Quit

exe = sh.ExpandEnvironmentStrings("%WINDIR%") & "\System32\conhost.exe"
psExe = sh.ExpandEnvironmentStrings("%WINDIR%") & "\System32\WindowsPowerShell\v1.0\powershell.exe"
cmd = """" & exe & """ --headless """ & psExe & """ -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File """ & ps & """"
sh.Run cmd, 0, False
