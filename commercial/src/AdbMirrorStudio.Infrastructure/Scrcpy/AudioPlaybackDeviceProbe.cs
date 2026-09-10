using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AdbMirrorStudio.Infrastructure.Scrcpy;

internal static class AudioPlaybackDeviceProbe
{
    private const int DeviceNotFound = unchecked((int)0x80070490);

    public static bool IsAvailable() => !OperatingSystem.IsWindows() || HasDefaultWindowsOutput();

    [SupportedOSPlatform("windows")]
    private static bool HasDefaultWindowsOutput()
    {
        IMMDeviceEnumerator? enumerator = null;
        nint endpoint = 0;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            // Match SDL's WASAPI default: eRender = 0, eConsole = 0.
            var result = enumerator.GetDefaultAudioEndpoint(0, 0, out endpoint);
            // Only suppress playback when Windows confirms there is no default output.
            // Let scrcpy report other audio errors instead of silently muting them.
            return result != DeviceNotFound;
        }
        catch (COMException)
        {
            return true;
        }
        finally
        {
            if (endpoint != 0) Marshal.Release(endpoint);
            if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
        }
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator;

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        // Keep the native vtable order, including the unused first method.
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, uint stateMask, out nint devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out nint endpoint);
    }
}
