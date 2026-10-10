param([switch]$Demo)
. "$PSScriptRoot\scripts\Common.ps1"
$published = Join-Path $PSScriptRoot 'publish\win-x64\PositionMonitor.exe'
$currentRelease = Join-Path $PSScriptRoot 'publish\当前版本.txt'
if (Test-Path -LiteralPath $currentRelease) {
    $currentApplication = (Get-Content -LiteralPath $currentRelease -Raw).Trim()
    if (Test-Path -LiteralPath $currentApplication) { $published = $currentApplication }
}
if (Test-Path -LiteralPath $published) {
    if ($Demo) { & $published --demo } else { & $published }
} else {
    $project = Join-Path $PSScriptRoot 'src\PositionMonitor.App\PositionMonitor.App.csproj'
    if ($Demo) { & $script:DotnetPath run --project $project -c Release -- --demo }
    else { & $script:DotnetPath run --project $project -c Release }
}
