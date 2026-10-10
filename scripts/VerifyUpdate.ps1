. "$PSScriptRoot\Common.ps1"
[xml]$properties = Get-Content -LiteralPath "$script:ProjectRoot\Directory.Build.props"
$version = [string]$properties.Project.PropertyGroup.Version
$archive = Join-Path $script:ProjectRoot "artifacts\releases\v$version\PositionMonitor-v$version-win-x64-portable.zip"
if (-not (Test-Path -LiteralPath $archive)) { throw '请先生成 Release 包。' }
$token = 'verify-' + [guid]::NewGuid().ToString('N')
$fixture = Join-Path $script:ProjectRoot "artifacts\$token"
$installation = Join-Path $fixture 'portable'
$staging = Join-Path $fixture ".position-monitor-stage-$token"
$backup = Join-Path $fixture ".position-monitor-backup-$token"
$updateRoot = 'D:\Program Files\PositionMonitorHost\updates'
$jobs = Join-Path $updateRoot "jobs\$token"
$runtime = Join-Path $updateRoot "runtime\$token"
New-Item -ItemType Directory -Path $jobs, $runtime -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::ExtractToDirectory($archive, $installation)
[IO.Compression.ZipFile]::ExtractToDirectory($archive, $staging)
New-Item -ItemType Directory -Path "$installation\data" -Force | Out-Null
$configuration = '{"Host":"127.0.0.1","Port":1,"Topic":"ucchip/up/sgw/+/#","UserName":"","ClientId":"update-verification"}'
[IO.File]::WriteAllText("$installation\data\connection.json", $configuration)
$confirmation = Join-Path $jobs 'confirmed.txt'
$job = @{
    CurrentProcessId = [int]::MaxValue
    InstallDirectory = $installation
    StagingDirectory = $staging
    BackupDirectory = $backup
    ApplicationFileName = 'PositionMonitor.exe'
    TargetVersion = $version
    ConfirmationFile = $confirmation
    LogFilePath = Join-Path $jobs 'updater.log'
    UpdateStateFilePath = Join-Path $jobs 'state.json'
}
$jobFile = Join-Path $jobs 'job.json'
[IO.File]::WriteAllText($jobFile, ($job | ConvertTo-Json))
$updater = Join-Path $runtime 'PositionMonitor.Updater.exe'
Copy-Item -LiteralPath "$installation\PositionMonitor.Updater.exe" -Destination $updater
$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $updateRoot 'runtime\native'
$process = Start-Process -FilePath $updater -ArgumentList '--job', ('"' + $jobFile + '"') -WindowStyle Hidden -PassThru
try {
    if (-not $process.WaitForExit(45000)) { throw '独立更新器验证超时。' }
    if ($process.ExitCode -ne 0) { throw "独立更新器退出码：$($process.ExitCode)。日志：$($job.LogFilePath)" }
    if (-not (Test-Path -LiteralPath $confirmation)) { throw '新版未完成真实 WPF 启动确认。' }
    if (Test-Path -LiteralPath $backup) { throw '启动确认后未清理旧版备份。' }
    if ([IO.File]::ReadAllText("$installation\data\connection.json") -ne $configuration) { throw '用户连接设置未保留。' }
    [IO.File]::WriteAllText("$script:ProjectRoot\artifacts\v$version-独立更新器验证.txt", "真实自包含更新器安装、WPF 启动确认、配置保留和备份清理全部通过。`r`n日志：$($job.LogFilePath)")
    Write-Host '真实自包含更新器安装、WPF 启动确认、配置保留和备份清理全部通过。'
} finally {
    # 仅关闭本次夹具中的程序，避免干扰用户正在运行的正式预览。
    Get-Process -Name PositionMonitor -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq "$installation\PositionMonitor.exe" } | ForEach-Object { $_.CloseMainWindow() | Out-Null; $_.WaitForExit(10000) | Out-Null }
}
