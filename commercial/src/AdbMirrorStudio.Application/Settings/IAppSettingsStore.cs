using AdbMirrorStudio.Domain.Settings;

namespace AdbMirrorStudio.Application.Settings;

public interface IAppSettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);

    // Non-null when the last load had to fall back to defaults. The default
    // implementation keeps existing stores (and test doubles) source-compatible.
    string? LastLoadWarning => null;
}
