' USB Guard status-notify hidden launcher; Chinese filename built without encoding-dependent literals.
Option Explicit
Dim fso, sh, dir, ps, exe, psExe, cmd
Set fso = CreateObject("Scripting.FileSystemObject")
Set sh = CreateObject("WScript.Shell")
dir = fso.GetParentFolderName(WScript.ScriptFullName)
ps = dir & "\" & ChrW(29366) & ChrW(24577) & ChrW(25552) & ChrW(31034) & ".ps1"
If Not fso.FileExists(ps) Then WScript.Quit 1
exe = sh.ExpandEnvironmentStrings("%WINDIR%") & "\System32\conhost.exe"
psExe = sh.ExpandEnvironmentStrings("%WINDIR%") & "\System32\WindowsPowerShell\v1.0\powershell.exe"
cmd = """" & exe & """ --headless """ & psExe & """ -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File """ & ps & """"
sh.Run cmd, 0, False
WScript.Quit 0