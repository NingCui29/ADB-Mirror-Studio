using System.Text.RegularExpressions;

namespace AdbMirrorStudio.Application.Adb;

public readonly partial record struct UiTestBounds(int Left, int Top, int Right, int Bottom)
{
    public int Height => Bottom - Top;
    public long Area => (long)(Right - Left) * Height;
    public int CenterX => Left + (Right - Left) / 2;
    public int CenterY => Top + Height / 2;

    [GeneratedRegex(@"\A\[([0-9]+),([0-9]+)\]\[([0-9]+),([0-9]+)\]\z", RegexOptions.CultureInvariant)]
    private static partial Regex NodeBoundsPattern();

    public static UiTestBounds? Parse(string value)
    {
        var match = NodeBoundsPattern().Match(value);
        if (!match.Success
            || !int.TryParse(match.Groups[1].ValueSpan, out var left)
            || !int.TryParse(match.Groups[2].ValueSpan, out var top)
            || !int.TryParse(match.Groups[3].ValueSpan, out var right)
            || !int.TryParse(match.Groups[4].ValueSpan, out var bottom)
            || left >= right || top >= bottom) return null;
        return new(left, top, right, bottom);
    }
}
