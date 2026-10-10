. "$PSScriptRoot\Common.ps1"
Push-Location $script:ProjectRoot
try {
    & $script:DotnetPath build PositionMonitor.sln -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw '项目构建失败。' }
    & $script:DotnetPath run --project tests\PositionMonitor.Tests\PositionMonitor.Tests.csproj -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw '自动验证失败。' }
    & $script:DotnetPath run --project tests\PositionMonitor.Update.Tests\PositionMonitor.Update.Tests.csproj -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw '在线升级验证失败。' }
} finally { Pop-Location }
