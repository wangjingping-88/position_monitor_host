param([switch]$SkipTests)
. "$PSScriptRoot\Common.ps1"
if (-not $SkipTests) { & "$PSScriptRoot\Build.ps1" }
[xml]$properties = Get-Content -LiteralPath "$script:ProjectRoot\Directory.Build.props"
$version = [string]$properties.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw '版本号必须为三段数字。' }
$staging = Join-Path $script:ProjectRoot ("artifacts\release-staging-" + [guid]::NewGuid().ToString('N'))
& "$PSScriptRoot\Publish.ps1" -OutputDirectory $staging
if (Test-Path -LiteralPath "$staging\data") { throw '发布包不得包含用户 data 目录。' }
$destination = Join-Path $script:ProjectRoot "artifacts\releases\v$version"
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$archive = Join-Path $destination "PositionMonitor-v$version-win-x64-portable.zip"
Add-Type -AssemblyName System.IO.Compression.FileSystem
$temporaryArchive = "$archive.$([guid]::NewGuid().ToString('N')).tmp"
[IO.Compression.ZipFile]::CreateFromDirectory($staging, $temporaryArchive, [IO.Compression.CompressionLevel]::Optimal, $false)
Move-Item -LiteralPath $temporaryArchive -Destination $archive -Force
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$archive.sha256.txt", "$hash  $([IO.Path]::GetFileName($archive))`n", [Text.UTF8Encoding]::new($false))
Write-Host "Release 包：$archive"
Write-Host "校验文件：$archive.sha256.txt"
