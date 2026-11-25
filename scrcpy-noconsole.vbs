' Launch scrcpy directly. cmd.exe is not involved, so shell metacharacters in
' arguments cannot be interpreted as additional commands.
Dim strCommand, Arg
strCommand = "scrcpy.exe"
For Each Arg In WScript.Arguments
    strCommand = strCommand & " " & Chr(34) & Replace(Arg, Chr(34), Chr(34) & Chr(34)) & Chr(34)
Next
CreateObject("Wscript.Shell").Run strCommand, 0, False
