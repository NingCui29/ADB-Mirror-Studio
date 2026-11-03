namespace AdbMirrorStudio.App;

internal static class AppVersionInfo
{
    public static string ProductVersion { get; } =
        $"V{typeof(AppVersionInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"}";

    public static string ReleaseDate { get; } = typeof(AppVersionInfo).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false)
        .Cast<System.Reflection.AssemblyMetadataAttribute>()
        .FirstOrDefault(attribute => attribute.Key == "ReleaseDate")?.Value ?? string.Empty;

    public static string DisplayVersion => string.IsNullOrEmpty(ReleaseDate)
        ? ProductVersion : $"{ProductVersion}-{ReleaseDate}";
}
