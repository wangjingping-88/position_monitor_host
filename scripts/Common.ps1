$ErrorActionPreference = 'Stop'
$script:ProjectRoot = Split-Path -Parent $PSScriptRoot
$script:DotnetPath = 'D:\Program Files\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $script:DotnetPath)) {
    throw '未找到 D:\Program Files\dotnet\dotnet.exe，请按 docs\环境与构建.md 配置 D 盘 .NET SDK。'
}
$env:DOTNET_ROOT = 'D:\Program Files\dotnet'
$env:DOTNET_CLI_HOME = 'D:\Program Files\dotnet-home'
$env:NUGET_PACKAGES = 'D:\Program Files\NuGetPackages'
$env:NUGET_HTTP_CACHE_PATH = 'D:\Program Files\NuGet\http-cache'
$env:TEMP = 'D:\Program Files\Temp'
$env:TMP = $env:TEMP
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
foreach ($path in @($env:DOTNET_CLI_HOME, $env:NUGET_PACKAGES, $env:NUGET_HTTP_CACHE_PATH, $env:TEMP)) {
    New-Item -ItemType Directory -Path $path -Force | Out-Null
}
