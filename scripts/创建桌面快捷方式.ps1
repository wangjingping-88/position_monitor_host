param([string]$ApplicationPath = '')
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($ApplicationPath)) {
    $currentRelease = Join-Path (Split-Path -Parent $PSScriptRoot) 'publish\当前版本.txt'
    $ApplicationPath = if (Test-Path -LiteralPath $currentRelease) { (Get-Content -LiteralPath $currentRelease -Raw).Trim() } else { Join-Path (Split-Path -Parent $PSScriptRoot) 'publish\win-x64\PositionMonitor.exe' }
}
$application = (Resolve-Path -LiteralPath $ApplicationPath).Path
$shell = New-Object -ComObject WScript.Shell
$desktop = $shell.SpecialFolders.Item('Desktop')
$shortcutPath = Join-Path $desktop '倾角定位监测仪.lnk'
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $application
$shortcut.WorkingDirectory = Split-Path -Parent $application
$shortcut.IconLocation = "$application,0"
$shortcut.Description = '倾角定位监测仪 · MQTT 实时监测与设备控制'
$shortcut.Save()
Write-Host "已创建桌面快捷方式：$shortcutPath"
