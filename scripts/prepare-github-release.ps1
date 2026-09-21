$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$project = [xml](Get-Content -LiteralPath (Join-Path $repository 'src/TogetherServer/TogetherServer.csproj') -Raw)
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'TogetherServer.csproj needs a stable MAJOR.MINOR.PATCH version.' }
$tag = "v$version"

& (Join-Path $PSScriptRoot 'build.ps1') -ReleaseName release-candidate
if ($LASTEXITCODE -ne 0) { throw 'Candidate build failed.' }

$assetName = 'TogetherServer-win-x64.exe'
$source = Join-Path $repository 'local-data/release-candidate/TogetherServer.exe'
$releaseDirectory = Join-Path $repository "local-data/github-release/$tag"
New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
$asset = Join-Path $releaseDirectory $assetName
Copy-Item -LiteralPath $source -Destination $asset -Force
$hash = (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath (Join-Path $releaseDirectory "$assetName.sha256") -Value "$hash  $assetName"
Write-Host "Prepared $tag at $asset"
Write-Host "SHA-256: $hash"
Write-Host "Publish a GitHub Release with tag $tag and attach $assetName after reviewing the candidate."
