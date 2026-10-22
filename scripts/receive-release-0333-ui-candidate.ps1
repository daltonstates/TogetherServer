# Read-only HTTP and static byte proof. Never loads, starts or rebuilds the app.
param([switch]$IncludeFixtures)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repository = Split-Path -Parent $PSScriptRoot
$sourceRevision = '7d6ab87a0d92f3a1e4c6f403a37a898e4807fa1d'
$candidateHash = '05af882c8ce710bad2ff6990220bdd82019c291f102c9c0ea36643a4d277318b'
$candidateRunId = 39819723346L
$candidateJobId = 119967477555L
$artifactId = 12443303157L
$archiveBytes = 61020009L
$archiveHash = 'a55676eb4b7e17698770d15713670e332fb3ffeadc2e2c597b70b0bf46a96d3a'
$indexHash = '0093ec5af1464d59502ea0ced4cd208dd68919fcb9697ce5490de9f44436b48f'
$api = 'https://api.github.com/repos/daltonstates/TogetherServer'
$headers = @{}

function Require([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
}

function Get-ReviewJson([string]$Path) {
    Invoke-RestMethod -Uri "$api/$Path" -Headers $headers -TimeoutSec 30
}

function Expand-ReviewArchive([string]$Zip, [string]$Destination) {
    $destinationPath = [IO.Path]::GetFullPath($Destination)
    $repositoryPrefix = [IO.Path]::GetFullPath($repository).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    Require ($destinationPath.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        !(Test-Path -LiteralPath $destinationPath)) 'Archive extraction requires fresh workspace storage.'
    $ancestor = [IO.DirectoryInfo]::new((Split-Path -Parent $destinationPath))
    while ($ancestor -and $ancestor.FullName.StartsWith($repositoryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        Require (!$ancestor.Exists -or ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'Archive extraction ancestry is linked.'
        $ancestor = $ancestor.Parent
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($Zip)
    $destinationPrefix = $destinationPath.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    try {
        Require ($archive.Entries.Count -gt 0 -and $archive.Entries.Count -le 4096) 'The archive inventory exceeds its bound.'
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        [long]$expandedBytes = 0
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName.TrimEnd('/')
            Require ($name.Length -gt 0 -and $name.Length -le 512 -and $name -notmatch '[\\:\x00-\x1f]' -and
                ![IO.Path]::IsPathRooted($name) -and $names.Add($name)) 'The archive contains an unsafe or duplicate path.'
            Require (@($name.Split('/') | Where-Object { $_ -in @('', '.', '..') -or $_ -match '[. ]$' -or
                $_ -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])($|\.)' }).Count -eq 0) 'The archive contains an unsafe path segment.'
            $target = [IO.Path]::GetFullPath((Join-Path $destinationPath $name))
            Require ($target.StartsWith($destinationPrefix, [StringComparison]::OrdinalIgnoreCase) -and
                (($entry.ExternalAttributes -shr 16) -band 61440) -ne 40960 -and
                ($entry.ExternalAttributes -band [int][IO.FileAttributes]::ReparsePoint) -eq 0) 'The archive contains a link or escapes fresh extraction.'
            $expandedBytes += $entry.Length
            Require ($expandedBytes -le 536870912) 'The expanded artifact exceeds its fixed size bound.'
        }
    } finally { $archive.Dispose() }
    Expand-Archive -LiteralPath $Zip -DestinationPath $destinationPath
}

function Assert-UiAssets([string]$Root, [object[]]$Assets) {
    $files = @(Get-ChildItem -LiteralPath $Root -Recurse -File -Force)
    Require ($Assets.Count -eq 7 -and $files.Count -eq 7) 'The frozen UI must contain exactly seven files.'
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($asset in $Assets) {
        Require ($asset.asset -is [string] -and $asset.asset -cmatch '^(assets/[A-Za-z0-9_-]+\.(js|css)|favicon\.svg|index\.html)$' -and
            $names.Add($asset.asset) -and $asset.sizeBytes -gt 0 -and $asset.sha256 -cmatch '^[0-9a-f]{64}$') 'The package UI asset inventory is invalid.'
        $file = Get-Item -LiteralPath (Join-Path $Root $asset.asset)
        Require (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0 -and
            $file.Length -eq $asset.sizeBytes -and
            (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $asset.sha256) 'A UI asset differs from the frozen package evidence.'
    }
    foreach ($file in $files) {
        Require ($names.Contains([IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/'))) 'The UI contains an unrecorded file.'
    }
    Require ($names.Contains('index.html') -and $names.Contains('favicon.svg') -and
        (Get-FileHash -LiteralPath (Join-Path $Root 'index.html') -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $indexHash) 'The frozen UI entry assets differ.'
}

Push-Location $repository
try {
    Require (![string]::IsNullOrWhiteSpace($env:GH_TOKEN) -and
        ![string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) 'A read-only Actions token and runner temporary directory are required.'
    $checkRevision = (git rev-parse HEAD).Trim()
    Require ($LASTEXITCODE -eq 0 -and $checkRevision -cmatch '^[0-9a-f]{40}$' -and
        $checkRevision -ceq $env:GITHUB_SHA) 'The checkout must match its exact GitHub verification source.'
    git merge-base --is-ancestor $sourceRevision HEAD
    Require ($LASTEXITCODE -eq 0) 'The frozen candidate must be an ancestor of the check source.'
    $changed = @(git diff --no-renames --name-only $sourceRevision HEAD)
    Require ($LASTEXITCODE -eq 0) 'Could not compare verification inputs.'
    $allowed = '^(docs/.+|scripts/(simple-shared-hosting-review|run-local-pairing-ux)\.mjs|checks/TogetherServer\.CustomChecks/Program\.cs|scripts/receive-release-0333-(ui-)?candidate\.ps1|\.github/workflows/(ui-redesign-visual-review|release-0333-continuation|windows-ci)\.yml)$'
    Require (@($changed | Where-Object { $_ -cnotmatch $allowed }).Count -eq 0) 'Product, dependency, build or unrelated check source changed after the frozen candidate.'
    $dirty = @(git status --porcelain --untracked-files=no)
    Require ($LASTEXITCODE -eq 0 -and $dirty.Count -eq 0) 'Verification requires unchanged tracked checkout files.'
    $extraction = [IO.Path]::GetFullPath((Join-Path $repository 'local-data/retained0333'))
    $uiDestination = Join-Path $repository 'ui/dist'
    $contextRoot = Join-Path $repository 'local-data/ui-redesign-visual-review'
    Require (!(Test-Path -LiteralPath $extraction) -and !(Test-Path -LiteralPath $uiDestination) -and
        !(Test-Path -LiteralPath (Join-Path $contextRoot 'retained-ui-context.json'))) 'Refusing to replace retained candidate, UI or context output.'
    foreach ($directory in @($repository, (Join-Path $repository 'local-data'), (Join-Path $repository 'ui'), $contextRoot)) {
        if (Test-Path -LiteralPath $directory) {
            $parent = Get-Item -LiteralPath $directory
            Require ($parent.PSIsContainer -and ($parent.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'Output parents must be plain directories.'
        }
    }
    $headers = @{ Authorization = "Bearer $env:GH_TOKEN"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }
    $run = Get-ReviewJson "actions/runs/$candidateRunId"
    Require ($run.id -eq $candidateRunId -and $run.head_sha -ceq $sourceRevision -and
        $run.head_branch -ceq 'main' -and $run.run_attempt -eq 1 -and $run.event -ceq 'push' -and
        $run.workflow_id -eq 367811679 -and $run.path -ceq '.github/workflows/windows-ci.yml' -and
        $run.repository.id -eq 1379836840 -and $run.head_repository.id -eq 1379836840) 'Original main Windows workflow identity differs.'
    $job = Get-ReviewJson "actions/jobs/$candidateJobId"
    Require ($job.id -eq $candidateJobId -and $job.run_id -eq $candidateRunId -and
        $job.head_sha -ceq $sourceRevision -and $job.run_attempt -eq 1 -and $job.name -ceq 'candidate' -and
        $job.status -ceq 'completed' -and $job.conclusion -ceq 'success') 'Original candidate job identity or outcome differs.'
    $artifact = Get-ReviewJson "actions/artifacts/$artifactId"
    Require ($artifact.id -eq $artifactId -and $artifact.name -ceq 'windows-candidate' -and
        $artifact.expired -eq $false -and $artifact.workflow_run.id -eq $candidateRunId -and
        $artifact.workflow_run.head_sha -ceq $sourceRevision -and $artifact.workflow_run.head_branch -ceq 'main' -and
        $artifact.workflow_run.repository_id -eq 1379836840 -and $artifact.workflow_run.head_repository_id -eq 1379836840 -and
        $artifact.size_in_bytes -eq $archiveBytes -and $artifact.digest -ceq "sha256:$archiveHash") 'Pinned artifact provenance, size or digest differs.'
    $zip = Join-Path $env:RUNNER_TEMP "release-0333-ui-$artifactId.zip"
    Require (!(Test-Path -LiteralPath $zip)) 'Refusing to replace an existing candidate ZIP.'
    Invoke-WebRequest -Uri "$api/actions/artifacts/$artifactId/zip" -Headers $headers -OutFile $zip -TimeoutSec 120
    Require ((Get-Item -LiteralPath $zip).Length -eq $archiveBytes -and
        (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $archiveHash) 'The candidate ZIP differs from its pinned bytes.'
    Expand-ReviewArchive $zip $extraction
    $candidateRelativePath = 'local-data/retained0333/local-data/release-candidate/TogetherServer.exe'
    $candidate = Get-Item -LiteralPath (Join-Path $repository $candidateRelativePath)
    $identityPath = Join-Path $extraction 'local-data/release-candidate/candidate.json'
    Require ((Get-Item -LiteralPath $identityPath).Length -gt 0 -and
        (Get-Item -LiteralPath $identityPath).Length -le 16384) 'Candidate metadata size is invalid.'
    $identity = Get-Content -LiteralPath $identityPath -Raw | ConvertFrom-Json
    $numericVersion = [Version]::new($candidate.VersionInfo.FileMajorPart, $candidate.VersionInfo.FileMinorPart,
        $candidate.VersionInfo.FileBuildPart, $candidate.VersionInfo.FilePrivatePart)
    Require ($identity.source -ceq $sourceRevision -and $identity.runId -eq $candidateRunId -and
        $identity.runAttempt -eq 1 -and $identity.job -ceq 'candidate' -and $identity.version -ceq '0.3.3.3' -and
        $identity.sha256 -ceq $candidateHash -and $identity.productVersion -ceq "0.3.3.3+$sourceRevision" -and
        $identity.authenticode -ceq 'NotSigned' -and $candidate.Length -eq 66143076 -and
        (Get-FileHash -LiteralPath $candidate.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $candidateHash -and
        $numericVersion -eq [Version]'0.3.3.3' -and $candidate.VersionInfo.ProductVersion -ceq "0.3.3.3+$sourceRevision" -and
        [string](Get-AuthenticodeSignature -LiteralPath $candidate.FullName).Status -ceq 'NotSigned') 'Immutable candidate identity, app bytes, version or unsigned state differs.'
    $packagePath = Join-Path $extraction 'local-data/release-candidate/package-evidence.json'
    Require ((Get-Item -LiteralPath $packagePath).Length -gt 0 -and
        (Get-Item -LiteralPath $packagePath).Length -le 65536) 'Package evidence size is invalid.'
    $package = Get-Content -LiteralPath $packagePath -Raw | ConvertFrom-Json
    Require ($package.schemaVersion -eq 1 -and $package.acceptance -ceq 'static-package-only; app not loaded or launched' -and
        $package.app.name -ceq 'TogetherServer.exe' -and $package.app.sha256 -ceq $candidateHash -and
        $package.app.sizeBytes -eq $candidate.Length -and $package.app.peSubsystem -eq 2 -and
        $package.assembly.matchesCurrentBuild -eq $true) 'Package evidence is not bound to this exact candidate.'
    $assets = @($package.assets)
    $sourceUi = Join-Path $extraction 'ui/dist'
    Assert-UiAssets $sourceUi $assets
    Require (!(Test-Path -LiteralPath $uiDestination)) 'Refusing to overwrite an existing UI bundle.'
    Copy-Item -LiteralPath $sourceUi -Destination $uiDestination -Recurse
    Assert-UiAssets $uiDestination $assets
    if ($IncludeFixtures) {
        foreach ($outputRoot in @('local-data/release-candidate',
            'src/TogetherServer.Fixture/bin/Release/net10.0', 'src/TogetherServer.ValheimFixture/bin/Release/net10.0',
            'src/TogetherServer.MinecraftFixture/bin/Release/net10.0', 'src/TogetherServer.FactorioFixture/bin/Release/net10.0',
            'src/TogetherServer.TerrariaFixture/bin/Release/net10.0')) {
            $source = Join-Path $extraction $outputRoot
            $destination = Join-Path $repository $outputRoot
            Require (!(Test-Path -LiteralPath $destination) -and (Test-Path -LiteralPath $source -PathType Container)) 'Candidate and fixture outputs must be fresh.'
            $sourceFiles = @(Get-ChildItem -LiteralPath $source -Recurse -File -Force)
            Require ($sourceFiles.Count -gt 0) 'A retained candidate or fixture output is empty.'
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
            Copy-Item -LiteralPath $source -Destination $destination -Recurse
            foreach ($sourceFile in $sourceFiles) {
                $copied = Get-Item -LiteralPath (Join-Path $destination ([IO.Path]::GetRelativePath($source, $sourceFile.FullName)))
                Require ($copied.Length -eq $sourceFile.Length -and
                    (Get-FileHash -LiteralPath $copied.FullName -Algorithm SHA256).Hash -ceq
                    (Get-FileHash -LiteralPath $sourceFile.FullName -Algorithm SHA256).Hash) 'A copied candidate or fixture changed.'
            }
        }
    }
    New-Item -ItemType Directory -Path $contextRoot -Force | Out-Null
    [ordered]@{ sourceRevision = $sourceRevision; verificationRevision = $checkRevision;
        verificationRunId = $env:GITHUB_RUN_ID; verificationRunAttempt = $env:GITHUB_RUN_ATTEMPT; verificationJob = $env:GITHUB_JOB;
        candidateRunId = $candidateRunId; candidateRunAttempt = 1; candidateJobId = $candidateJobId;
        candidateArtifactId = $artifactId; candidateArtifactZipBytes = $archiveBytes; candidateArtifactZipSha256 = $archiveHash;
        candidateRelativePath = $candidateRelativePath; candidateBytes = $candidate.Length; candidateSha256 = $candidateHash;
        productVersion = $candidate.VersionInfo.ProductVersion; authenticode = 'NotSigned'; indexSha256 = $indexHash;
        candidateIdentitySha256 = (Get-FileHash -LiteralPath $identityPath -Algorithm SHA256).Hash.ToLowerInvariant();
        packageEvidenceSha256 = (Get-FileHash -LiteralPath $packagePath -Algorithm SHA256).Hash.ToLowerInvariant();
        uiAssets = @($assets | Select-Object asset,sizeBytes,sha256); uiAssetCount = 7; copiedUiHashesVerified = $true;
        allowedSourceChanges = $changed; rebuilt = $false; appLaunched = $false; fullyAccepted = $false;
        boundary = 'Exact retained candidate UI bytes, not a rebuilt demo bundle. Hosted synthetic UI review only; app processes, game, save, native, second-PC and WAN acceptance remain separate.' } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $contextRoot 'retained-ui-context.json') -Encoding utf8
    Write-Host 'PASS exact 0.3.3.3 candidate and seven UI assets; retained UI copied without rebuild or app launch.'
} finally {
    $headers.Clear()
    Pop-Location
}
