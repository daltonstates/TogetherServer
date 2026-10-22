# Release-specific HTTP/hash proof only. This script never launches the app.
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$repository = Split-Path -Parent $PSScriptRoot
$revision = '72bb9067627c866a74eb26b44e4bec61c4c15279'
$candidateHash = '1b154b45438f86951c761080c2efcd9f9db259f4a622ce26fcdb7ba31db5c29d'
$originalRunId = 39796156022L
if (!$env:GH_TOKEN) { throw 'Hosted artifact-read authentication is required.' }
git merge-base --is-ancestor $revision HEAD
if ($LASTEXITCODE -ne 0) { throw 'The immutable candidate must be an ancestor of the check source.' }
$changed = @(git diff --no-renames --name-only $revision HEAD)
if ($LASTEXITCODE -ne 0 -or @($changed | Where-Object {
    $_ -notmatch '^(docs/|checks/TogetherServer\.CompanionChecks/SharedWorldLiveTransferJourney\.cs$|scripts/receive-release-0332-candidate\.ps1$|\.github/workflows/release-0332-continuation\.yml$)'
}).Count) { throw 'Only the reviewed check, receiver, workflow and documentation may differ from the candidate.' }
$api = 'https://api.github.com/repos/daltonstates/TogetherServer'
$headers = @{Authorization = "Bearer $env:GH_TOKEN"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28'}
function Receive-PinnedArtifact([long]$Id, [string]$Name, [string]$Hash, [long]$Bytes, [string]$Destination,
    [long]$RunId = $originalRunId, [string]$SourceRevision = $revision) {
    $artifact = Invoke-RestMethod -Uri "$api/actions/artifacts/$Id" -Headers $headers
    if ($artifact.id -ne $Id -or $artifact.name -cne $Name -or $artifact.expired -or
        $artifact.workflow_run.id -ne $RunId -or $artifact.workflow_run.head_sha -cne $SourceRevision -or
        $artifact.digest -cne "sha256:$Hash" -or $artifact.size_in_bytes -ne $Bytes) { throw 'Pinned artifact provenance differs.' }
    $zip = Join-Path $env:RUNNER_TEMP "release-0332-$Id.zip"
    Invoke-WebRequest -Uri "$api/actions/artifacts/$Id/zip" -Headers $headers -OutFile $zip
    if ((Get-Item -LiteralPath $zip).Length -ne $Bytes -or
        (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant() -cne $Hash) { throw 'Pinned archive bytes differ.' }
    Expand-Archive -LiteralPath $zip -DestinationPath $Destination -Force
}
try {
    $run = Invoke-RestMethod -Uri "$api/actions/runs/$originalRunId" -Headers $headers
    if ($run.head_sha -cne $revision -or $run.head_branch -cne 'main' -or $run.run_attempt -ne 1 -or
        $run.path -cne '.github/workflows/windows-ci.yml') { throw 'Original main workflow identity differs.' }
    Receive-PinnedArtifact 12432274957 'windows-candidate' '3dbe28bb09706dba50f1f2dbf51b5ed0ade6de695fd6a37aca634986fbfbcccc' 61004381 $repository
    $retainedRoot = Join-Path $repository 'local-data/ci-evidence/0332-retained-existing'
    Receive-PinnedArtifact 12434748149 'qol-windows-evidence-Existing' '1bd42ab29254405505a66dcf2197546634ea964e3dd45d8ff72afcb633848528' 996 $retainedRoot
    $continuedRoot = Join-Path $repository 'local-data/ci-evidence/0332-prior-continuation'
    Receive-PinnedArtifact 12436496114 'release-0332-existing-continuation' '7f714759931282b647eaee7555ac360d0ff73866482b34b00146595bee13d5ff' 2337 $continuedRoot 39800835312 '2993d72b21b2f43ab1ef326b5a3210d08aa2dede'
} finally { $headers.Clear() }
$candidate = Join-Path $repository 'local-data/release-candidate/TogetherServer.exe'
$identity = Get-Content -LiteralPath (Join-Path $repository 'local-data/release-candidate/candidate.json') -Raw | ConvertFrom-Json
$info = (Get-Item -LiteralPath $candidate).VersionInfo
if ($identity.source -cne $revision -or $identity.runId -ne "$originalRunId" -or $identity.runAttempt -ne '1' -or
    $identity.job -cne 'candidate' -or $identity.version -cne '0.3.3.2' -or $identity.sha256 -cne $candidateHash -or
    $info.ProductVersion -cne "0.3.3.2+$revision" -or [Version]$info.FileVersion -ne [Version]'0.3.3.2' -or
    (Get-Item -LiteralPath $candidate).Length -ne 66133345 -or
    (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash.ToLowerInvariant() -cne $candidateHash -or
    [string](Get-AuthenticodeSignature -LiteralPath $candidate).Status -cne 'NotSigned') { throw 'Immutable candidate identity differs.' }
$report = Get-Content -LiteralPath (Join-Path $retainedRoot 'release-gate/release-gate-report.json') -Raw | ConvertFrom-Json
$expected = @('Git whitespace check', 'Desktop diagnostic isolation', 'Check runner listener identity',
    'UI lint', 'UI unit tests', 'Locked application restore', 'TogetherServer.Checks',
    'TogetherServer.ValheimChecks', 'TogetherServer.FactorioChecks', 'TogetherServer.TerrariaChecks',
    'TogetherServer.MinecraftChecks', 'TogetherServer.MinecraftSetupChecks', 'TogetherServer.CustomChecks',
    'TogetherServer.SharedHistoryChecks', 'TogetherServer.UpdateChecks', 'Core remote journey',
    'World-load packaged rehearsal', 'Shared Worlds packaged journey', 'Staging LiveSave owner action')
if ($report.suite -cne 'Existing' -or $report.candidate.sourceRevision -cne $revision -or
    $report.candidate.verificationRevision -cne $revision -or $report.candidate.sha256.ToLowerInvariant() -cne $candidateHash -or
    $report.candidate.productVersion -cne "0.3.3.2+$revision" -or @($report.checks).Count -ne 20) { throw 'Retained Existing report identity differs.' }
for ($index = 0; $index -lt $expected.Count; $index++) {
    if ($report.checks[$index].name -cne $expected[$index] -or $report.checks[$index].outcome -cne 'passed') { throw 'Retained passed stage differs.' }
}
if ($report.checks[19].name -cne 'Shared LiveSave packaged transfer journey' -or
    $report.checks[19].outcome -cne 'failed') { throw 'The original failed tamper stage must remain recorded.' }
$continued = Get-Content -LiteralPath (Join-Path $continuedRoot '0332-continuation/report.json') -Raw | ConvertFrom-Json
$continuedExpected = @('Shared LiveSave packaged transfer journey', 'TogetherServer.CompanionChecks',
    'Solution formatting', 'Packaged served smoke', 'Production plus staging isolation smoke',
    'Packaged hidden desktop smoke', 'Packaged interactive desktop smoke', 'Update handoff smoke')
if ($continued.suite -cne 'Existing0332Continuation' -or $continued.sourceRevision -cne $revision -or
    $continued.candidateSha256 -cne $candidateHash -or $continued.runId -ne '39800835312' -or $continued.runAttempt -ne '1' -or
    $continued.verificationRevision -cne '2993d72b21b2f43ab1ef326b5a3210d08aa2dede' -or
    $continued.retainedPassedChecks -ne 19 -or $continued.expectedChecks -ne 8 -or @($continued.checks).Count -ne 8 -or
    $continued.originalRunId -ne $originalRunId -or $continued.originalExistingJobId -ne 119885027181L -or
    $continued.fullyAccepted -ne $false -or $continued.nativeNotificationVerified -ne $false -or
    $continued.originalNativeFailurePreserved -ne $true) { throw 'Prior continuation identity differs.' }
for ($index = 0; $index -lt $continuedExpected.Count; $index++) {
    $outcome = if ($index -eq 0) { 'failed' } else { 'passed' }
    if ($continued.checks[$index].name -cne $continuedExpected[$index] -or
        $continued.checks[$index].outcome -cne $outcome) { throw 'Prior continuation outcomes differ.' }
}
$contextRoot = New-Item -ItemType Directory -Path (Join-Path $repository 'local-data/ci-evidence/0332-continuation') -Force
@{sourceRevision = $revision; candidateSha256 = $candidateHash; originalRunId = $originalRunId;
    originalExistingJobId = 119885027181L; retainedPassedChecks = 19; retainedContinuationPassedChecks = 7;
    priorContinuationRunId = 39800835312L; priorContinuationJobId = 119900719881L;
    priorContinuationSource = '2993d72b21b2f43ab1ef326b5a3210d08aa2dede'; priorContinuationArtifactId = 12436496114L;
    originalFailedCheck = $report.checks[19].name;
    verificationRevision = $env:GITHUB_SHA; verificationRunId = $env:GITHUB_RUN_ID; verificationRunAttempt = $env:GITHUB_RUN_ATTEMPT;
    candidateArtifactId = 12432274957L; existingArtifactId = 12434748149L; fullyAccepted = $false; nativeNotificationVerified = $false} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $contextRoot.FullName 'context.json') -Encoding utf8
Write-Output 'PASS pinned unchanged 0.3.3.2 candidate and all 26 retained Existing stages; original failures remain failures.'
