# Release-specific HTTP/download/static proof; never launches the app or checks.
param(
    [switch]$Existing,
    [long]$ExistingArtifactId = 0,
    [ValidatePattern('^$|^[0-9a-f]{64}$')][string]$ExistingArtifactSha256 = ''
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$revision = '4c2e774dd9e3a5a3b8e14575811a808aa755ef3a'
$candidateHash = 'e382e933ede963a0035215eed1512220525417f640dac651b717a6de88ba7cb0'
$runId = 39683376603L
$candidateArtifactId = 12384538572L
$candidateArchiveHash = '6ccb28da15f989d354b23b823959e5188e3ec1aff5b2eea35e7ec9f5dc15a513'
$continuationSource = 'fe932b4e50a611633471ecc7d267ad59666ff531'
$continuationRunId = 39687698900L
$continuationJobId = 119525840594L
$continuationArtifactId = 12386279991L
$continuationArchiveHash = '8fc4561fa35888b1c927038733a095668c077c7d5930015b21de4c3d83fba8fa'
$api = 'https://api.github.com/repos/daltonstates/TogetherServer'
$headers = @{}

function Require([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
}

function Get-FinalJson([string]$Path) {
    Invoke-RestMethod -Uri "$api/$Path" -Headers $headers -TimeoutSec 30
}

function Read-BoundedJson([string]$Path) {
    $file = Get-Item -LiteralPath $Path
    Require (!$file.PSIsContainer -and $file.Length -gt 0 -and $file.Length -le 2097152 -and
        ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'Required JSON proof is missing, linked or oversized.'
    Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
}

function Receive-FinalArtifact([long]$Id, [string]$Name, [string]$Hash, [string]$Destination,
    [long]$ExpectedRunId = $runId, [string]$ExpectedSource = $revision) {
    $artifact = Get-FinalJson "actions/artifacts/$Id"
    Require ($artifact.id -eq $Id -and $artifact.name -ceq $Name -and $artifact.expired -eq $false -and
        $artifact.workflow_run.id -eq $ExpectedRunId -and $artifact.workflow_run.head_sha -ceq $ExpectedSource -and
        $artifact.digest -ceq "sha256:$Hash" -and $artifact.size_in_bytes -gt 0 -and
        $artifact.size_in_bytes -le 536870912) 'Pinned final artifact identity or archive digest mismatch.'
    Require (!(Test-Path -LiteralPath $Destination)) 'Refusing to overwrite an existing artifact extraction.'
    $zip = Join-Path $env:RUNNER_TEMP "release-0331-final-$Id.zip"
    Require (!(Test-Path -LiteralPath $zip)) 'Refusing to overwrite an existing artifact download.'
    Invoke-WebRequest -Uri "$api/actions/artifacts/$Id/zip" -Headers $headers -OutFile $zip -TimeoutSec 120
    $zipFile = Get-Item -LiteralPath $zip
    Require ($zipFile.Length -gt 0 -and $zipFile.Length -le 536870912 -and
        (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $Hash) 'Final artifact ZIP bytes do not match their pinned SHA-256.'
    $root = [IO.Path]::GetFullPath($Destination).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        Require ($archive.Entries.Count -gt 0 -and $archive.Entries.Count -le 4096) 'Final artifact entry inventory exceeds its fixed bound.'
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        [long]$expandedBytes = 0
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName.TrimEnd('/')
            Require ($name.Length -gt 0 -and $name -notmatch '[\\:\x00-\x1f]' -and
                ![IO.Path]::IsPathRooted($name) -and $names.Add($name)) 'Final artifact contains an ambiguous or unexpected path.'
            $segments = @($name.Split('/'))
            Require (@($segments | Where-Object { $_ -in @('', '.', '..') -or $_ -match '[. ]$' }).Count -eq 0) 'Final artifact contains an unsafe path segment.'
            $target = [IO.Path]::GetFullPath((Join-Path $Destination $name))
            Require ($target.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) -and
                (($entry.ExternalAttributes -shr 16) -band 61440) -ne 40960) 'Final artifact contains a linked entry or escaped its fresh extraction.'
            $expandedBytes += $entry.Length
            Require ($expandedBytes -le 536870912) 'Final artifact expanded bytes exceed the fixed bound.'
        }
    }
    finally { $archive.Dispose() }
    Expand-Archive -LiteralPath $zip -DestinationPath $Destination
    [pscustomobject]@{ id = $Id; name = $Name; zipSha256 = $Hash; zipBytes = $zipFile.Length;
        root = [IO.Path]::GetFullPath($Destination) }
}

Push-Location $repository
try {
    Require (![string]::IsNullOrWhiteSpace($env:GH_TOKEN)) 'A read-only Actions token is required.'
    Require (![string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) 'The dedicated runner temporary directory is required.'
    Require ((!$Existing -and $ExistingArtifactId -eq 0 -and !$ExistingArtifactSha256) -or
        ($Existing -and $ExistingArtifactId -gt 0 -and $ExistingArtifactSha256 -cmatch '^[0-9a-f]{64}$')) 'Existing retention requires the root-reviewed artifact ID and SHA-256 together.'
    $currentRevision = (git rev-parse HEAD).Trim()
    Require ($LASTEXITCODE -eq 0 -and $currentRevision -cmatch '^[0-9a-f]{40}$') 'Current verification source is unavailable.'
    git merge-base --is-ancestor $revision HEAD
    Require ($LASTEXITCODE -eq 0) 'Verification HEAD must descend from the exact compiled candidate source.'
    $changed = @(git diff --no-renames --name-only $revision HEAD)
    Require ($LASTEXITCODE -eq 0) 'Could not compare final verification source inputs.'
    $allowed = '^(docs/|checks/TogetherServer\.CompanionChecks/(Program|LocalPairingLabJourney|SharedWorldJourney)\.cs$|\.github/workflows/release-0331-final-checks\.yml$|scripts/receive-release-0331-final\.ps1$)'
    Require (@($changed | Where-Object { $_ -notmatch $allowed }).Count -eq 0) 'Product, dependency, installer or unrelated check inputs changed after candidate compilation.'
    git merge-base --is-ancestor $continuationSource HEAD
    Require ($LASTEXITCODE -eq 0) 'Current verification source must descend from the retained nine-pass continuation source.'
    $continuationChanges = @(git diff --no-renames --name-only $continuationSource HEAD)
    Require ($LASTEXITCODE -eq 0) 'Could not compare retained continuation check inputs.'
    $continuationAllowed = '^(docs/|checks/TogetherServer\.CompanionChecks/(Program|LocalPairingLabJourney)\.cs$|\.github/workflows/release-0331-final-checks\.yml$|scripts/receive-release-0331-final\.ps1$)'
    Require (@($continuationChanges | Where-Object { $_ -notmatch $continuationAllowed }).Count -eq 0) 'A retained continuation input changed outside default CompanionChecks, the paired lab or release metadata.'
    $programPath = 'checks/TogetherServer.CompanionChecks/Program.cs'
    $referenceProgramLines = @(git show ($continuationSource + ':' + $programPath))
    Require ($LASTEXITCODE -eq 0) 'The retained check dispatcher source is unavailable.'
    $currentProgramLines = @(git show ('HEAD:' + $programPath))
    Require ($LASTEXITCODE -eq 0) 'The current check dispatcher source is unavailable.'
    $referenceProgram = $referenceProgramLines -join "`n"
    $currentProgram = $currentProgramLines -join "`n"
    $dispatcherBoundary = 'var root = Path.GetFullPath("local-data/companion-checks/'
    $referenceBoundary = $referenceProgram.IndexOf($dispatcherBoundary, [StringComparison]::Ordinal)
    $currentBoundary = $currentProgram.IndexOf($dispatcherBoundary, [StringComparison]::Ordinal)
    Require ($referenceBoundary -ge 0 -and $currentBoundary -ge 0 -and
        $referenceProgram.Substring(0, $referenceBoundary) -ceq $currentProgram.Substring(0, $currentBoundary)) 'A dispatcher prefix used by retained passing journeys changed; only the default body and its local functions may differ.'
    $evidence = Join-Path $repository 'local-data/ci-evidence/final-candidate'
    Require (!(Test-Path -LiteralPath $evidence)) 'Final candidate evidence must use a fresh directory.'
    New-Item -ItemType Directory -Path $evidence | Out-Null
    $headers = @{ Authorization = "Bearer $env:GH_TOKEN"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }
    $run = Get-FinalJson "actions/runs/$runId"
    $jobResponse = Get-FinalJson "actions/runs/$runId/jobs?filter=all&per_page=100"
    Require ($run.id -eq $runId -and $run.head_sha -ceq $revision -and $run.run_attempt -eq 1 -and
        $run.workflow_id -eq 367811679 -and $run.path -ceq '.github/workflows/windows-ci.yml' -and
        (($run.status -ceq 'in_progress' -and [string]::IsNullOrWhiteSpace([string]$run.conclusion)) -or
         ($run.status -ceq 'completed' -and $run.conclusion -ceq 'failure')) -and
        (!$Existing -or $run.status -ceq 'completed') -and
        $jobResponse.total_count -eq 15 -and @($jobResponse.jobs).Count -eq 15) 'The exact original full Windows run or fifteen-job inventory changed; Existing retention requires the completed run.'
    $expectedJobs = @(
        @(119511217481L, 'candidate', 'success'),
        @(119512118447L, 'Verify QolApi', 'success'),
        @(119512118492L, 'Verify Browser', 'success'),
        @(119512118507L, 'Verify Rehearsal', 'success'),
        @(119512118525L, 'Verify automatic shared hosting', 'success'),
        @(119512118491L, 'Verify 0.3.0 compatibility', 'success'),
        @(119512118620L, 'Verify 0.3.1 compatibility', 'success'),
        @(119512118566L, 'Verify 0.3.2 compatibility', 'success'),
        @(119512118567L, 'Verify 0.3.2.1 compatibility', 'success'),
        @(119512119103L, 'Verify 0.3.2.2 compatibility', 'success'),
        @(119512118771L, 'Verify 0.3.3 compatibility', 'success'),
        @(119512118530L, 'Package candidate without full acceptance claim', 'success'),
        @(119512118598L, 'Verify Existing', 'failure'),
        @(119512118522L, 'Verify Desktop', 'failure'),
        @(119512118628L, 'Verify local pairing and both-ended UX', 'failure')
    )
    foreach ($expected in $expectedJobs) {
        $job = @($jobResponse.jobs | Where-Object id -EQ $expected[0])
        Require ($job.Count -eq 1 -and $job[0].name -ceq $expected[1] -and $job[0].run_id -eq $runId -and
            $job[0].head_sha -ceq $revision -and $job[0].run_attempt -eq 1) "Original job identity mismatch: $($expected[1])"
        if ($expected[0] -eq 119512118598L -and !$Existing) {
            Require (($job[0].status -cin @('queued', 'pending', 'waiting', 'requested', 'in_progress') -and
                [string]::IsNullOrWhiteSpace([string]$job[0].conclusion)) -or
                ($job[0].status -ceq 'completed' -and $job[0].conclusion -cin @('success', 'failure'))) 'Original Existing job has an unexpected status or conclusion; its report remains unproven.'
        }
        else {
            Require ($job[0].status -ceq 'completed' -and $job[0].conclusion -ceq $expected[2]) "Original retained job conclusion mismatch: $($expected[1])"
        }
    }
    $unaffectedJobs = @($jobResponse.jobs | Where-Object { $_.id -notin @(119512118598L, 119512118522L, 119512118628L) })
    Require ($unaffectedJobs.Count -eq 12 -and @($unaffectedJobs | Where-Object conclusion -CEQ 'success').Count -eq 12) 'All twelve unaffected original jobs must have passed.'
    $originalExisting = @($jobResponse.jobs | Where-Object id -EQ 119512118598L)[0]
    $candidateArtifact = Receive-FinalArtifact $candidateArtifactId 'windows-candidate' $candidateArchiveHash (Join-Path $repository 'local-data/final-candidate-0331-extraction')
    $candidate = Join-Path $candidateArtifact.root 'local-data/release-candidate/TogetherServer.exe'
    $metadata = Read-BoundedJson (Join-Path $candidateArtifact.root 'local-data/release-candidate/candidate.json')
    $file = Get-Item -LiteralPath $candidate
    $info = $file.VersionInfo
    $numeric = [Version]::new($info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart, $info.FilePrivatePart)
    Require ($metadata.source -ceq $revision -and $metadata.runId -eq $runId -and $metadata.runAttempt -eq 1 -and
        $metadata.job -ceq 'candidate' -and $metadata.version -ceq '0.3.3.1' -and $metadata.sha256 -ceq $candidateHash -and
        $metadata.productVersion -ceq "0.3.3.1+$revision" -and $metadata.authenticode -ceq 'NotSigned' -and
        $file.Length -eq 66131757 -and (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $candidateHash -and
        $numeric -eq [Version]'0.3.3.1' -and $info.ProductVersion.Trim() -ceq "0.3.3.1+$revision" -and
        (Get-AuthenticodeSignature -LiteralPath $candidate).Status -eq 'NotSigned') 'Immutable final candidate bytes, version, source or build metadata mismatch.'
    $outputRoots = @('local-data/release-candidate', 'ui/dist',
        'src/TogetherServer.Fixture/bin/Release/net10.0', 'src/TogetherServer.ValheimFixture/bin/Release/net10.0',
        'src/TogetherServer.MinecraftFixture/bin/Release/net10.0', 'src/TogetherServer.FactorioFixture/bin/Release/net10.0',
        'src/TogetherServer.TerrariaFixture/bin/Release/net10.0')
    $payloadFiles = @(Get-ChildItem -LiteralPath $candidateArtifact.root -Recurse -File)
    foreach ($payloadFile in $payloadFiles) {
        $relative = [IO.Path]::GetRelativePath($candidateArtifact.root, $payloadFile.FullName).Replace('\', '/')
        Require (@($outputRoots | Where-Object { $relative.StartsWith($_ + '/', [StringComparison]::Ordinal) }).Count -eq 1) 'The candidate archive contains output outside its seven reviewed trees.'
    }
    foreach ($outputRoot in $outputRoots) {
        Require ((Test-Path -LiteralPath (Join-Path $candidateArtifact.root $outputRoot) -PathType Container) -and
            !(Test-Path -LiteralPath (Join-Path $repository $outputRoot))) 'Candidate outputs must exist in the archive and use fresh repository destinations.'
    }
    foreach ($outputRoot in $outputRoots) {
        $destination = Join-Path $repository $outputRoot
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $candidateArtifact.root $outputRoot) -Destination $destination -Recurse
    }
    Require ((Get-FileHash -LiteralPath (Join-Path $repository 'local-data/release-candidate/TogetherServer.exe') -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $candidateHash) 'Restored candidate bytes changed.'
    $context = [ordered]@{ sourceRevision = $revision; candidateSha256 = $candidateHash; candidateBytes = $file.Length;
        candidateArtifactId = $candidateArtifactId; candidateArtifactZipSha256 = $candidateArchiveHash;
        originalRunId = $runId; originalRunAttempt = 1; candidateJobId = 119511217481L;
        verificationRevision = $currentRevision; continuationRunId = $env:GITHUB_RUN_ID;
        continuationRunAttempt = $env:GITHUB_RUN_ATTEMPT; job = $env:GITHUB_JOB; allowedSourceChanges = $changed;
        originalJobs = @($jobResponse.jobs | Select-Object id, name, head_sha, run_attempt, status, conclusion);
        retainedSuccessfulJobs = 12; retainedFailedJobs = @($jobResponse.jobs | Where-Object conclusion -CEQ 'failure').Count;
        originalRunStatus = $run.status; originalRunConclusion = $run.conclusion;
        originalExistingJobId = $originalExisting.id; originalExistingStatus = $originalExisting.status;
        originalExistingConclusion = $originalExisting.conclusion; priorExistingProofPending = $true;
        continuationSourceChanges = $continuationChanges; dispatcherPrefixUnchanged = $true;
        fullyAccepted = $false; nativeNotificationVerified = $false;
        boundary = 'Only twelve unaffected successful jobs and exact Desktop/pairing failure facts are retained. Existing status is recorded but its report remains unproven until separately verified. This receiver performs HTTP/download/static proof only; functional continuation and physical-PC, WAN and real-game acceptance are separate.' }
    $context | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $evidence 'context.json') -Encoding utf8

    if ($Existing) {
        $listing = Get-FinalJson "actions/runs/$runId/artifacts?per_page=100"
        Require ($listing.total_count -le 100 -and @($listing.artifacts).Count -eq $listing.total_count) 'Original artifacts exceed the fixed single-page listing bound.'
        $existingArtifacts = @($listing.artifacts | Where-Object name -CEQ 'qol-windows-evidence-Existing')
        Require ($existingArtifacts.Count -eq 1 -and $existingArtifacts[0].id -eq $ExistingArtifactId -and
            $existingArtifacts[0].expired -eq $false -and $existingArtifacts[0].workflow_run.id -eq $runId -and
            $existingArtifacts[0].workflow_run.head_sha -ceq $revision -and
            $existingArtifacts[0].digest -ceq "sha256:$ExistingArtifactSha256") 'The unique root-pinned original Existing artifact is unavailable or changed.'
        $existingEvidence = Join-Path $repository 'local-data/ci-evidence/final-existing'
        Require (!(Test-Path -LiteralPath $existingEvidence)) 'Existing retention requires a fresh evidence directory.'
        New-Item -ItemType Directory -Path $existingEvidence | Out-Null
        $artifact = Receive-FinalArtifact $ExistingArtifactId 'qol-windows-evidence-Existing' $ExistingArtifactSha256 (Join-Path $existingEvidence 'original-artifact')
        $reports = @(Get-ChildItem -LiteralPath $artifact.root -Filter release-gate-report.json -File -Recurse)
        Require ($reports.Count -eq 1) 'The original Existing report is missing or ambiguous.'
        $report = Read-BoundedJson $reports[0].FullName
        Require ($report.suite -ceq 'Existing' -and $report.candidate.sourceRevision -ceq $revision -and
            $report.candidate.verificationRevision -ceq $revision -and $report.candidate.sha256 -ieq $candidateHash -and
            $report.candidate.productVersion -ceq "0.3.3.1+$revision" -and $report.candidate.authenticode -ceq 'NotSigned') 'The original Existing report belongs to another candidate.'
        $expectedChecks = @('Git whitespace check', 'Desktop diagnostic isolation', 'Check runner listener identity',
            'UI lint', 'UI unit tests', 'Locked application restore', 'TogetherServer.Checks', 'TogetherServer.ValheimChecks',
            'TogetherServer.FactorioChecks', 'TogetherServer.TerrariaChecks', 'TogetherServer.MinecraftChecks',
            'TogetherServer.MinecraftSetupChecks', 'TogetherServer.CustomChecks', 'TogetherServer.SharedHistoryChecks',
            'TogetherServer.UpdateChecks', 'Core remote journey', 'World-load packaged rehearsal')
        $checks = @($report.checks)
        Require ($checks.Count -eq 18 -and @($checks.name | Select-Object -Unique).Count -eq 18 -and
            @($checks | Where-Object outcome -CEQ 'passed').Count -eq 17 -and
            @($checks | Where-Object outcome -CEQ 'failed').Count -eq 1) 'Original partial Existing report must retain exactly seventeen passes and one failure.'
        for ($index = 0; $index -lt $expectedChecks.Count; $index++) {
            Require ($checks[$index].name -ceq $expectedChecks[$index] -and $checks[$index].outcome -ceq 'passed') 'A retained completed Existing stage differs from the reviewed ordered inventory.'
        }
        Require ($checks[17].name -ceq 'Shared Worlds packaged journey' -and $checks[17].outcome -ceq 'failed') 'The original Shared Worlds failure was omitted or changed.'
        Copy-Item -LiteralPath $reports[0].FullName -Destination (Join-Path $existingEvidence 'release-gate-report.json')
        $context.existingJobId = 119512118598L
        $context.existingArtifactId = $artifact.id
        $context.existingArtifactZipSha256 = $artifact.zipSha256
        $context.existingReportSha256 = (Get-FileHash -LiteralPath $reports[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $context.partialFact = $true
        $context.incomplete = $true
        $context.priorExistingProofPending = $false
        $context.checkCount = 18
        $context.passedCheckCount = 17
        $context.failedCheckCount = 1
        $context.failedCheck = 'Shared Worlds packaged journey'
        $continuedRun = Get-FinalJson "actions/runs/$continuationRunId"
        $continuedJob = Get-FinalJson "actions/jobs/$continuationJobId"
        Require ($continuedRun.id -eq $continuationRunId -and $continuedRun.head_sha -ceq $continuationSource -and
            $continuedRun.run_attempt -eq 1 -and $continuedRun.path -ceq '.github/workflows/release-0331-final-checks.yml' -and
            $continuedRun.status -ceq 'completed' -and $continuedRun.conclusion -ceq 'failure' -and
            $continuedJob.id -eq $continuationJobId -and $continuedJob.run_id -eq $continuationRunId -and
            $continuedJob.head_sha -ceq $continuationSource -and $continuedJob.run_attempt -eq 1 -and
            $continuedJob.name -ceq 'Complete final Existing checks' -and $continuedJob.status -ceq 'completed' -and
            $continuedJob.conclusion -ceq 'failure') 'The retained nine-pass continuation run/job identity or preserved failure changed.'
        $continuedStep = @($continuedJob.steps | Where-Object name -CEQ 'Complete all ten remaining Existing stages')
        Require ($continuedStep.Count -eq 1 -and $continuedStep[0].status -ceq 'completed' -and
            $continuedStep[0].conclusion -ceq 'failure') 'The actual ten-stage continuation step must retain its reviewed failure.'
        $continuedArtifact = Receive-FinalArtifact $continuationArtifactId 'final-existing-remainder-evidence' $continuationArchiveHash (Join-Path $existingEvidence 'prior-continuation-artifact') -ExpectedRunId $continuationRunId -ExpectedSource $continuationSource
        $continuedReports = @(Get-ChildItem -LiteralPath $continuedArtifact.root -Filter report.json -File -Recurse)
        Require ($continuedReports.Count -eq 1) 'The retained continuation report is missing or ambiguous.'
        $continuedReport = Read-BoundedJson $continuedReports[0].FullName
        Require ($continuedReport.suite -ceq 'ExistingContinuation' -and $continuedReport.sourceRevision -ceq $revision -and
            $continuedReport.candidateSha256 -ceq $candidateHash -and $continuedReport.verificationRevision -ceq $continuationSource -and
            $continuedReport.runId -eq $continuationRunId -and $continuedReport.runAttempt -eq 1 -and
            $continuedReport.retainedRunId -eq $runId -and $continuedReport.retainedJobId -eq 119512118598 -and
            $continuedReport.retainedPassedChecks -eq 17 -and $continuedReport.expectedChecks -eq 10 -and
            $continuedReport.nativeNotificationVerified -eq $false) 'Retained continuation candidate, source, run or scope differs from the reviewed facts.'
        $continuedNames = @('Shared Worlds packaged journey', 'Staging LiveSave owner action', 'Shared LiveSave packaged transfer journey',
            'TogetherServer.CompanionChecks', 'Solution formatting', 'Packaged served smoke',
            'Production plus staging isolation smoke', 'Packaged hidden desktop smoke',
            'Packaged interactive desktop smoke', 'Update handoff smoke')
        $continuedChecks = @($continuedReport.checks)
        Require ($continuedChecks.Count -eq 10 -and @($continuedChecks.name | Select-Object -Unique).Count -eq 10 -and
            @($continuedChecks | Where-Object outcome -CEQ 'passed').Count -eq 9 -and
            @($continuedChecks | Where-Object outcome -CEQ 'failed').Count -eq 1) 'The retained continuation must contain exactly nine passes and one default-Companion failure.'
        for ($index = 0; $index -lt 10; $index++) {
            $expectedOutcome = if ($index -eq 3) { 'failed' } else { 'passed' }
            Require ($continuedChecks[$index].name -ceq $continuedNames[$index] -and
                $continuedChecks[$index].outcome -ceq $expectedOutcome) 'A retained continuation outcome or exact stage order changed.'
        }
        Copy-Item -LiteralPath $continuedReports[0].FullName -Destination (Join-Path $existingEvidence 'prior-continuation-report.json')
        $context.priorContinuationRevision = $continuationSource
        $context.priorContinuationRunId = $continuationRunId
        $context.priorContinuationRunAttempt = 1
        $context.priorContinuationJobId = $continuationJobId
        $context.priorContinuationJobConclusion = 'failure'
        $context.priorContinuationArtifactId = $continuationArtifactId
        $context.priorContinuationArtifactZipSha256 = $continuationArchiveHash
        $context.priorContinuationReportSha256 = (Get-FileHash -LiteralPath $continuedReports[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $context.priorContinuationPassedChecks = 9
        $context.priorContinuationFailedCheck = 'TogetherServer.CompanionChecks'
        $context.retainedCompletedChecks = 26
        $context.remainingCheck = 'TogetherServer.CompanionChecks'
        $context.boundary = 'Exact seventeen original passes and nine continuation passes retained; both historical jobs remain failed. The current default CompanionChecks stage must still pass. HTTP/download/static proof only; no full native, physical-PC, WAN or real-game acceptance is claimed.'
        $context.result = 'Seventeen original completed passes and nine later continuation passes retained. The original Shared Worlds failure and later default CompanionChecks failure remain recorded; default CompanionChecks still requires its focused pass. Full acceptance is not claimed.'
        $context | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $existingEvidence 'context.json') -Encoding utf8
        $context | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $evidence 'context.json') -Encoding utf8
        Write-Host 'RETAINED PARTIAL Existing evidence: 17 original passes and 9 continuation passes; both historical failures remain recorded. Default CompanionChecks still requires a successful focused check.'
    }
    Write-Host "PASS static final candidate identity $candidateHash; twelve unaffected successful jobs and completed Desktop/pairing failures retained. Existing proof pending: $($context.priorExistingProofPending). No full acceptance claim."
}
finally {
    $headers.Clear()
    Pop-Location
}
