param([switch]$FrameworkDependent, [switch]$DesktopShortcut, [string]$OutputDirectory = 'publish\win-x64')
. "$PSScriptRoot\Common.ps1"
$output = [System.IO.Path]::GetFullPath($(if ([System.IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory } else { Join-Path $script:ProjectRoot $OutputDirectory }))
$contained = if ($FrameworkDependent) { 'false' } else { 'true' }
& $script:DotnetPath publish "$script:ProjectRoot\src\PositionMonitor.App\PositionMonitor.App.csproj" -c Release -r win-x64 --self-contained $contained -o $output -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false --nologo
if ($LASTEXITCODE -ne 0) { throw '发布失败。' }
$updaterOutput = Join-Path $script:ProjectRoot 'artifacts\updater-win-x64'
& $script:DotnetPath publish "$script:ProjectRoot\src\PositionMonitor.Updater\PositionMonitor.Updater.csproj" -c Release -r win-x64 --self-contained true -o $updaterOutput -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false --nologo
if ($LASTEXITCODE -ne 0) { throw '独立更新器发布失败。' }
Copy-Item -LiteralPath "$updaterOutput\PositionMonitor.Updater.exe" -Destination "$output\PositionMonitor.Updater.exe" -Force
Copy-Item -LiteralPath "$script:ProjectRoot\README.md" -Destination "$output\使用说明.md" -Force
if ($output -eq (Join-Path $script:ProjectRoot 'publish\win-x64')) {
    Set-Content -LiteralPath "$script:ProjectRoot\publish\当前版本.txt" -Value "$output\PositionMonitor.exe" -Encoding utf8
}
Write-Host "发布完成：$output\PositionMonitor.exe"
if ($DesktopShortcut) { & "$PSScriptRoot\创建桌面快捷方式.ps1" -ApplicationPath "$output\PositionMonitor.exe" }
