param([switch]$Benchmark)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $projectRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$dotnet = if (Test-Path '.tools/dotnet/dotnet.exe') { Join-Path $projectRoot '.tools/dotnet/dotnet.exe' } else { 'dotnet' }
if ($dotnet -ne 'dotnet') {
    $env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools/cli'
    $env:NUGET_PACKAGES = Join-Path $projectRoot '.tools/packages'
}
& $dotnet restore Clean.slnx --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Dependency restore failed' }
& $dotnet build Clean.slnx -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Release build failed' }
if ($Benchmark) { $env:CLEAN_RUN_BENCHMARK = '1' }
& $dotnet test Clean.slnx -c Release --no-build --logger trx --results-directory artifacts/TestResults
if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
