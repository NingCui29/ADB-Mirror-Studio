param([string]$NsisCompilerPath, [string]$AppBuildDirectory)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-common.ps1')
$commercialRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$privacyAudit = Join-Path $PSScriptRoot 'test-privacy.ps1'
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$fixtureRoot = Join-Path $temporaryRoot "adb-release-script-tests-$([Guid]::NewGuid().ToString('N'))"
$fixtureRepo = Join-Path $fixtureRoot 'repository with spaces'
$payload = Join-Path $fixtureRoot 'payload'
$passed = 0

function Assert-ScriptCondition([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
    $script:passed++
}

function Assert-PrivacyRejects([string]$ExpectedPath) {
    $message = ''
    try { $null = & $privacyAudit -RepositoryRoot $fixtureRepo }
    catch { $message = $_.Exception.Message }
    Assert-ScriptCondition ($message.Contains($ExpectedPath, [StringComparison]::Ordinal)) '隐私审计未识别预期文件。'
}

Assert-ReleaseChildPath -Root $temporaryRoot -Path $fixtureRoot
New-Item -ItemType Directory -Path $fixtureRepo, $payload -Force | Out-Null
try {
    & git -C $fixtureRepo init --quiet
    if ($LASTEXITCODE -ne 0) { throw '测试仓库初始化失败。' }
    [IO.File]::WriteAllText((Join-Path $fixtureRepo '正常 文件.cs'), '// ordinary source')
    & git -C $fixtureRepo add -- '正常 文件.cs'
    if ($LASTEXITCODE -ne 0) { throw '测试文件暂存失败。' }
    $auditResult = & $privacyAudit -RepositoryRoot $fixtureRepo
    Assert-ScriptCondition ($auditResult -match '隐私审计通过') '正常 Unicode 文件未通过审计。'

    $sensitivePath = Join-Path $fixtureRepo '新增 敏感.cs'
    # Deliberately synthetic credential fixture, assembled to avoid resembling a real secret in source.
    [IO.File]::WriteAllText($sensitivePath, 'gh' + 'p_' + ('A' * 32))
    Assert-PrivacyRejects '新增 敏感.cs'
    & git -C $fixtureRepo add -- '新增 敏感.cs'
    if ($LASTEXITCODE -ne 0) { throw 'Unicode 测试文件暂存失败。' }
    Assert-PrivacyRejects '新增 敏感.cs'
    [IO.File]::WriteAllText($sensitivePath, '// redacted fixture')

    [IO.File]::WriteAllText((Join-Path $fixtureRepo 'settings.json'), '{}')
    Assert-PrivacyRejects 'settings.json'
    Remove-Item -LiteralPath (Join-Path $fixtureRepo 'settings.json')
    [IO.File]::WriteAllText((Join-Path $fixtureRepo '.env.example'), '# environment template')
    $auditResult = & $privacyAudit -RepositoryRoot $fixtureRepo
    Assert-ScriptCondition ($auditResult -match '隐私审计通过') '无凭据的环境变量模板未通过审计。'
    [IO.File]::WriteAllText((Join-Path $fixtureRepo '.env.example'), 'gh' + 'p_' + ('B' * 32))
    Assert-PrivacyRejects '.env.example'
    [IO.File]::WriteAllText((Join-Path $fixtureRepo '.env.example'), '# environment template')

    foreach ($separator in @('/', '\', '\\')) {
        [IO.File]::WriteAllText($sensitivePath, 'C:' + $separator + 'Users' + $separator + 'FixtureAccount')
        Assert-PrivacyRejects '新增 敏感.cs'
    }
    [IO.File]::WriteAllText($sensitivePath, '// redacted fixture')

    $version = Get-ReleaseVersion -CommercialRoot $commercialRoot
    Assert-ScriptCondition ($version.FileVersion -eq "$($version.Version).0") '发布版本不一致。'
    $versionFixture = Join-Path $fixtureRoot 'version'
    $versionApp = Join-Path $versionFixture 'src\AdbMirrorStudio.App'
    New-Item -ItemType Directory -Path $versionApp -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $commercialRoot 'src\AdbMirrorStudio.App\app.manifest') -Destination $versionApp
    Copy-Item -LiteralPath (Join-Path $commercialRoot 'src\AdbMirrorStudio.App\Package.appxmanifest') -Destination $versionApp
    [xml]$versionProperties = Get-Content -LiteralPath (Join-Path $commercialRoot 'Directory.Build.props') -Raw
    $versionProperties.Project.PropertyGroup.FileVersion = '0.0.0.0'
    $versionProperties.Save((Join-Path $versionFixture 'Directory.Build.props'))
    $versionMismatchRejected = $false
    try { $null = Get-ReleaseVersion -CommercialRoot $versionFixture }
    catch { $versionMismatchRejected = $_.Exception.Message.Contains('FileVersion') }
    Assert-ScriptCondition $versionMismatchRejected '不一致的文件版本未被拒绝。'
    Assert-ReleaseChildPath -Root $fixtureRoot -Path (Join-Path $fixtureRoot 'artifacts\staging')
    $escapedPathRejected = $false
    try { Assert-ReleaseChildPath -Root $fixtureRoot -Path ($fixtureRoot + '-sibling\staging') }
    catch { $escapedPathRejected = $true }
    Assert-ScriptCondition $escapedPathRejected '前缀相同的兄弟目录未被拒绝。'
    $traversalRejected = $false
    try { Assert-ReleaseChildPath -Root $fixtureRoot -Path (Join-Path $fixtureRoot '..\outside') }
    catch { $traversalRejected = $true }
    Assert-ScriptCondition $traversalRejected '相对路径越界未被拒绝。'

    $lockedStaging = Join-Path $fixtureRoot 'locked-staging'
    New-Item -ItemType Directory -Path $lockedStaging -Force | Out-Null
    $lockedFile = [IO.File]::Open((Join-Path $lockedStaging 'locked.dll'), [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $completedResult = & {
            try { 'completed-artifact' }
            finally { Remove-ReleaseStagingDirectory -Root $fixtureRoot -StagingDirectory $lockedStaging }
        } 3>&1
        Assert-ScriptCondition ($completedResult -contains 'completed-artifact' -and (Test-Path -LiteralPath $lockedStaging)) '文件占用导致成功构建结果丢失。'
        $primaryFailure = ''
        try {
            try { throw 'primary-build-failure' }
            finally { Remove-ReleaseStagingDirectory -Root $fixtureRoot -StagingDirectory $lockedStaging 3>$null }
        }
        catch { $primaryFailure = $_.Exception.Message }
        Assert-ScriptCondition ($primaryFailure -eq 'primary-build-failure') '清理失败覆盖了原始构建错误。'
    }
    finally { $lockedFile.Dispose() }
    Remove-ReleaseStagingDirectory -Root $fixtureRoot -StagingDirectory $lockedStaging
    Assert-ScriptCondition (-not (Test-Path -LiteralPath $lockedStaging)) '释放占用后未能清理测试临时目录。'
    Remove-ReleaseStagingDirectory -Root $payload -StagingDirectory $fixtureRepo 3>$null
    Assert-ScriptCondition (Test-Path -LiteralPath $fixtureRepo) '清理函数越过指定根目录删除了兄弟目录。'

    $incompletePayloadRejected = $false
    try { Assert-ReleasePayload -PayloadDirectory $payload -ExpectedFileVersion $version.FileVersion }
    catch { $incompletePayloadRejected = $_.Exception.Message.Contains('AdbMirrorStudio.App.exe') }
    Assert-ScriptCondition $incompletePayloadRejected '残缺发行负载未被拒绝。'

    New-Item -ItemType Directory -Path (Join-Path $payload 'Tools\nested') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $payload 'AdbMirrorStudio.App.exe'), 'fixture')
    [IO.File]::WriteAllText((Join-Path $payload 'Tools\nested\中文 $file.dll'), 'fixture')
    $uninstallInclude = Join-Path $fixtureRoot 'uninstall.nsh'
    Write-UninstallPayloadInclude -PayloadDirectory $payload -OutputPath $uninstallInclude
    $manifest = Get-Content -LiteralPath $uninstallInclude -Raw
    Assert-ScriptCondition ($manifest.Contains('Delete "$INSTDIR\Tools\nested\中文 $$file.dll"')) '卸载清单未正确引用文件名。'
    Assert-ScriptCondition (-not $manifest.Contains('RMDir /r')) '卸载清单不应递归删除用户目录。'
    Assert-ScriptCondition ($manifest.IndexOf('RMDir "$INSTDIR\Tools\nested"') -lt $manifest.IndexOf('RMDir "$INSTDIR\Tools"')) '卸载目录未按从深到浅的顺序清理。'
    $installerSource = Get-Content -LiteralPath (Join-Path $commercialRoot 'installer\AdbMirrorStudio.nsi') -Raw
    Assert-ScriptCondition ($installerSource.Contains('!insertmacro RemoveInstalledPayload') -and -not $installerSource.Contains('RMDir /r "$INSTDIR"')) '安装器未使用精确卸载清单。'
    Assert-ScriptCondition (-not $installerSource.Contains('taskkill.exe') -and $installerSource.Contains('!insertmacro EnsureApplicationClosed Install') -and $installerSource.Contains('!insertmacro EnsureApplicationClosed Uninstall')) '安装器不应强制结束同名进程及录屏。'

    if (-not [string]::IsNullOrWhiteSpace($AppBuildDirectory)) {
        $releasePayload = Join-Path $fixtureRoot 'release-payload'
        New-Item -ItemType Directory -Path $releasePayload -Force | Out-Null
        foreach ($relativePath in Get-RequiredReleaseFiles) {
            $sourcePath = Join-Path $AppBuildDirectory $relativePath
            if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
                $sourcePath = Join-Path $commercialRoot $relativePath
            }
            $destinationPath = Join-Path $releasePayload $relativePath
            New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destinationPath)) -Force | Out-Null
            Copy-Item -LiteralPath $sourcePath -Destination $destinationPath
        }
        Assert-ReleasePayload -PayloadDirectory $releasePayload -ExpectedFileVersion $version.FileVersion
        $passed++
        Remove-Item -LiteralPath (Join-Path $releasePayload 'Tools\scrcpy-server')
        $missingServerRejected = $false
        try { Assert-ReleasePayload -PayloadDirectory $releasePayload -ExpectedFileVersion $version.FileVersion }
        catch { $missingServerRejected = $_.Exception.Message.Contains('scrcpy-server') }
        Assert-ScriptCondition $missingServerRejected '缺少设备端 scrcpy-server 的负载未被拒绝。'
        Copy-Item -LiteralPath (Join-Path $AppBuildDirectory 'Tools\scrcpy-server') -Destination (Join-Path $releasePayload 'Tools\scrcpy-server')
        $stalePayloadRejected = $false
        try { Assert-ReleasePayload -PayloadDirectory $releasePayload -ExpectedFileVersion '0.0.0.0' }
        catch { $stalePayloadRejected = $_.Exception.Message.Contains('版本') }
        Assert-ScriptCondition $stalePayloadRejected '旧版本负载未被拒绝。'
    }

    if (-not [string]::IsNullOrWhiteSpace($NsisCompilerPath)) {
        $license = Join-Path $fixtureRoot 'license.txt'
        [IO.File]::WriteAllText($license, 'Test license')
        $compilerArguments = @('/INPUTCHARSET', 'UTF8', '/WX', '/V2', '/PPO',
            "/DAPP_VERSION=$($version.Version)", "/DAPP_FILE_VERSION=$($version.FileVersion)",
            "/DSOURCE_DIR=$payload", "/DLICENSE_FILE=$license", "/DOUTPUT_DIR=$fixtureRoot",
            "/DUNINSTALL_INCLUDE=$uninstallInclude", (Join-Path $commercialRoot 'installer\AdbMirrorStudio.nsi'))
        $null = & $NsisCompilerPath @compilerArguments
        Assert-ScriptCondition ($LASTEXITCODE -eq 0) 'NSIS 预处理验证失败。'
    }
    Write-Output "发行脚本验证通过：$passed 项检查；未构建发行包或运行安装器。"
}
finally {
    Assert-ReleaseChildPath -Root $temporaryRoot -Path $fixtureRoot
    Remove-Item -LiteralPath $fixtureRoot -Recurse -Force
}
