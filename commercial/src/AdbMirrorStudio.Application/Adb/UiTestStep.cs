namespace AdbMirrorStudio.Application.Adb;

public enum UiTestAction
{
    Launch, Tap, AssertVisible, InputText, Back, SwipeUp, SwipeDown, Screenshot, Logcat
}

public sealed record UiTestStep(UiTestAction Action, string Value = "", bool ScrollIfMissing = false)
{
    public static UiTestStep CreateDraft(UiTestAction action, string value, string? pickedSelector, bool scrollIfMissing)
    {
        var usesSelector = action is UiTestAction.Tap or UiTestAction.AssertVisible;
        var stepValue = action switch
        {
            UiTestAction.InputText => value,
            UiTestAction.Launch => value.Trim(),
            _ when usesSelector => value == pickedSelector ? value : value.Trim(),
            _ => string.Empty
        };
        return new UiTestStep(action, stepValue, usesSelector && scrollIfMissing);
    }

    public string? ValueError => Action switch
    {
        UiTestAction.InputText when string.IsNullOrEmpty(Value) => "请输入要发送的文本。",
        UiTestAction.InputText when Value.Length > 1024 => "输入文本最多支持 1024 个字符。",
        UiTestAction.InputText when Value.Any(c => c is < ' ' or > '~') => "仅支持可打印的 ASCII 字符。",
        UiTestAction.InputText when Value.Contains("%s", StringComparison.Ordinal) => "%s 是 Android 输入命令的保留序列。",
        UiTestAction.Tap or UiTestAction.AssertVisible when string.IsNullOrWhiteSpace(Value) => "请输入目标控件。",
        UiTestAction.Launch when string.IsNullOrWhiteSpace(Value) => "请输入应用包名。",
        _ => null
    };

    public string ActionLabel => LabelFor(Action);

    public static string LabelFor(UiTestAction action) => action switch
    {
        UiTestAction.Launch => "启动应用",
        UiTestAction.Tap => "点击控件",
        UiTestAction.AssertVisible => "断言控件可见",
        UiTestAction.InputText => "输入文本",
        UiTestAction.Back => "返回",
        UiTestAction.SwipeUp => "上滑",
        UiTestAction.SwipeDown => "下滑",
        UiTestAction.Screenshot => "截图",
        UiTestAction.Logcat => "保存 Logcat",
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };
}
