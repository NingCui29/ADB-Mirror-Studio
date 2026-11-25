namespace AdbMirrorStudio.App.Mirroring;

internal readonly record struct MirrorToolbarPlacement(int X, int Y, int Width, int Height, bool Compact)
{
    public static MirrorToolbarPlacement? Fit(int left, int top, int width, int captionHeight, uint dpi)
    {
        int Scale(int dip) => (int)Math.Round(dip * dpi / 96.0);

        // Leave the device title and the native caption buttons unobstructed.
        var leftInset = Math.Clamp(width / 3, Scale(84), Scale(204));
        var rightInset = Scale(152);
        var compactWidth = Scale(34);
        if (dpi == 0 || captionHeight < Scale(23) || width < Scale(72) + rightInset + compactWidth) return null;

        var compact = width - leftInset - rightInset < Scale(224);
        var toolbarWidth = compact ? compactWidth : Scale(224);
        leftInset = Math.Min(leftInset, width - rightInset - toolbarWidth);
        var x = Math.Clamp((width - toolbarWidth) / 2, leftInset, width - rightInset - toolbarWidth);
        return new MirrorToolbarPlacement(left + x, top, toolbarWidth, captionHeight, compact);
    }
}
