param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipPortableBuild,
    [string]$NsisCompilerPath = $env:NSIS_COMPILER,
    [string]$SignCommand = $env:ADB_MIRROR_SIGN_COMMAND
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-common.ps1')
$commercialRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$releaseScript = Join-Path $PSScriptRoot 'build-release.ps1'
$installerScript = Join-Path $commercialRoot 'installer\AdbMirrorStudio.nsi'
$installerRoot = Join-Path $commercialRoot 'artifacts\installer'
$releaseRoot = Join-Path $commercialRoot 'artifacts\release'
$stagingDirectory = Join-Path $installerRoot ".staging-$([Guid]::NewGuid().ToString('N'))"
$payloadDirectory = Join-Path $stagingDirectory 'payload'
$licensePath = Join-Path $stagingDirectory 'FREE-USE-LICENSE.txt'
$uninstallInclude = Join-Path $stagingDirectory 'uninstall-files.nsh'

$releaseVersion = Get-ReleaseVersion -CommercialRoot $commercialRoot
$version = $releaseVersion.Version
$fileVersion = $releaseVersion.FileVersion
$productVersion = "V$version"
$portableArchive = Join-Path $releaseRoot "AdbMirrorStudio-$productVersion-win-x64.zip"
$installerName = "ADB-Mirror-Studio-Setup-$productVersion-win-x64.exe"
$installerPath = Join-Path $installerRoot $installerName

Assert-ReleaseChildPath -Root $commercialRoot -Path $stagingDirectory

if (-not $SkipPortableBuild) {
    & $releaseScript -Configuration $Configuration
}
if (-not (Test-Path -LiteralPath $portableArchive)) {
    throw "未找到便携发行包：$portableArchive"
}

if ([string]::IsNullOrWhiteSpace($NsisCompilerPath)) {
    $command = Get-Command 'makensis.exe' -ErrorAction SilentlyContinue
    if ($command) { $NsisCompilerPath = $command.Source }
}
if ([string]::IsNullOrWhiteSpace($NsisCompilerPath)) {
    $compilerCandidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\NSIS\makensis.exe'),
        (Join-Path $env:ProgramFiles 'NSIS\makensis.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'NSIS\makensis.exe')
    )
    $NsisCompilerPath = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($NsisCompilerPath) -or -not (Test-Path -LiteralPath $NsisCompilerPath)) {
    throw '未找到 NSIS 编译器。请安装 NSIS 3，或通过 NSIS_COMPILER 指定 makensis.exe。'
}

New-Item -ItemType Directory -Path $payloadDirectory -Force | Out-Null
try {
    Expand-Archive -LiteralPath $portableArchive -DestinationPath $payloadDirectory -Force
    Assert-ReleasePayload -PayloadDirectory $payloadDirectory -ExpectedFileVersion $fileVersion
    Write-UninstallPayloadInclude -PayloadDirectory $payloadDirectory -OutputPath $uninstallInclude

    $privacyText = Get-Content (Join-Path $commercialRoot 'PRIVACY.md') -Raw
    $licenseText = Get-Content (Join-Path $commercialRoot 'FREE-USE-LICENSE.md') -Raw
    $installerNotice = "$privacyText`r`n`r`n========================================`r`n`r`n$licenseText"
    [System.IO.File]::WriteAllText($licensePath, ($installerNotice -replace "`r?`n", "`r`n"), [System.Text.UTF8Encoding]::new($true))
    $payloadBytes = (Get-ChildItem $payloadDirectory -Recurse -File | Measure-Object -Property Length -Sum).Sum
    $estimatedSizeKb = [Math]::Ceiling($payloadBytes / 1KB)

    New-Item -ItemType Directory -Path $installerRoot -Force | Out-Null
    $stagedInstaller = Join-Path $stagingDirectory $installerName

    $compilerArguments = @(
        '/INPUTCHARSET',
        'UTF8',
        '/WX',
        '/V3',
        "/DAPP_VERSION=$version",
        "/DAPP_FILE_VERSION=$fileVersion",
        "/DSOURCE_DIR=$payloadDirectory",
        "/DLICENSE_FILE=$licensePath",
        "/DOUTPUT_DIR=$stagingDirectory",
        "/DUNINSTALL_INCLUDE=$uninstallInclude",
        "/DESTIMATED_SIZE_KB=$estimatedSizeKb"
    )
    if (-not [string]::IsNullOrWhiteSpace($SignCommand)) {
        if (-not $SignCommand.Contains('%1', [System.StringComparison]::Ordinal)) {
            throw '签名命令必须包含由 NSIS 替换为目标文件路径的 %1 占位符。'
        }
        $compilerArguments += "/DSIGN_COMMAND=$SignCommand"
    }
    $compilerArguments += $installerScript

    & $NsisCompilerPath @compilerArguments
    if ($LASTEXITCODE -ne 0) { throw 'NSIS 编译失败。' }
    if (-not (Test-Path -LiteralPath $stagedInstaller)) { throw '安装器未生成。' }

    $signature = Get-AuthenticodeSignature -LiteralPath $stagedInstaller
    if (-not [string]::IsNullOrWhiteSpace($SignCommand) -and $signature.Status -ne 'Valid') {
        throw '指定了签名命令，但安装器未通过 Authenticode 签名验证。'
    }
    [IO.File]::Move($stagedInstaller, $installerPath, $true)
    $hash = Get-FileHash -LiteralPath $installerPath -Algorithm SHA256
    [pscustomobject]@{
        Version = $productVersion
        Installer = $installerPath
        Sha256 = $hash.Hash
        SizeBytes = (Get-Item -LiteralPath $installerPath).Length
        SignatureStatus = $signature.Status
    }
}
finally {
    Remove-ReleaseStagingDirectory -Root $commercialRoot -StagingDirectory $stagingDirectory
}
