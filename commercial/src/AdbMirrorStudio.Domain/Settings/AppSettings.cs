namespace AdbMirrorStudio.Domain.Settings;

public sealed record AppSettings(
    int SchemaVersion,
    string Theme,
    string Language,
    string LastEndpoint,
    bool AutoRefresh,
    string MirrorProfileId = "balanced",
    bool FirstRunCompleted = false,
    bool AutoReconnect = false,
    bool HasConnectedBefore = false)
{
    public const int CurrentSchemaVersion = 2;

    public string[] RememberedEndpoints { get; init; } = [];
    public bool PerformanceMonitoringEnabled { get; init; }

    public AppSettings UpgradeConnectionHistory()
    {
        var history = ConnectionHistory.Normalize(RememberedEndpoints);
        if (HasConnectedBefore && !string.IsNullOrWhiteSpace(LastEndpoint))
        {
            history = ConnectionHistory.Add(history, LastEndpoint);
        }

        return this with
        {
            SchemaVersion = CurrentSchemaVersion,
            LastEndpoint = history.FirstOrDefault() ?? string.Empty,
            AutoReconnect = false,
            HasConnectedBefore = history.Length > 0,
            RememberedEndpoints = history
        };
    }

    public static AppSettings Default { get; } = new(
        CurrentSchemaVersion,
        "System",
        "zh-CN",
        string.Empty,
        true);
}
