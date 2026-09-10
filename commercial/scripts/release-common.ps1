function Assert-ReleaseChildPath {
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string]$Path)
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $childPath = [IO.Path]::GetFullPath($Path)
    if (-not $childPath.StartsWith($rootPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw '拒绝操作发布根目录以外的路径。'
    }
    # A junction under artifacts must not redirect recursive cleanup to another directory.
    $currentPath = $childPath
    while ($currentPath.Length -ge $rootPath.Length) {
        if (Test-Path -LiteralPath $currentPath) {
            if (((Get-Item -LiteralPath $currentPath -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw '发布路径包含符号链接或目录联接，停止操作。'
            }
        }
        if ($currentPath -eq $rootPath) { break }
        $currentPath = [IO.Path]::GetDirectoryName($currentPath)
    }
}

function Remove-ReleaseStagingDirectory {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string]$StagingDirectory)
    try {
        if (-not (Test-Path -LiteralPath $StagingDirectory -ErrorAction Stop)) { return }
        Assert-ReleaseChildPath -Root $Root -Path $StagingDirectory
        Remove-Item -LiteralPath $StagingDirectory -Recurse -Force -ErrorAction Stop
    }
    catch {
        # Cleanup must never replace a build exception or turn a completed artifact into a failure.
        # Boundary validation remains mandatory: unsafe targets are retained without deletion.
        Write-Warning "发行临时目录未清理，已保留供稍后检查：$StagingDirectory。原因：$($_.Exception.Message)" -WarningAction Continue
    }
}

function Get-ReleaseVersion {
    param([Parameter(Mandatory)][string]$CommercialRoot)
    [xml]$properties = Get-Content -LiteralPath (Join-Path $CommercialRoot 'Directory.Build.props') -Raw
    $version = [string]$properties.Project.PropertyGroup.Version
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw '发行版本必须使用 Major.Minor.Patch 格式。' }
    $fileVersion = "$version.0"
    foreach ($pair in @(
        @('VersionPrefix', $version), @('AssemblyVersion', $fileVersion),
        @('FileVersion', $fileVersion), @('InformationalVersion', "V$version")
    )) {
        if ([string]$properties.Project.PropertyGroup.($pair[0]) -cne $pair[1]) {
            throw "Directory.Build.props 中 $($pair[0]) 与 Version 不一致。"
        }
    }
    if (([version]$fileVersion).Major -gt 65535 -or ([version]$fileVersion).Minor -gt 65535 -or ([version]$fileVersion).Build -gt 65535) {
        throw '文件版本字段必须介于 0 和 65535 之间。'
    }
    foreach ($manifest in @(
        @('src\AdbMirrorStudio.App\app.manifest', "/*[local-name()='assembly']/*[local-name()='assemblyIdentity']/@version"),
        @('src\AdbMirrorStudio.App\Package.appxmanifest', "/*[local-name()='Package']/*[local-name()='Identity']/@Version")
    )) {
        [xml]$document = Get-Content -LiteralPath (Join-Path $CommercialRoot $manifest[0]) -Raw
        if ($document.SelectSingleNode($manifest[1]).Value -cne $fileVersion) {
            throw "版本清单与 Directory.Build.props 不一致：$($manifest[0])"
        }
    }
    return [pscustomobject]@{ Version = $version; FileVersion = $fileVersion }
}

function Get-RequiredReleaseFiles {
    return @(
        'AdbMirrorStudio.App.exe', 'AdbMirrorStudio.App.dll', 'AdbMirrorStudio.App.deps.json',
        'AdbMirrorStudio.App.runtimeconfig.json', 'AdbMirrorStudio.Application.dll',
        'AdbMirrorStudio.Domain.dll', 'AdbMirrorStudio.Infrastructure.dll',
        'coreclr.dll', 'hostfxr.dll', 'hostpolicy.dll', 'Microsoft.ui.xaml.dll', 'WinRT.Runtime.dll',
        'AdbMirrorStudio.App.pri', 'Microsoft.UI.pri', 'Microsoft.UI.Xaml.Controls.pri', 'Microsoft.WindowsAppRuntime.pri',
        'Tools\adb.exe', 'Tools\AdbWinApi.dll', 'Tools\AdbWinUsbApi.dll',
        'Tools\scrcpy.exe', 'Tools\scrcpy-server', 'Tools\scrcpy.png',
        'Tools\avcodec-62.dll', 'Tools\avformat-62.dll', 'Tools\avutil-60.dll',
        'Tools\libusb-1.0.dll', 'Tools\SDL3.dll', 'Tools\swresample-6.dll',
        'Assets\AppIcon.ico', 'Licenses\Apache-2.0.txt', 'THIRD-PARTY-NOTICES.md',
        'PRIVACY.md', 'FREE-USE-LICENSE.md', 'README.md'
    )
}

function Assert-ReleasePayload {
    param([Parameter(Mandatory)][string]$PayloadDirectory, [Parameter(Mandatory)][string]$ExpectedFileVersion)
    foreach ($relativePath in Get-RequiredReleaseFiles) {
        $fullPath = Join-Path $PayloadDirectory $relativePath
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf) -or (Get-Item -LiteralPath $fullPath).Length -eq 0) {
            throw "发行负载缺少必要文件或文件为空：$relativePath"
        }
    }
    foreach ($item in Get-ChildItem -LiteralPath $PayloadDirectory -Recurse -Force) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw '发行负载不能包含符号链接或目录联接。'
        }
        if (-not $item.PSIsContainer -and ($item.Name -match '(?i)^(settings|credentials|secrets)\.json$|^\.env($|\.)' -or
            $item.Extension -match '(?i)^\.(pdb|dbg|dmp|log|etl|pfx|p12|pem|key|snk|jks|keystore)$')) {
            throw "发行负载包含禁止文件：$([IO.Path]::GetRelativePath($PayloadDirectory, $item.FullName))"
        }
    }
    $actualVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $PayloadDirectory 'AdbMirrorStudio.App.dll')).FileVersion
    if ($actualVersion -ne $ExpectedFileVersion) {
        throw '发行负载版本与当前发布版本不一致，请重新构建便携包。'
    }
}

function Write-UninstallPayloadInclude {
    param([Parameter(Mandatory)][string]$PayloadDirectory, [Parameter(Mandatory)][string]$OutputPath)
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add('!macro RemoveInstalledPayload')
    foreach ($file in Get-ChildItem -LiteralPath $PayloadDirectory -Recurse -File -Force | Sort-Object FullName) {
        $relativePath = [IO.Path]::GetRelativePath($PayloadDirectory, $file.FullName).Replace('$', '$$')
        $lines.Add('  Delete "$INSTDIR\' + $relativePath + '"')
    }
    $lines.Add('  Delete "$INSTDIR\Uninstall.exe"')
    foreach ($directory in Get-ChildItem -LiteralPath $PayloadDirectory -Recurse -Directory -Force | Sort-Object { $_.FullName.Length } -Descending) {
        $relativePath = [IO.Path]::GetRelativePath($PayloadDirectory, $directory.FullName).Replace('$', '$$')
        $lines.Add('  RMDir "$INSTDIR\' + $relativePath + '"')
    }
    $lines.Add('  RMDir "$INSTDIR"')
    $lines.Add('!macroend')
    [IO.File]::WriteAllLines($OutputPath, $lines, [Text.UTF8Encoding]::new($false))
}
