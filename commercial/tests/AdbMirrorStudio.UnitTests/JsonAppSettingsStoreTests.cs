using AdbMirrorStudio.Domain.Settings;
using AdbMirrorStudio.Infrastructure.Persistence;

namespace AdbMirrorStudio.UnitTests;

public sealed class JsonAppSettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "AdbMirrorStudio.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SaveAndLoad_RoundTripsSettings()
    {
        var store = new JsonAppSettingsStore(Path.Combine(_directory, "settings.json"));
        var expected = AppSettings.Default with
        {
            Theme = "Dark",
            LastEndpoint = "pixel.local:5555",
            HasConnectedBefore = true,
            RememberedEndpoints = ["pixel.local:5555", "192.168.1.8:5555"]
        };

        await store.SaveAsync(expected);
        var actual = await store.LoadAsync();

        Assert.Equal(expected.SchemaVersion, actual.SchemaVersion);
        Assert.Equal(expected.Theme, actual.Theme);
        Assert.Equal(expected.Language, actual.Language);
        Assert.Equal(expected.LastEndpoint, actual.LastEndpoint);
        Assert.Equal(expected.AutoRefresh, actual.AutoRefresh);
        Assert.Equal(expected.MirrorProfileId, actual.MirrorProfileId);
        Assert.Equal(expected.FirstRunCompleted, actual.FirstRunCompleted);
        Assert.Equal(expected.AutoReconnect, actual.AutoReconnect);
        Assert.Equal(expected.HasConnectedBefore, actual.HasConnectedBefore);
        Assert.Equal(expected.RememberedEndpoints, actual.RememberedEndpoints);
        Assert.False(File.Exists(Path.Combine(_directory, "settings.json.tmp")));
    }

    [Fact]
    public async Task Load_CorruptFile_ReturnsDefaults()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        await File.WriteAllTextAsync(path, "{not-json");

        var actual = await new JsonAppSettingsStore(path).LoadAsync();

        Assert.Equal(AppSettings.Default, actual);
    }

    [Fact]
    public async Task Load_LockedFile_ReturnsDefaults()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        await File.WriteAllTextAsync(path, "{}");
        await using var lockStream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var actual = await new JsonAppSettingsStore(path).LoadAsync();

        Assert.Equal(AppSettings.Default, actual);
    }

    [Fact]
    public async Task Load_LegacySettings_UpgradesToRememberedHistoryWithoutAutoConnect()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "settings.json");
        await File.WriteAllTextAsync(path, """
            {
              "schemaVersion": 1,
              "theme": "System",
              "language": "zh-CN",
              "lastEndpoint": "pixel.local:5555",
              "autoRefresh": true,
              "mirrorProfileId": "balanced",
              "firstRunCompleted": true,
              "autoReconnect": true,
              "hasConnectedBefore": true
            }
            """);

        var loaded = await new JsonAppSettingsStore(path).LoadAsync();
        var upgraded = loaded.UpgradeConnectionHistory();

        Assert.Equal(AppSettings.CurrentSchemaVersion, upgraded.SchemaVersion);
        Assert.Equal(["pixel.local:5555"], upgraded.RememberedEndpoints);
        Assert.False(upgraded.AutoReconnect);
    }

    [Fact]
    public async Task Save_ConcurrentStoreInstancesKeepCompleteSettingsAndDoNotLeaveTemporaryFiles()
    {
        var path = Path.Combine(_directory, "settings.json");
        var candidates = Enumerable.Range(0, 24)
            .Select(index => AppSettings.Default with { LastEndpoint = $"{index}:" + new string('x', 64 * 1024) })
            .ToArray();

        await Task.WhenAll(candidates.Select(settings => new JsonAppSettingsStore(path).SaveAsync(settings)));
        var actual = await new JsonAppSettingsStore(path).LoadAsync();

        Assert.Contains(candidates, item => item.LastEndpoint == actual.LastEndpoint);
        Assert.Equal([path], Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task Save_PreCancelledOperationPreservesSavedSettings()
    {
        var path = Path.Combine(_directory, "settings.json");
        var store = new JsonAppSettingsStore(path);
        await store.SaveAsync(AppSettings.Default with { LastEndpoint = "original.local:5555" });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.SaveAsync(AppSettings.Default, new CancellationToken(true)));

        Assert.Equal("original.local:5555", (await store.LoadAsync()).LastEndpoint);
        Assert.Equal([path], Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task Save_CanReplaceFileWhileAnotherReaderRetainsPreviousSnapshot()
    {
        var path = Path.Combine(_directory, "settings.json");
        var store = new JsonAppSettingsStore(path);
        await store.SaveAsync(AppSettings.Default);
        await using var previousSnapshot = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete);

        await store.SaveAsync(AppSettings.Default with { Theme = "Dark" });

        Assert.Equal("Dark", (await store.LoadAsync()).Theme);
        using var reader = new StreamReader(previousSnapshot);
        Assert.Contains("System", await reader.ReadToEndAsync());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
