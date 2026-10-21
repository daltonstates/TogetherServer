# Release-specific HTTP/download/static proof. This script never launches the app or checks.
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$revision = 'e3de9ee809947b48c4a5cd972ff11c09b7ace8c3'
$candidateHash = '685925c2f9964a26d06d1f8bbae59a8bae9f5a0c6637264ce6d0f03238e86d9e'
$metadataHash = 'b59bff8f9b471aaaef8e2dcd47e65e7017aa2e3b0a92b86a5491e34513f08c16'
$runId = 39691663270L
$candidateArtifactId = 12387549296L
$archiveHash = '3bef44b4d0d1020c2350b5582a2d76d95d8cacec7c6261ea4676fa1414c9b9db'
$api = 'https://api.github.com/repos/daltonstates/TogetherServer'
$headers = @{}

function Require([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
}

function Get-AutomaticJson([string]$Path) {
    Invoke-RestMethod -Uri "$api/$Path" -Headers $headers -TimeoutSec 30
}

Push-Location $repository
try {
    Require (![string]::IsNullOrWhiteSpace($env:GH_TOKEN) -and
        ![string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) 'Read-only Actions token and dedicated runner temporary directory are required.'
    $currentRevision = (git rev-parse HEAD).Trim()
    Require ($LASTEXITCODE -eq 0 -and $currentRevision -cmatch '^[0-9a-f]{40}$' -and
        $currentRevision -ceq $env:GITHUB_SHA) 'Verification checkout must match its exact GitHub source.'
    git merge-base --is-ancestor $revision HEAD
    Require ($LASTEXITCODE -eq 0) 'Verification source must descend from the exact compiled E3 candidate.'
    $changed = @(git diff --no-renames --name-only $revision HEAD)
    Require ($LASTEXITCODE -eq 0) 'Could not compare automatic verification inputs.'
    $allowed = '^(docs/|checks/TogetherServer\.CompanionChecks/SimpleSharedHostingJourney\.cs$|\.github/workflows/release-0331-automatic-checks\.yml$|scripts/receive-release-0331-automatic\.ps1$)'
    Require (@($changed | Where-Object { $_ -notmatch $allowed }).Count -eq 0) 'Product, dependency, installer or unrelated check inputs changed after E3 compilation.'
    $evidence = Join-Path $repository 'local-data/ci-evidence/automatic-candidate-0331'
    $extraction = Join-Path $repository 'local-data/automatic-candidate-0331-extraction'
    Require (!(Test-Path -LiteralPath $evidence) -and !(Test-Path -LiteralPath $extraction)) 'Automatic candidate proof requires fresh output directories.'
    $outputRoots = @('local-data/release-candidate', 'ui/dist',
        'src/TogetherServer.Fixture/bin/Release/net10.0', 'src/TogetherServer.ValheimFixture/bin/Release/net10.0',
        'src/TogetherServer.MinecraftFixture/bin/Release/net10.0', 'src/TogetherServer.FactorioFixture/bin/Release/net10.0',
        'src/TogetherServer.TerrariaFixture/bin/Release/net10.0')
    foreach ($outputRoot in $outputRoots) {
        Require (!(Test-Path -LiteralPath (Join-Path $repository $outputRoot))) 'Refusing to replace any of the seven immutable candidate output trees.'
    }
    $headers = @{ Authorization = "Bearer $env:GH_TOKEN"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }
    $run = Get-AutomaticJson "actions/runs/$runId"
    $jobResponse = Get-AutomaticJson "actions/runs/$runId/jobs?filter=all&per_page=100"
    Require ($run.id -eq $runId -and $run.head_sha -ceq $revision -and $run.run_attempt -eq 1 -and
        $run.workflow_id -eq 367811679 -and $run.path -ceq '.github/workflows/windows-ci.yml' -and
        (($run.status -ceq 'in_progress' -and [string]::IsNullOrWhiteSpace([string]$run.conclusion)) -or
         ($run.status -ceq 'completed' -and $run.conclusion -ceq 'failure')) -and
        $jobResponse.total_count -eq 15 -and @($jobResponse.jobs).Count -eq 15) 'Original E3 full-run identity or fifteen-job inventory changed.'
    $expectedJobs = @(
        @(119539055732L, 'candidate'),
        @(119540052532L, 'Verify Existing'),
        @(119540052655L, 'Verify QolApi'),
        @(119540052482L, 'Verify Browser'),
        @(119540052576L, 'Verify Rehearsal'),
        @(119540052349L, 'Verify local pairing and both-ended UX'),
        @(119540052540L, 'Verify automatic shared hosting'),
        @(119540052519L, 'Verify 0.3.0 compatibility'),
        @(119540052500L, 'Verify 0.3.1 compatibility'),
        @(119540052555L, 'Verify 0.3.2 compatibility'),
        @(119540052491L, 'Verify 0.3.2.1 compatibility'),
        @(119540052511L, 'Verify 0.3.2.2 compatibility'),
        @(119540052512L, 'Verify 0.3.3 compatibility'),
        @(119540052476L, 'Package candidate without full acceptance claim'),
        @(119540052556L, 'Verify Desktop')
    )
    foreach ($expected in $expectedJobs) {
        $job = @($jobResponse.jobs | Where-Object id -EQ $expected[0])
        Require ($job.Count -eq 1 -and $job[0].name -ceq $expected[1] -and $job[0].run_id -eq $runId -and
            $job[0].head_sha -ceq $revision -and $job[0].run_attempt -eq 1 -and
            $job[0].status -cin @('queued', 'pending', 'waiting', 'requested', 'in_progress', 'completed')) "Original E3 job identity mismatch: $($expected[1])"
    }
    $candidateJob = @($jobResponse.jobs | Where-Object id -EQ 119539055732L)[0]
    $automaticJob = @($jobResponse.jobs | Where-Object id -EQ 119540052540L)[0]
    Require ($candidateJob.status -ceq 'completed' -and $candidateJob.conclusion -ceq 'success' -and
        $automaticJob.status -ceq 'completed' -and $automaticJob.conclusion -ceq 'failure') 'The original candidate must have passed and the original automatic job must retain its failure.'
    $ordinaryStep = @($automaticJob.steps | Where-Object name -CEQ 'Verify two ordinary handoffs against the downloaded candidate')
    Require ($ordinaryStep.Count -eq 1 -and $ordinaryStep[0].status -ceq 'completed' -and
        $ordinaryStep[0].conclusion -ceq 'failure') 'The original ordinary automatic step must remain failed; diagnostic success is not acceptance.'
    $artifact = Get-AutomaticJson "actions/artifacts/$candidateArtifactId"
    Require ($artifact.id -eq $candidateArtifactId -and $artifact.name -ceq 'windows-candidate' -and $artifact.expired -eq $false -and
        $artifact.workflow_run.id -eq $runId -and $artifact.workflow_run.head_sha -ceq $revision -and
        $artifact.digest -ceq "sha256:$archiveHash" -and $artifact.size_in_bytes -gt 0 -and
        $artifact.size_in_bytes -le 536870912) 'Pinned E3 candidate artifact identity or archive digest mismatch.'
    $zip = Join-Path $env:RUNNER_TEMP "release-0331-automatic-$candidateArtifactId.zip"
    Require (!(Test-Path -LiteralPath $zip)) 'Refusing to replace an existing candidate ZIP.'
    Invoke-WebRequest -Uri "$api/actions/artifacts/$candidateArtifactId/zip" -Headers $headers -OutFile $zip -TimeoutSec 120
    $zipFile = Get-Item -LiteralPath $zip
    Require ($zipFile.Length -eq 61002320 -and
        (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $archiveHash) 'Downloaded E3 ZIP differs from its pinned bytes.'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    $root = [IO.Path]::GetFullPath($extraction).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    try {
        Require ($archive.Entries.Count -gt 0 -and $archive.Entries.Count -le 4096) 'Candidate archive entry count exceeds its bound.'
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        [long]$expandedBytes = 0
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName.TrimEnd('/')
            Require ($name.Length -gt 0 -and $name -notmatch '[\\:\x00-\x1f]' -and
                ![IO.Path]::IsPathRooted($name) -and $names.Add($name)) 'Candidate archive contains an unsafe or duplicate path.'
            $segments = @($name.Split('/'))
            Require (@($segments | Where-Object { $_ -in @('', '.', '..') -or $_ -match '[. ]$' }).Count -eq 0) 'Candidate archive contains an unsafe path segment.'
            $target = [IO.Path]::GetFullPath((Join-Path $extraction $name))
            Require ($target.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) -and
                (($entry.ExternalAttributes -shr 16) -band 61440) -ne 40960) 'Candidate archive contains a link or escapes its fresh extraction.'
            $expandedBytes += $entry.Length
            Require ($expandedBytes -le 536870912) 'Candidate expanded bytes exceed the fixed bound.'
        }
    }
    finally { $archive.Dispose() }
    Expand-Archive -LiteralPath $zip -DestinationPath $extraction
    $candidate = Join-Path $extraction 'local-data/release-candidate/TogetherServer.exe'
    $metadataPath = Join-Path $extraction 'local-data/release-candidate/candidate.json'
    $metadataFile = Get-Item -LiteralPath $metadataPath
    Require ($metadataFile.Length -gt 0 -and $metadataFile.Length -le 16384 -and
        (Get-FileHash -LiteralPath $metadataPath -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $metadataHash) 'E3 candidate metadata changed.'
    $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
    $file = Get-Item -LiteralPath $candidate
    $info = $file.VersionInfo
    $numeric = [Version]::new($info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart, $info.FilePrivatePart)
    Require ($metadata.source -ceq $revision -and $metadata.runId -eq $runId -and $metadata.runAttempt -eq 1 -and
        $metadata.job -ceq 'candidate' -and $metadata.version -ceq '0.3.3.1' -and $metadata.sha256 -ceq $candidateHash -and
        $metadata.productVersion -ceq "0.3.3.1+$revision" -and $metadata.authenticode -ceq 'NotSigned' -and
        $file.Length -eq 66131760 -and (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $candidateHash -and
        $numeric -eq [Version]'0.3.3.1' -and $info.ProductVersion.Trim() -ceq "0.3.3.1+$revision" -and
        (Get-AuthenticodeSignature -LiteralPath $candidate).Status -eq 'NotSigned') 'Immutable E3 app bytes, numeric/product version or candidate metadata mismatch.'
    $payloadFiles = @(Get-ChildItem -LiteralPath $extraction -Recurse -File)
    foreach ($payloadFile in $payloadFiles) {
        $relative = [IO.Path]::GetRelativePath($extraction, $payloadFile.FullName).Replace('\', '/')
        Require (@($outputRoots | Where-Object { $relative.StartsWith($_ + '/', [StringComparison]::Ordinal) }).Count -eq 1) 'Candidate archive contains output outside its seven reviewed trees.'
    }
    foreach ($outputRoot in $outputRoots) {
        $source = Join-Path $extraction $outputRoot
        Require ((Test-Path -LiteralPath $source -PathType Container) -and
            @(Get-ChildItem -LiteralPath $source -Recurse -File).Count -gt 0) 'A reviewed candidate output tree is missing or empty.'
        $destination = Join-Path $repository $outputRoot
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $destination -Recurse
    }
    foreach ($payloadFile in $payloadFiles) {
        $relative = [IO.Path]::GetRelativePath($extraction, $payloadFile.FullName)
        $copied = Get-Item -LiteralPath (Join-Path $repository $relative)
        Require ($copied.Length -eq $payloadFile.Length -and
            (Get-FileHash -LiteralPath $copied.FullName -Algorithm SHA256).Hash -ceq
            (Get-FileHash -LiteralPath $payloadFile.FullName -Algorithm SHA256).Hash) 'A restored candidate or fixture output changed.'
    }
    New-Item -ItemType Directory -Path $evidence | Out-Null
    $context = [ordered]@{ sourceRevision = $revision; candidateSha256 = $candidateHash; candidateBytes = $file.Length;
        candidateMetadataSha256 = $metadataHash; candidateArtifactId = $candidateArtifactId; candidateArtifactZipSha256 = $archiveHash;
        originalRunId = $runId; originalRunAttempt = 1; originalRunStatus = $run.status; originalRunConclusion = $run.conclusion;
        originalAutomaticJobId = $automaticJob.id; originalAutomaticJobStatus = $automaticJob.status;
        originalAutomaticJobConclusion = $automaticJob.conclusion; originalOrdinaryStepConclusion = $ordinaryStep[0].conclusion;
        originalJobs = @($jobResponse.jobs | Select-Object id, name, head_sha, run_attempt, status, conclusion);
        verificationRevision = $currentRevision; verificationRunId = $env:GITHUB_RUN_ID; verificationRunAttempt = $env:GITHUB_RUN_ATTEMPT;
        verificationJob = $env:GITHUB_JOB; immutableOutputTrees = $outputRoots; fullyAccepted = $false; nativeNotificationVerified = $false;
        boundary = 'HTTP/download/static E3 proof only. The original automatic job remains failed. Other original jobs are recorded without an acceptance claim; two fresh ordinary hosted handoffs must still pass.' }
    $context | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $evidence 'context.json') -Encoding utf8
    Write-Host "PASS static E3 candidate identity $candidateHash. Original automatic failure preserved; no full acceptance claim."
}
finally {
    $headers.Clear()
    Pop-Location
}
