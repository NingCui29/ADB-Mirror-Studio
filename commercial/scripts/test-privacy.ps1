param(
    [string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$originalLocation = Get-Location

try {
    Set-Location -LiteralPath $repositoryRoot
    # Read NUL-delimited UTF-8 paths so Unicode, spaces, and embedded newlines are not Git-quoted or split.
    $startInfo = [Diagnostics.ProcessStartInfo]::new('git')
    $startInfo.WorkingDirectory = $repositoryRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    foreach ($argument in @('ls-files', '--cached', '--others', '--exclude-standard', '-z')) {
        $startInfo.ArgumentList.Add($argument)
    }
    $gitProcess = [Diagnostics.Process]::new()
    $gitProcess.StartInfo = $startInfo
    try {
        $null = $gitProcess.Start()
        $outputTask = $gitProcess.StandardOutput.ReadToEndAsync()
        $errorTask = $gitProcess.StandardError.ReadToEndAsync()
        $gitProcess.WaitForExit()
        $fileOutput = $outputTask.GetAwaiter().GetResult()
        $null = $errorTask.GetAwaiter().GetResult()
        if ($gitProcess.ExitCode -ne 0) { throw '无法读取 Git 跟踪及新增文件列表。' }
        $auditFiles = @($fileOutput.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries) | Sort-Object -Unique)
    }
    finally {
        $gitProcess.Dispose()
    }

    $forbiddenPathPattern = '(?i)(^|/)(\.env($|\.)|settings\.json$|credentials\.json$|secrets\.json$|[^/]+\.(pdb|dbg|dmp|log|etl|pfx|p12|pem|key|snk|jks|keystore)$)'
    $forbiddenPaths = @($auditFiles | Where-Object {
        $_ -match $forbiddenPathPattern -and [IO.Path]::GetFileName($_) -ne '.env.example'
    })
    if ($forbiddenPaths.Count -gt 0) {
        Write-Error "检测到不应被 Git 跟踪的文件：$($forbiddenPaths -join ', ')"
    }

    $textExtensions = @(
        '', '.cs', '.csproj', '.gitignore', '.gitattributes', '.json', '.md', '.nsi', '.nsh',
        '.props', '.ps1', '.sln', '.slnx', '.targets', '.txt', '.xaml', '.xml', '.yaml', '.yml',
        '.py', '.js', '.ts', '.tsx', '.jsx', '.html', '.css', '.pubxml', '.config', '.manifest'
    )
    $sensitivePatterns = [ordered]@{
        '访问令牌' = '(?i)(github_pat_[A-Za-z0-9_]+|gh[pousr]_[A-Za-z0-9]{20,}|sk-[A-Za-z0-9_-]{20,}|AKIA[0-9A-Z]{16})'
        '私钥' = '-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----'
        '硬编码凭据' = '(?i)(password|passwd|secret|token|api[_-]?key)\s*[:=]\s*["''][^"'']{8,}'
        '本机用户路径' = '(?i)[A-Z]:[\\/]+Users[\\/]+[^\\/\s]+'
    }
    $contentViolations = [System.Collections.Generic.List[string]]::new()
    foreach ($relativePath in $auditFiles) {
        $extension = [IO.Path]::GetExtension($relativePath).ToLowerInvariant()
        if ($textExtensions -notcontains $extension -and [IO.Path]::GetFileName($relativePath) -ne '.env.example') { continue }
        $fullPath = Join-Path $repositoryRoot $relativePath
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) { continue }
        $content = [IO.File]::ReadAllText($fullPath)
        foreach ($entry in $sensitivePatterns.GetEnumerator()) {
            if ($content -match $entry.Value) {
                $contentViolations.Add("$relativePath [$($entry.Key)]")
            }
        }
    }
    if ($contentViolations.Count -gt 0) {
        Write-Error "检测到潜在敏感内容（仅显示文件和类别）：$($contentViolations -join ', ')"
    }

    Write-Output "隐私审计通过：已检查 $($auditFiles.Count) 个 Git 跟踪及新增未忽略文件。"
}
finally {
    Set-Location -LiteralPath $originalLocation
}
