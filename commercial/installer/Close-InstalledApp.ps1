[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InstallDirectory,
    [Parameter(Mandatory)][ValidateSet('Request', 'Check', 'Force')][string]$Action
)

$ErrorActionPreference = 'Stop'
$relativePaths = @('AdbMirrorStudio.App.exe')
if ($Action -ne 'Request') { $relativePaths += 'Tools\scrcpy.exe', 'Tools\adb.exe' }

$sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
$found = $false
foreach ($relativePath in $relativePaths) {
    $targetPath = [IO.Path]::GetFullPath((Join-Path $InstallDirectory $relativePath))
    $processName = [IO.Path]::GetFileNameWithoutExtension($targetPath)
    foreach ($process in [Diagnostics.Process]::GetProcessesByName($processName)) {
        try {
            if ($process.SessionId -ne $sessionId) { continue }
            # Keep a handle open through the path check and termination to pin the process identity.
            if ($Action -eq 'Force') { $null = $process.Handle }
            if (-not [string]::Equals($process.MainModule.FileName, $targetPath, [StringComparison]::OrdinalIgnoreCase)) {
                continue
            }

            $found = $true
            if ($Action -eq 'Request') { [void]$process.CloseMainWindow() }
            elseif ($Action -eq 'Force') { $process.Kill() }
        }
        catch [System.ComponentModel.Win32Exception], [System.InvalidOperationException], [System.NotSupportedException] {
            # Another process can exit or be inaccessible. The installer still checks for file locks.
        }
        finally { $process.Dispose() }
    }
}

if ($found) { exit 0 }
exit 1
