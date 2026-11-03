using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace AdbMirrorStudio.App;

public sealed partial class MainWindow : Window
{
    private bool _isClosing;
    public MainWindow(AppServices services)
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Title = "ADB Mirror Studio";
        VersionText.Text = AppVersionInfo.DisplayVersion;
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        if (File.Exists(iconPath)) AppWindow.SetIcon(iconPath);
        UpdateTitleBarColors();
        SizeAndCenterWindow();
        RootFrame.Navigate(typeof(MainPage), services);
    }

    internal void ApplyTheme(string theme)
    {
        if (_isClosing) return;
        RootLayout.RequestedTheme = theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        UpdateTitleBarColors();
    }

    internal void PrepareForShutdown()
    {
        _isClosing = true;
        (RootFrame.Content as MainPage)?.Shutdown();
    }

    private void RootLayout_ActualThemeChanged(FrameworkElement sender, object args) => UpdateTitleBarColors();

    private void UpdateTitleBarColors()
    {
        if (_isClosing) return;
        var isDark = RootLayout.ActualTheme == ElementTheme.Dark;
        var foreground = isDark ? Colors.White : Colors.Black;
        var inactive = isDark
            ? ColorHelper.FromArgb(150, 255, 255, 255)
            : ColorHelper.FromArgb(150, 0, 0, 0);
        var hover = isDark
            ? ColorHelper.FromArgb(28, 255, 255, 255)
            : ColorHelper.FromArgb(18, 0, 0, 0);
        var pressed = isDark
            ? ColorHelper.FromArgb(45, 255, 255, 255)
            : ColorHelper.FromArgb(32, 0, 0, 0);

        AppWindow.TitleBar.ButtonForegroundColor = foreground;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = inactive;
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonHoverBackgroundColor = hover;
        AppWindow.TitleBar.ButtonHoverForegroundColor = foreground;
        AppWindow.TitleBar.ButtonPressedBackgroundColor = pressed;
        AppWindow.TitleBar.ButtonPressedForegroundColor = foreground;
    }

    private void SizeAndCenterWindow()
    {
        const double desiredWidthDip = 1180;
        const double desiredHeightDip = 780;

        var windowHandle = WindowNative.GetWindowHandle(this);
        var scale = Math.Max(1.0, NativeMethods.GetDpiForWindow(windowHandle) / 96.0);
        var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var workArea = displayArea.WorkArea;

        var desiredWidth = (int)Math.Round(desiredWidthDip * scale);
        var desiredHeight = (int)Math.Round(desiredHeightDip * scale);
        var width = Math.Min(desiredWidth, (int)Math.Round(workArea.Width * 0.92));
        var height = Math.Min(desiredHeight, (int)Math.Round(workArea.Height * 0.88));
        var x = workArea.X + Math.Max(0, (workArea.Width - width) / 2);
        var y = workArea.Y + Math.Max(0, (workArea.Height - height) / 2);

        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern uint GetDpiForWindow(nint windowHandle);
    }
}
