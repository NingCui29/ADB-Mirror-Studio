// Execute the production ViewModel in headless tests. These small stand-ins only
// supply presentation types; they do not simulate WinUI rendering or layout.
namespace Microsoft.UI.Xaml.Controls
{
    public enum InfoBarSeverity { Informational, Success, Warning, Error }
}

namespace Microsoft.UI
{
    public readonly record struct TestColor(byte A, byte R, byte G, byte B);
    public static class ColorHelper
    {
        public static TestColor FromArgb(byte a, byte r, byte g, byte b) => new(a, r, g, b);
    }
}

namespace Microsoft.UI.Xaml.Media
{
    public class Brush;
    public sealed class SolidColorBrush(Microsoft.UI.TestColor color) : Brush
    {
        public Microsoft.UI.TestColor Color { get; } = color;
    }
}

namespace AdbMirrorStudio.App
{
    public sealed record AppServices(
        Application.Adb.IAdbService Adb,
        Application.Mirroring.IMirrorSessionManager MirrorSessions,
        Application.Settings.IAppSettingsStore Settings,
        Application.Diagnostics.IDiagnosticsService Diagnostics,
        Application.Updates.IUpdateService Updates);

    internal static class AppVersionInfo
    {
        public const string ProductVersion = "V1.0.0";
    }
}
