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
    dotnet restore src/TogetherServer/TogetherServer.csproj --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked Host restore failed' }
    dotnet build src/TogetherServer.Fixture/TogetherServer.Fixture.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Fixture build failed' }
    dotnet build src/TogetherServer.ValheimFixture/TogetherServer.ValheimFixture.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Valheim console fixture build failed' }
    dotnet build src/TogetherServer.MinecraftFixture/TogetherServer.MinecraftFixture.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Minecraft console fixture build failed' }
    dotnet publish src/TogetherServer/TogetherServer.csproj -c Release --no-restore -o local-data/publish
    if ($LASTEXITCODE -ne 0) { throw 'Host publish failed' }
    $releaseDirectory = Join-Path $repository "local-data/$ReleaseName"
    New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repository 'local-data/publish/TogetherServer.exe') -Destination (Join-Path $releaseDirectory 'TogetherServer.exe') -Force
    if ($ReleaseName -eq 'release-candidate') {
        Write-Host "Production-mode candidate (do not open beside production): local-data/$ReleaseName/TogetherServer.exe"
        Write-Host 'For the isolated persistent development app, run scripts/prepare-staging-package.ps1 and open TogetherServer DEVELOPMENT.exe.'
    }
    else {
        Write-Host "Production app: local-data/$ReleaseName/TogetherServer.exe"
    }
}
finally { Pop-Location }
