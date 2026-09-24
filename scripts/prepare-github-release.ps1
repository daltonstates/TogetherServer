param(
    [string]$SigningCertificateThumbprint = $env:TOGETHERSERVER_SIGNING_THUMBPRINT,
    [string]$SignToolPath = '',
    [uri]$TimestampUrl = 'https://timestamp.digicert.com'
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot

Push-Location $repository
try {
    $dirty = @(git status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the Git worktree.' }
    if ($dirty.Count -ne 0) { throw 'Release preparation requires a clean Git worktree. Commit or remove every tracked and untracked change first.' }

    $project = [xml](Get-Content -LiteralPath 'src/TogetherServer/TogetherServer.csproj' -Raw)
    $version = [string]$project.Project.PropertyGroup.Version
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'TogetherServer.csproj needs a stable MAJOR.MINOR.PATCH version.' }
    $tag = "v$version"
    $localTags = @(git tag --list 'v*')
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect local release tags.' }
    $remoteRefs = @(git ls-remote --tags origin 'refs/tags/v*')
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect origin release tags.' }
    $remoteTags = @($remoteRefs | ForEach-Object {
        if ($_ -match 'refs/tags/(v\d+\.\d+\.\d+)(\^\{\})?$') { $Matches[1] }
    })
    $knownTags = @($localTags + $remoteTags | Sort-Object -Unique)
    if ($knownTags -contains $tag) { throw "Release tag $tag already exists locally or on origin." }
    $knownVersions = @($knownTags | ForEach-Object {
        if ($_ -match '^v(\d+\.\d+\.\d+)$') { [Version]$Matches[1] }
    })
    if ($knownVersions.Count -gt 0) {
        $latestVersion = $knownVersions | Sort-Object | Select-Object -Last 1
        if ([Version]$version -le $latestVersion) {
            throw "Release version $version must be newer than existing version $latestVersion."
        }
    }

    $releaseDirectory = Join-Path $repository "local-data/github-release/$tag"
    if (Test-Path -LiteralPath $releaseDirectory) { throw "Release directory already exists: $releaseDirectory" }

    & (Join-Path $PSScriptRoot 'build.ps1') -ReleaseName release-candidate
    if ($LASTEXITCODE -ne 0) { throw 'Candidate build failed.' }

    $source = Join-Path $repository 'local-data/release-candidate/TogetherServer.exe'
    if (!(Test-Path -LiteralPath $source -PathType Leaf)) { throw "Candidate EXE was not created: $source" }
    $signature = Get-AuthenticodeSignature -LiteralPath $source
    if ($signature.Status -ne 'Valid') {
        $thumbprint = ($SigningCertificateThumbprint -replace '\s', '').ToUpperInvariant()
        if ($thumbprint -notmatch '^[0-9A-F]{40}([0-9A-F]{24})?$') {
            throw 'The candidate is unsigned. Supply a CurrentUser/My code-signing certificate thumbprint with -SigningCertificateThumbprint or TOGETHERSERVER_SIGNING_THUMBPRINT.'
        }
        if ($SignToolPath) {
            $resolvedSignTool = (Resolve-Path -LiteralPath $SignToolPath).Path
        }
        else {
            $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
            if ($command) { $resolvedSignTool = $command.Source }
            else {
                $sdkRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10/bin'
                $resolvedSignTool = Get-ChildItem -LiteralPath $sdkRoot -Filter signtool.exe -File -Recurse -ErrorAction SilentlyContinue |
                    Where-Object { $_.FullName -match '[\\/]x64[\\/]signtool\.exe$' } |
                    Sort-Object FullName -Descending | Select-Object -First 1 -ExpandProperty FullName
            }
        }
        if (!$resolvedSignTool) { throw 'signtool.exe was not found. Install the Windows SDK or pass -SignToolPath.' }
        & $resolvedSignTool sign /sha1 $thumbprint /s My /fd SHA256 /td SHA256 /tr $TimestampUrl.AbsoluteUri $source
        if ($LASTEXITCODE -ne 0) { throw 'Authenticode signing failed.' }
        $signature = Get-AuthenticodeSignature -LiteralPath $source
    }
    if ($signature.Status -ne 'Valid' -or !$signature.SignerCertificate) {
        throw "Release candidate needs a valid Authenticode signature; status was $($signature.Status)."
    }

    & (Join-Path $PSScriptRoot 'verify-release.ps1') -AppPath $source -RequireSignature
    if ($LASTEXITCODE -ne 0) { throw 'Exact release-candidate verification failed.' }

    $assetName = 'TogetherServer-win-x64.exe'
    New-Item -ItemType Directory -Path $releaseDirectory | Out-Null
    $asset = Join-Path $releaseDirectory $assetName
    Copy-Item -LiteralPath $source -Destination $asset
    $sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
    $assetHash = (Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash
    if ($assetHash -ne $sourceHash) { throw 'Prepared asset bytes differ from the verified candidate.' }
    $assetSignature = Get-AuthenticodeSignature -LiteralPath $asset
    if ($assetSignature.Status -ne 'Valid' -or
        $assetSignature.SignerCertificate.Thumbprint -ne $signature.SignerCertificate.Thumbprint) {
        throw 'Prepared asset did not preserve the verified candidate signature.'
    }
    $hash = $assetHash.ToLowerInvariant()
    Set-Content -LiteralPath (Join-Path $releaseDirectory "$assetName.sha256") -Value "$hash  $assetName" -Encoding ascii
    Write-Host "Prepared signed $tag at $asset"
    Write-Host "SHA-256: $hash"
    Write-Host "Signer: $($assetSignature.SignerCertificate.Subject)"
    Write-Host "Publish a GitHub Release with tag $tag and attach $assetName plus its .sha256 file after reviewing the candidate."
}
finally { Pop-Location }
