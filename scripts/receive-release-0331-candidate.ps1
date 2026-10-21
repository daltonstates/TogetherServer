# This release-only continuation reads/downloads evidence; it never launches the app.
param()
$ErrorActionPreference = 'Stop'
$revision = 'd6ed7b48e32f23e00d86947133da08c8413d8f4f'
$candidateHash = 'e021acfe90adda04ad42885475b8a05bb193440058076627eab23802c4fafec1'
$archiveHash = '4bf29ea3be0a2eaf3bed3db31eddf82853376538b77c85ee947db575954fc276'
if (!$env:GH_TOKEN) { throw 'A read-only Actions token is required.' }
git merge-base --is-ancestor $revision HEAD
if ($LASTEXITCODE -ne 0) { throw 'Verification source does not descend from the candidate.' }
$changed = @(git diff --no-renames --name-only $revision HEAD)
if ($LASTEXITCODE -ne 0) { throw 'Could not compare verification inputs.' }
$allowed = '^(docs/|checks/TogetherServer\.Checks/(Program|SharedSaveReceiptRetryChecks)\.cs$|checks/TogetherServer\.CompanionChecks/(LocalPairingLabJourney|SharedWorldJourney)\.cs$|checks/update-handoff-smoke\.ps1$|ui/src/ServerFilesPanel\.test\.tsx$|scripts/(verify-release|receive-release-0331-candidate|verify-release-0331-prior)\.ps1$|\.github/workflows/release-0331-continuation\.yml$)'
if (@($changed | Where-Object { $_ -notmatch $allowed }).Count) { throw 'Product or unrelated inputs changed after candidate compilation.' }
$evidence = New-Item -ItemType Directory -Path local-data/ci-evidence/continuation -Force
$headers = @{Authorization="Bearer $env:GH_TOKEN";Accept='application/vnd.github+json';'X-GitHub-Api-Version'='2022-11-28'}
$api = 'https://api.github.com/repos/daltonstates/TogetherServer'
try {
    $run = Invoke-RestMethod -Uri "$api/actions/runs/39669799182" -Headers $headers
    $response = Invoke-RestMethod -Uri "$api/actions/runs/39669799182/jobs?filter=all&per_page=100" -Headers $headers
    if ($run.head_sha -ne $revision -or $run.status -ne 'completed' -or $run.conclusion -ne 'failure' -or
        $run.run_attempt -ne 1 -or $run.workflow_id -ne 367811679 -or $run.path -ne '.github/workflows/windows-ci.yml' -or
        $response.total_count -ne 15 -or @($response.jobs).Count -ne 15) { throw 'Initial workflow identity changed.' }
    $expectedJobs = @(
        @(119464350297,'candidate','success'),
        @(119465829364,'Verify automatic shared hosting','success'),
        @(119465829448,'Verify 0.3.2 compatibility','success'),
        @(119465829457,'Verify QolApi','success'),
        @(119465829459,'Verify Rehearsal','success'),
        @(119465829562,'Verify 0.3.0 compatibility','success'),
        @(119465829615,'Verify 0.3.2.1 compatibility','success'),
        @(119465829626,'Verify Browser','success'),
        @(119465829697,'Verify 0.3.1 compatibility','success'),
        @(119465829836,'Verify 0.3.2.2 compatibility','success'),
        @(119465829361,'Verify Desktop','failure'),
        @(119465829385,'Verify local pairing and both-ended UX','failure'),
        @(119465829389,'Verify Existing','failure'),
        @(119465829473,'Verify 0.3.3 compatibility','failure'),
        @(119470215003,'Package verified release','skipped')
    )
    foreach ($expected in $expectedJobs) {
        $job = @($response.jobs | Where-Object id -eq $expected[0])
        if ($job.Count -ne 1 -or $job[0].name -ne $expected[1] -or $job[0].status -ne 'completed' -or
            $job[0].conclusion -ne $expected[2] -or $job[0].head_sha -ne $revision -or $job[0].run_attempt -ne 1) {
            throw "Initial job evidence mismatch: $($expected[1])."
        }
    }
    foreach ($item in @(
        @{id=12379945602;name='windows-candidate';hash=$archiveHash;destination='.'},
        @{id=12379591472;name='qol-windows-evidence-Desktop';hash='eb6cef28fed144aceaa9aba6cf949633776c8415f840dd532e7a6924353c06d2';destination='local-data/prior-desktop-0331'}
    )) {
        $artifact = Invoke-RestMethod -Uri "$api/actions/artifacts/$($item.id)" -Headers $headers
        if ($artifact.id -ne $item.id -or $artifact.name -ne $item.name -or $artifact.expired -ne $false -or
            $artifact.workflow_run.id -ne 39669799182 -or $artifact.workflow_run.head_sha -ne $revision -or
            $artifact.digest -ne "sha256:$($item.hash)") { throw 'Artifact provenance mismatch.' }
        $zip = Join-Path $env:RUNNER_TEMP "$($item.id).zip"
        Invoke-WebRequest -Uri "$api/actions/artifacts/$($item.id)/zip" -Headers $headers -OutFile $zip
        if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant() -ne $item.hash) { throw 'Artifact ZIP digest mismatch.' }
        Expand-Archive -LiteralPath $zip -DestinationPath $item.destination -Force
    }
} finally { $headers.Clear() }
$candidate = (Resolve-Path local-data/release-candidate/TogetherServer.exe).Path
$identity = Get-Content local-data/release-candidate/candidate.json -Raw | ConvertFrom-Json
if ($identity.source -ne $revision -or $identity.runId -ne '39669799182' -or $identity.sha256 -ne $candidateHash -or
    (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash.ToLowerInvariant() -ne $candidateHash -or
    (Get-Item -LiteralPath $candidate).VersionInfo.ProductVersion -ne "0.3.3.1+$revision" -or
    (Get-Item -LiteralPath $candidate).VersionInfo.FileVersion -ne '0.3.3.1' -or
    (Get-AuthenticodeSignature -LiteralPath $candidate).Status -ne 'NotSigned') { throw 'Immutable candidate identity mismatch.' }
$summaries = @(Get-ChildItem local-data/prior-desktop-0331/qol-desktop -Filter summary.json -File -Recurse)
if ($summaries.Count -ne 1) { throw 'Ambiguous prior native summary.' }
$native = Get-Content -LiteralPath $summaries[0].FullName -Raw | ConvertFrom-Json
# Preserve the failure. Notification delivery/click was blocked by OS QuietTime.
if ($native.success -ne $false -or $native.result -ne 'BLOCKED' -or $native.requiredCaseCount -ne 13 -or
    $native.requiredCasesPassed -ne 12 -or $native.requiredCasesBlocked -ne 1 -or $native.remainingNativeCasesCompleted -ne $true -or
    $native.cleanupCompleted -ne $true) {
    throw 'Prior native limitation differs from the reviewed evidence.'
}
Copy-Item -LiteralPath $summaries[0].FullName -Destination (Join-Path $evidence 'prior-native-summary.json')
@{sourceRevision=$revision;candidateSha256=$candidateHash;candidateArtifactId=12379945602;candidateArtifactZipSha256=$archiveHash;
  initialRunId=39669799182;initialRunAttempt=1;verificationRevision=$env:GITHUB_SHA;continuationRunId=$env:GITHUB_RUN_ID;
  continuationRunAttempt=$env:GITHUB_RUN_ATTEMPT;job=$env:GITHUB_JOB;allowedSourceChanges=$changed;
  priorJobs=@($response.jobs | Select-Object id,name,head_sha,run_attempt,status,conclusion);
  fullyAccepted=$false;nativeNotificationVerified=$false;
  boundary='Explicit dedicated hosted Windows opt-in only; synthetic data; no physical second-PC, WAN or game acceptance.'} |
  ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidence 'context.json') -Encoding utf8
Write-Host "PASS retained candidate $candidateHash with separate verification source $env:GITHUB_SHA."
