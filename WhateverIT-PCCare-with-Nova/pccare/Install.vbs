' =====================================================================
'  Whatever IT PC Care - double-click this to install.
'  It opens a graphical setup window and does everything behind it.
'  No command/cmd window is ever shown.
' =====================================================================
Option Explicit
Dim sh, fso, scriptDir, cmd
Set sh  = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")
scriptDir = fso.GetParentFolderName(WScript.ScriptFullName)
cmd = "powershell -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -STA -File """ & scriptDir & "\installer-gui.ps1"""
' The second argument 0 = run completely hidden (no console flash).
sh.Run cmd, 0, False
