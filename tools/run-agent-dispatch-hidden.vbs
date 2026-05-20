Option Explicit

Dim shell, fso, scriptDir, repoRoot, nodePath, dispatchScript, arguments, index, command

Set shell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")

scriptDir = fso.GetParentFolderName(WScript.ScriptFullName)
repoRoot = fso.GetParentFolderName(scriptDir)
nodePath = shell.ExpandEnvironmentStrings("%ProgramFiles%") & "\nodejs\node.exe"
dispatchScript = repoRoot & "\tools\dispatch-agent-requests.mjs"

If Not fso.FileExists(nodePath) Then
  nodePath = "node.exe"
End If

arguments = ""
For index = 0 To WScript.Arguments.Count - 1
  arguments = arguments & " " & Quote(WScript.Arguments(index))
Next

command = Quote(nodePath) & " " & Quote(dispatchScript) & arguments
shell.CurrentDirectory = repoRoot
WScript.Quit shell.Run(command, 0, True)

Function Quote(value)
  Quote = """" & Replace(value, """", "\""") & """"
End Function
