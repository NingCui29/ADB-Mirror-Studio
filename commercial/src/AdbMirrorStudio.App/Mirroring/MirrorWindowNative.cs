using System.Runtime.InteropServices;

namespace AdbMirrorStudio.App.Mirroring;

internal static class MirrorWindowNative
{
    private const int WindowStyle = -16;
    private const int ExtendedStyle = -20;
    private const int OwnerIndex = -8;
    private const int CaptionStyle = 0x00C00000;
    private static readonly SubclassProcedure ToolbarProcedure = HandleToolbarMessage;

    public static bool BelongsToProcess(nint window, int? processId) =>
        window != 0 && processId is { } pid && GetWindowThreadProcessId(window, out var actual) != 0 && actual == (uint)pid;

    public static bool IsAttached(nint window, nint owner) => GetWindow(window, 4 /* GW_OWNER */) == owner;

    public static bool Attach(nint window, nint owner)
    {
        const int noActivate = 0x08000000;
        const int toolWindow = 0x00000080;
        const int appWindow = 0x00040000;
        var style = GetWindowLongPtr(window, ExtendedStyle).ToInt64();
        SetWindowLongPtr(window, ExtendedStyle, (nint)((style | noActivate | toolWindow) & ~appWindow));
        SetWindowLongPtr(window, OwnerIndex, owner);

        // The presenter reinstates dialog-frame styles when the WinUI window changes.
        if (SetWindowSubclass(window, ToolbarProcedure, 1, 0))
        {
            const uint frameChanged = 0x0020, noMove = 0x0002, noSize = 0x0001, noZOrder = 0x0004, keepInactive = 0x0010;
            SetWindowPos(window, 0, 0, 0, 0, 0, frameChanged | noMove | noSize | noZOrder | keepInactive);
        }

        // Keep the popup flush with the title bar, including on Windows 11.
        const uint cornerPreference = 33;
        const uint doNotRound = 1;
        const uint borderColor = 34;
        const uint noBorder = 0xFFFFFFFE;
        DwmSetWindowAttribute(window, cornerPreference, doNotRound, sizeof(uint));
        DwmSetWindowAttribute(window, borderColor, noBorder, sizeof(uint));
        return IsAttached(window, owner);
    }

    private static nint HandleToolbarMessage(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data)
    {
        const uint nonClientCalcSize = 0x0083, nonClientPaint = 0x0085, nonClientDestroy = 0x0082;
        if (message is nonClientCalcSize or nonClientPaint) return 0;
        if (message == nonClientDestroy) RemoveWindowSubclass(window, ToolbarProcedure, id);
        return DefSubclassProc(window, message, wParam, lParam);
    }

    public static MirrorToolbarPlacement? GetPlacement(nint window)
    {
        if (!IsWindowVisible(window) || IsIconic(window)
            || (GetWindowLongPtr(window, WindowStyle).ToInt64() & CaptionStyle) != CaptionStyle)
            return null;

        var client = new Point();
        if (!ClientToScreen(window, ref client)) return null;
        if (DwmGetWindowAttribute(window, 9 /* DWMWA_EXTENDED_FRAME_BOUNDS */, out var frame, Marshal.SizeOf<Rect>()) != 0
            && !GetWindowRect(window, out frame)) return null;

        var dpi = GetDpiForWindow(window);
        // SDL may retain the normal style while switching to fullscreen. The client origin then meets the frame top.
        return MirrorToolbarPlacement.Fit(frame.Left, frame.Top, frame.Right - frame.Left, client.Y - frame.Top, dpi);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left; public int Top; public int Right; public int Bottom; }

    private delegate nint SubclassProcedure(nint window, uint message, nuint wParam, nint lParam, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint window, SubclassProcedure procedure, nuint id, nuint data);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint window, SubclassProcedure procedure, nuint id);

    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint window, uint message, nuint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, uint relation);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern nint GetWindowLongPtr(nint window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern nint SetWindowLongPtr(nint window, int index, nint value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClientToScreen(nint window, ref Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint window);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint window, uint attribute, out Rect value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, uint attribute, in uint value, int size);
}
