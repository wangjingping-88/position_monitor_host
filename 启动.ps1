. "$PSScriptRoot\scripts\Common.ps1"
$published = Join-Path $PSScriptRoot 'publish\win-x64\PositionMonitor.exe'
if (Test-Path -LiteralPath $published) { & $published }
else { & $script:DotnetPath run --project (Join-Path $PSScriptRoot 'src\PositionMonitor.App\PositionMonitor.App.csproj') -c Release }
