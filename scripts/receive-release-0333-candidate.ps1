# Exact release-only retained report proof; never launches a process or check.
param([Parameter(Mandatory=$true)][ValidateSet('Existing', 'Pairing')][string]$Suite)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'receive-release-0333-ui-candidate.ps1') -IncludeFixtures
$contextRoot = Join-Path $repository 'local-data/ci-evidence/0333-continuation'
Require (!(Test-Path -LiteralPath $contextRoot)) 'Continuation context requires fresh output.'
New-Item -ItemType Directory -Path $contextRoot | Out-Null
Copy-Item -LiteralPath (Join-Path $repository 'local-data/ui-redesign-visual-review/retained-ui-context.json') -Destination (Join-Path $contextRoot 'candidate-ui-context.json')
$headers = @{ Authorization = "Bearer $env:GH_TOKEN"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }
try {
    $retainedId = if ($Suite -ceq 'Existing') { 12444567355L } else { 12444452782L }
    $retainedName = if ($Suite -ceq 'Existing') { 'qol-windows-evidence-Existing' } else { 'local-pairing-evidence' }
    $retainedBytes = if ($Suite -ceq 'Existing') { 827L } else { 1141530L }
    $retainedHash = if ($Suite -ceq 'Existing') { '3419140cce2eab536b4cdc0bdd4d1ed579b36cd3238fcd81566dab12f73979b7' } else { '9b47b397b506ae6dde0b2cdeac9bd0b2009d6c91c060d3db26942f10ffefdcd6' }
    $retainedJobId = if ($Suite -ceq 'Existing') { 119968652593L } else { 119968652866L }
    $retainedJobName = if ($Suite -ceq 'Existing') { 'Verify Existing' } else { 'Verify local pairing and both-ended UX' }
    $job = Get-ReviewJson "actions/jobs/$retainedJobId"
    Require ($job.id -eq $retainedJobId -and $job.run_id -eq $candidateRunId -and $job.head_sha -ceq $sourceRevision -and
        $job.run_attempt -eq 1 -and $job.name -ceq $retainedJobName -and $job.status -ceq 'completed' -and
        $job.conclusion -ceq 'failure') 'The original failed job identity must remain preserved.'
    $artifact = Get-ReviewJson "actions/artifacts/$retainedId"
    Require ($artifact.id -eq $retainedId -and $artifact.name -ceq $retainedName -and $artifact.expired -eq $false -and
        $artifact.workflow_run.id -eq $candidateRunId -and $artifact.workflow_run.head_sha -ceq $sourceRevision -and
        $artifact.workflow_run.repository_id -eq 1379836840 -and $artifact.workflow_run.head_repository_id -eq 1379836840 -and
        $artifact.size_in_bytes -eq $retainedBytes -and $artifact.digest -ceq "sha256:$retainedHash") 'The retained report artifact differs.'
    $zip = Join-Path $env:RUNNER_TEMP "release-0333-retained-$retainedId.zip"
    Require (!(Test-Path -LiteralPath $zip)) 'Refusing to replace a retained report ZIP.'
    Invoke-WebRequest -Uri "$api/actions/artifacts/$retainedId/zip" -Headers $headers -OutFile $zip -TimeoutSec 120
    Require ((Get-Item -LiteralPath $zip).Length -eq $retainedBytes -and
        (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $retainedHash) 'The retained report ZIP bytes differ.'
    $retainedRoot = Join-Path $repository ("local-data/ci-evidence/0333-retained-$($Suite.ToLowerInvariant())")
    Expand-ReviewArchive $zip $retainedRoot
    if ($Suite -ceq 'Existing') {
        $report = Get-Content -LiteralPath (Join-Path $retainedRoot 'release-gate/release-gate-report.json') -Raw | ConvertFrom-Json
        $expected = @('Git whitespace check', 'Desktop diagnostic isolation', 'Check runner listener identity', 'UI lint', 'UI unit tests',
            'Locked application restore', 'TogetherServer.Checks', 'TogetherServer.ValheimChecks', 'TogetherServer.FactorioChecks',
            'TogetherServer.TerrariaChecks', 'TogetherServer.MinecraftChecks', 'TogetherServer.MinecraftSetupChecks')
        Require ($report.suite -ceq 'Existing' -and $report.candidate.sourceRevision -ceq $sourceRevision -and
            $report.candidate.verificationRevision -ceq $sourceRevision -and $report.candidate.sha256.ToLowerInvariant() -ceq $candidateHash -and
            $report.candidate.productVersion -ceq "0.3.3.3+$sourceRevision" -and @($report.checks).Count -eq 13) 'Retained Existing report identity differs.'
        for ($index = 0; $index -lt $expected.Count; $index++) {
            Require ($report.checks[$index].name -ceq $expected[$index] -and $report.checks[$index].outcome -ceq 'passed') 'A retained Existing pass differs.'
        }
        Require ($report.checks[12].name -ceq 'TogetherServer.CustomChecks' -and $report.checks[12].outcome -ceq 'failed') 'The original Custom failure must remain failed.'
        $retainedPasses = 12
    } else {
        $identity = Get-Content -LiteralPath (Join-Path $retainedRoot 'candidate.json') -Raw | ConvertFrom-Json
        Require ($identity.sourceRevision -ceq $sourceRevision -and $identity.candidateSha256 -ceq $candidateHash -and
            $identity.finalCandidateSha256 -ceq $candidateHash -and $identity.productVersion -ceq "0.3.3.3+$sourceRevision" -and
            $identity.workflowRunId -eq $candidateRunId -and $identity.workflowRunAttempt -eq 1 -and $identity.job -ceq 'local-pairing') 'Retained pairing candidate differs.'
        $report = Get-Content -LiteralPath (Join-Path $retainedRoot 'lan-api/report.json') -Raw | ConvertFrom-Json
        $expected = @('isolated-apps', 'pinned-pairing', 'separate-permissions-and-consent', 'signed-chat', 'synthetic-stop-copy-receipts',
            'password-permission-revoke', 'unsupported-password-source-denied', 'guided-hosting', 'local-copy-hosting', 'lost-response-cleanup',
            'cleanup', 'new-host-notice', 'new-host-authenticated-connect', 'new-host-permission-isolation', 'new-host-stop-and-transfer',
            'new-host-restart-reconnect', 'new-host-revocation-isolation', 'new-host-receipt-confirmed')
        Require ($report.schema -eq 1 -and $report.mode -ceq 'SamePcLanApi' -and $report.physicalPcCount -eq 1 -and
            $report.outcome -ceq 'Passed' -and @($report.stages).Count -eq 18 -and
            @($report.stages | Where-Object state -CNE 'Passed').Count -eq 0 -and
            @($report.stages | Where-Object id -cnotin $expected).Count -eq 0 -and
            @($report.stages.id | Select-Object -Unique).Count -eq 18 -and $report.cleanup.jobActiveProcesses -eq 0 -and
            $report.windowObservation.state -ceq 'Passed' -and $report.windowObservation.samples -gt 0 -and
            $report.windowObservation.peakVisibleOwnedWindows -eq 0) 'The exact eighteen retained LAN stages differ.'
        foreach ($field in @('discoveredHost', 'connectedOwner', 'connectedObserver', 'transferredCandidateCopy', 'restartedHostConnections', 'isolatedRevocation')) {
            Require ($report.apiHostChange.$field -eq $true) 'A retained LAN hosting or isolation proof differs.'
        }
        $priorUx = Get-Content -LiteralPath (Join-Path $retainedRoot 'ux/report.json') -Raw | ConvertFrom-Json
        Require ($priorUx.outcome -ceq 'Failed' -and $priorUx.failedStage -ceq 'two-ended-ux' -and
            $priorUx.failureCode -ceq 'TwoEndedUxFailed') 'The original UX failure must remain preserved.'
        $retainedPasses = 18
    }
    [ordered]@{ suite = $Suite; sourceRevision = $sourceRevision; verificationRevision = $checkRevision;
        candidateRunId = $candidateRunId; candidateJobId = $candidateJobId; candidateArtifactId = $artifactId;
        candidateSha256 = $candidateHash; productVersion = "0.3.3.3+$sourceRevision";
        originalJobId = $retainedJobId; originalJobConclusion = 'failure'; candidateRunAttempt = 1;
        retainedArtifactId = $retainedId; retainedArtifactZipBytes = $retainedBytes; retainedArtifactZipSha256 = $retainedHash;
        retainedPassedChecks = $retainedPasses; originalFailurePreserved = $true;
        verificationRunId = $env:GITHUB_RUN_ID; verificationRunAttempt = $env:GITHUB_RUN_ATTEMPT;
        fullyAccepted = $false; nativeNotificationVerified = $false; quietTimeLimitationRetained = $true;
        boundary = 'Retained source/synthetic passes and fixed remaining hosted checks only. Original failures and native QuietTime limitation remain recorded; no physical second-PC, WAN or real-game acceptance.' } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $contextRoot 'context.json') -Encoding utf8
    Write-Host "PASS immutable candidate, seven UI assets and $retainedPasses retained $Suite checks; original failure preserved."
} finally { $headers.Clear() }
