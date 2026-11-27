namespace AdbMirrorStudio.Application.Adb;

public sealed record UiTestNode(
    string Text,
    string ResourceId,
    string Description,
    string ClassName,
    string Bounds,
    bool Enabled,
    bool Clickable,
    bool Scrollable)
{
    public int Depth { get; init; }
}
