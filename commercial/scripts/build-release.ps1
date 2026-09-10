param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-common.ps1')
$commercialRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$workspaceRoot = (Resolve-Path (Join-Path $commercialRoot '..')).Path
$localDotnet = Join-Path $workspaceRoot '.tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localDotnet) { $localDotnet } else { 'dotnet' }
$project = Join-Path $commercialRoot 'src\AdbMirrorStudio.App\AdbMirrorStudio.App.csproj'
$tests = Join-Path $commercialRoot 'tests\AdbMirrorStudio.UnitTests\AdbMirrorStudio.UnitTests.csproj'
$privacyAudit = Join-Path $commercialRoot 'scripts\test-privacy.ps1'
$artifactRoot = Join-Path $commercialRoot 'artifacts\release'
$releaseVersion = Get-ReleaseVersion -CommercialRoot $commercialRoot
$productVersion = "V$($releaseVersion.Version)"
$archivePath = Join-Path $artifactRoot "AdbMirrorStudio-$productVersion-win-x64.zip"
$stagingDirectory = Join-Path $artifactRoot ".staging-$([Guid]::NewGuid().ToString('N'))"
$publishDirectory = Join-Path $stagingDirectory 'win-x64'

Assert-ReleaseChildPath -Root $commercialRoot -Path $stagingDirectory

New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
try {
    & $privacyAudit -RepositoryRoot $workspaceRoot

    & $dotnet test $tests --configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw '测试失败，停止发布。' }

    & $dotnet publish $project `
        --configuration $Configuration `
        --runtime win-x64 `
        --self-contained true `
        --output $publishDirectory `
        /p:Unpackaged=true `
        /p:PublishSingleFile=false `
        /p:PublishTrimmed=false `
        /p:DebugType=None `
        /p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw '发布构建失败。' }

    Copy-Item -LiteralPath (Join-Path $commercialRoot 'THIRD-PARTY-NOTICES.md') -Destination $publishDirectory
    Copy-Item -LiteralPath (Join-Path $commercialRoot 'PRIVACY.md') -Destination $publishDirectory
    Copy-Item -LiteralPath (Join-Path $commercialRoot 'FREE-USE-LICENSE.md') -Destination $publishDirectory
    Copy-Item -LiteralPath (Join-Path $commercialRoot 'README.md') -Destination $publishDirectory

    $excludedExtensions = @('.pdb', '.dbg', '.dmp', '.log', '.etl', '.pfx', '.p12', '.pem', '.key', '.snk', '.jks', '.keystore')
    $excludedNames = @('settings.json', 'credentials.json', 'secrets.json')
    $excludedReleaseFiles = @(Get-ChildItem -LiteralPath $publishDirectory -Recurse -File | Where-Object {
        $excludedExtensions -contains $_.Extension.ToLowerInvariant() -or
        $excludedNames -contains $_.Name.ToLowerInvariant() -or
        $_.Name.StartsWith('.env', [System.StringComparison]::OrdinalIgnoreCase)
    })
    foreach ($excludedFile in $excludedReleaseFiles) {
        Remove-Item -LiteralPath $excludedFile.FullName -Force
    }

    $remainingSensitiveFiles = @(Get-ChildItem -LiteralPath $publishDirectory -Recurse -File | Where-Object {
        $excludedExtensions -contains $_.Extension.ToLowerInvariant() -or
        $excludedNames -contains $_.Name.ToLowerInvariant() -or
        $_.Name.StartsWith('.env', [System.StringComparison]::OrdinalIgnoreCase)
    })
    if ($remainingSensitiveFiles.Count -gt 0) {
        throw "发布目录仍包含禁止文件：$($remainingSensitiveFiles.Name -join ', ')"
    }

    $localPathPattern = '(?i)[A-Z]:[\\/]Users[\\/][^\\/\x00\s]+'
    $localPathFiles = [System.Collections.Generic.List[string]]::new()
    $contentAuditFiles = @(Get-ChildItem -LiteralPath $publishDirectory -Recurse -File | Where-Object {
        $_.Length -le 32MB -or $_.Name.StartsWith('AdbMirrorStudio.', [System.StringComparison]::OrdinalIgnoreCase)
    })
    foreach ($contentAuditFile in $contentAuditFiles) {
        $bytes = [IO.File]::ReadAllBytes($contentAuditFile.FullName)
        $utf8Content = [Text.Encoding]::UTF8.GetString($bytes)
        $utf16Content = [Text.Encoding]::Unicode.GetString($bytes)
        if ($utf8Content -match $localPathPattern -or $utf16Content -match $localPathPattern) {
            $localPathFiles.Add($contentAuditFile.FullName.Substring($publishDirectory.Length + 1))
        }
    }
    if ($localPathFiles.Count -gt 0) {
        throw "发布目录包含本机用户路径：$($localPathFiles -join ', ')"
    }

    Assert-ReleasePayload -PayloadDirectory $publishDirectory -ExpectedFileVersion $releaseVersion.FileVersion
    $stagedArchive = Join-Path $stagingDirectory 'portable.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($publishDirectory, $stagedArchive, [IO.Compression.CompressionLevel]::Optimal, $false)
    # Retain the last working archive if publication or compression fails.
    [IO.File]::Move($stagedArchive, $archivePath, $true)

    $hash = Get-FileHash -LiteralPath $archivePath -Algorithm SHA256
    [pscustomobject]@{
        Version = $productVersion
        Archive = $archivePath
        Sha256 = $hash.Hash
        SizeBytes = (Get-Item -LiteralPath $archivePath).Length
    }
}
finally {
    Remove-ReleaseStagingDirectory -Root $commercialRoot -StagingDirectory $stagingDirectory
}
