using System.Collections.Concurrent;
using System.Text.Json;
using AdbMirrorStudio.Application.Settings;
using AdbMirrorStudio.Domain.Settings;
using AdbMirrorStudio.Infrastructure.Serialization;

namespace AdbMirrorStudio.Infrastructure.Persistence;

public sealed class JsonAppSettingsStore(string filePath) : IAppSettingsStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = Gates.GetOrAdd(Path.GetFullPath(filePath), _ => new SemaphoreSlim(1, 1));
    private readonly string _filePath = Path.GetFullPath(filePath);

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_filePath)) return AppSettings.Default;
            await using var stream = new FileStream(_filePath, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous);
            return await JsonSerializer.DeserializeAsync(
                    stream,
                    AdbMirrorStudioJsonContext.Default.AppSettings,
                    cancellationToken)
                .ConfigureAwait(false) ?? AppSettings.Default;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            return AppSettings.Default;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporaryPath = _filePath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.WriteThrough | FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        settings,
                        AdbMirrorStudioJsonContext.Default.AppSettings,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(_filePath))
            {
                File.Replace(temporaryPath, _filePath, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, _filePath);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Preserve the primary save result when a scanner briefly locks the temporary file.
            }
            _gate.Release();
        }
    }
}
