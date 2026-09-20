param([ValidateSet('release', 'release-candidate')][string]$ReleaseName = 'release')
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
Push-Location (Join-Path $repository 'ui')
try {
    npm ci
    if ($LASTEXITCODE -ne 0) { throw 'npm ci failed' }
    npm run build
    if ($LASTEXITCODE -ne 0) { throw 'UI build failed' }
}
finally { Pop-Location }

Push-Location $repository
try {
    dotnet build src/TogetherServer.Fixture/TogetherServer.Fixture.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Fixture build failed' }
    dotnet build src/TogetherServer.ValheimFixture/TogetherServer.ValheimFixture.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Valheim console fixture build failed' }
    dotnet publish src/TogetherServer/TogetherServer.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o local-data/publish
    if ($LASTEXITCODE -ne 0) { throw 'Host publish failed' }
    $releaseDirectory = Join-Path $repository "local-data/$ReleaseName"
    New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repository 'local-data/publish/TogetherServer.exe') -Destination (Join-Path $releaseDirectory 'TogetherServer.exe') -Force
    Write-Host "Ready to double-click: local-data/$ReleaseName/TogetherServer.exe"
}
finally { Pop-Location }
