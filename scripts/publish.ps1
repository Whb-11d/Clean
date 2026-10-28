param([string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
if ($Runtime -ne 'win-x64') { throw 'Only win-x64 is validated for this release.' }
$projectRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $projectRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$dotnet = if (Test-Path '.tools/dotnet/dotnet.exe') { Join-Path $projectRoot '.tools/dotnet/dotnet.exe' } else { 'dotnet' }
if ($dotnet -ne 'dotnet') {
    $env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools/cli'
    $env:NUGET_PACKAGES = Join-Path $projectRoot '.tools/packages'
}
$output = Join-Path $projectRoot "artifacts/publish/$Runtime"
& $dotnet publish src/Clean.App/Clean.App.csproj -c Release -r $Runtime --self-contained true -o $output -p:PublishTrimmed=false -p:PublishSingleFile=false
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
Copy-Item -LiteralPath LICENSE, README.md, THIRD-PARTY-NOTICES.md, CHANGELOG.md -Destination $output
Copy-Item -LiteralPath docs/user-guide.md -Destination $output
$revision = (& git rev-parse HEAD).Trim()
Set-Content -LiteralPath (Join-Path $output 'SOURCE-COMMIT.txt') -Value $revision -Encoding utf8
$archive = Join-Path $projectRoot "artifacts/Clean-0.1.0-$Runtime.zip"
Compress-Archive -Path "$output/*" -DestinationPath $archive -Force
$hash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath ($archive + '.sha256') -Value "$hash  $([IO.Path]::GetFileName($archive))" -Encoding ascii
Write-Output "Package: $archive"
Write-Output "SHA256: $hash"
Write-Output "Source: $revision"
