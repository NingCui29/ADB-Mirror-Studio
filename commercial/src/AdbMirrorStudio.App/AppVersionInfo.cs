using System.Reflection;

namespace AdbMirrorStudio.App;

internal static class AppVersionInfo
{
    public static string ProductVersion { get; } =
        $"V{typeof(AppVersionInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"}";

    public static string DisplayVersion { get; } = typeof(AppVersionInfo).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? ProductVersion;
}
