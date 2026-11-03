namespace AdbMirrorStudio.Application.Adb;

public static class DeviceShellCommand
{
    public static void Validate(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            throw new ArgumentException("设备命令不能为空。", nameof(command));
        }
        if (command.Length > 4096)
        {
            throw new ArgumentException("设备命令不能超过 4096 个字符。", nameof(command));
        }
        if (command.Any(character => char.IsControl(character) && character != '\t'))
        {
            throw new ArgumentException("设备命令不能包含换行或其他控制字符。", nameof(command));
        }

        var executable = LeadingExecutable(command.TrimStart());
        var fileName = executable[(executable.LastIndexOfAny(['/', '\\']) + 1)..];
        if (fileName.Equals("adb", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("adb.exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "这里已是 Android 设备 Shell，请直接输入设备命令，无需 adb 或 adb shell 前缀；目标由当前所选设备固定。查找包名示例：pm list packages com.tp。",
                nameof(command));
        }
    }

    private static string LeadingExecutable(string command)
    {
        if (command[0] is '\'' or '"')
        {
            var endQuote = command.IndexOf(command[0], 1);
            return endQuote > 0 && (endQuote == command.Length - 1 || IsTokenEnd(command[endQuote + 1]))
                ? command[1..endQuote]
                : string.Empty;
        }

        var end = 0;
        while (end < command.Length && !IsTokenEnd(command[end])) end++;
        return command[..end];
    }

    private static bool IsTokenEnd(char character) =>
        char.IsWhiteSpace(character) || character is ';' or '|' or '&' or '<' or '>' or '(' or ')';
}
