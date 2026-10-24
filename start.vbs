Option Explicit
Dim shell, files, root, executable
Set shell = CreateObject("WScript.Shell")
Set files = CreateObject("Scripting.FileSystemObject")
root = files.GetParentFolderName(WScript.ScriptFullName)
executable = files.BuildPath(root, "dist\MBrowser.exe")
If Not files.FileExists(executable) Then
    executable = files.BuildPath(root, "bin\Release\net10.0-windows\MBrowser.exe")
End If
If files.FileExists(executable) Then
    shell.CurrentDirectory = root
    shell.Run Chr(34) & executable & Chr(34), 1, False
Else
    MsgBox "Run build.ps1 first, then open start.vbs or dist\MBrowser.exe.", 48, "MBrowser"
End If
