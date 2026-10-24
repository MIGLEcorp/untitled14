Option Explicit
Dim shell, files, root, executable
Set shell = CreateObject("WScript.Shell")
Set files = CreateObject("Scripting.FileSystemObject")
root = files.GetParentFolderName(WScript.ScriptFullName)
executable = files.BuildPath(root, "dist\Feather.exe")
If Not files.FileExists(executable) Then
    executable = files.BuildPath(root, "bin\Release\net10.0-windows\Feather.exe")
End If
If files.FileExists(executable) Then
    shell.CurrentDirectory = root
    shell.Run Chr(34) & executable & Chr(34), 1, False
Else
    MsgBox "Run build.ps1 first, then open start.vbs or dist\Feather.exe.", 48, "Feather"
End If
