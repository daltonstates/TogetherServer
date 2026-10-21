# Release-only HTTP/download/hash proof. This script never launches an app or test.
param([Parameter(Mandatory)][ValidateSet('Existing', 'Package')][string]$Scope)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$proofSource = '342b9393716cdc847e724ce012e6508788454402'
$candidateSource = 'd6ed7b48e32f23e00d86947133da08c8413d8f4f'
$candidateHash = 'e021acfe90adda04ad42885475b8a05bb193440058076627eab23802c4fafec1'
$installerHash = '541754625185c4247a409b86d69c85bf06bb610354aabf73284af4f8b9beb644'
$priorRunId = 39677264616L
$api = 'https://api.github.com/repos/daltonstates/TogetherServer'
$headers = @{}

function Require([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
}

function Get-PriorJson([string]$ApiPath, [int]$TimeoutSeconds = 30) {
    Invoke-RestMethod -Uri "$api/$ApiPath" -Headers $headers -TimeoutSec $TimeoutSeconds
}

function Confirm-JobIdentity($Job, [long]$Id, [string]$Name) {
    Require ($Job.id -eq $Id -and $Job.run_id -eq $priorRunId -and $Job.run_attempt -eq 1 -and
        $Job.head_sha -ceq $proofSource -and $Job.name -ceq $Name) 'Pinned prior job identity mismatch.'
}

function Read-BoundedJson([string]$Path, [long]$Limit = 2097152) {
    $file = Get-Item -LiteralPath $Path
    Require (!$file.PSIsContainer -and $file.Length -gt 0 -and $file.Length -le $Limit -and
        ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'The retained JSON report is missing, linked or oversized.'
    Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
}

function Receive-VerifiedArtifact([long]$Id, [string]$Name, [string]$ExpectedHash,
    [string]$Destination, [string[]]$ExpectedFiles = @()) {
    $artifact = Get-PriorJson "actions/artifacts/$Id"
    Require ($artifact.id -eq $Id -and $artifact.name -ceq $Name -and $artifact.expired -eq $false -and
        $artifact.workflow_run.id -eq $priorRunId -and $artifact.workflow_run.head_sha -ceq $proofSource -and
        $artifact.digest -cmatch '^sha256:[0-9a-f]{64}$' -and
        $artifact.size_in_bytes -gt 0 -and $artifact.size_in_bytes -le 536870912) 'Pinned prior artifact identity mismatch.'
    $digest = [string]$artifact.digest
    $hash = $digest.Substring(7)
    if ($ExpectedHash) { Require ($hash -ceq $ExpectedHash) 'Pinned prior artifact digest changed.' }
    $zip = Join-Path $env:RUNNER_TEMP "release-0331-prior-$Id.zip"
    Invoke-WebRequest -Uri "$api/actions/artifacts/$Id/zip" -Headers $headers -OutFile $zip -TimeoutSec 120
    $zipFile = Get-Item -LiteralPath $zip
    Require ($zipFile.Length -gt 0 -and $zipFile.Length -le 536870912 -and
        (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $hash) 'Prior artifact ZIP digest mismatch.'
    Require (!(Test-Path -LiteralPath $Destination)) 'Refusing to overwrite an existing retained artifact directory.'
    $destinationRoot = [IO.Path]::GetFullPath($Destination).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $files = @($archive.Entries | Where-Object { !$_.FullName.EndsWith('/') })
        Require ($files.Count -gt 0 -and $files.Count -le 2048) 'Prior artifact entry count exceeds its fixed bound.'
        [long]$expandedBytes = 0
        foreach ($entry in $files) {
            Require (!$entry.FullName.Contains('\') -and ![IO.Path]::IsPathRooted($entry.FullName)) 'Prior artifact contains an unexpected path.'
            $target = [IO.Path]::GetFullPath((Join-Path $Destination $entry.FullName))
            Require ($target.StartsWith($destinationRoot, [StringComparison]::OrdinalIgnoreCase)) 'Prior artifact escaped its retained directory.'
            $expandedBytes += $entry.Length
            Require ($expandedBytes -le 536870912) 'Prior artifact expanded bytes exceed the fixed bound.'
        }
        if ($ExpectedFiles.Count) {
            $names = @($files | ForEach-Object FullName)
            Require ($names.Count -eq $ExpectedFiles.Count -and
                @($names | Select-Object -Unique).Count -eq $ExpectedFiles.Count -and
                @($names | Where-Object { $_ -cnotin $ExpectedFiles }).Count -eq 0) 'The retained release asset inventory changed.'
        }
    }
    finally { $archive.Dispose() }
    Expand-Archive -LiteralPath $zip -DestinationPath $Destination
    [pscustomobject]@{ id = $Id; name = $Name; zipSha256 = $hash; zipBytes = $zipFile.Length;
        root = [IO.Path]::GetFullPath($Destination) }
}

Push-Location $repository
try {
    Require (![string]::IsNullOrWhiteSpace($env:GH_TOKEN)) 'A read-only Actions token is required.'
    Require (![string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) 'The dedicated runner temporary directory is required.'
    $currentSource = (git rev-parse HEAD).Trim()
    Require ($LASTEXITCODE -eq 0 -and $currentSource -cmatch '^[0-9a-f]{40}$') 'Current verification source is unavailable.'
    git merge-base --is-ancestor $proofSource HEAD
    Require ($LASTEXITCODE -eq 0) 'Retained verification source must be an ancestor of the current source.'
    $changed = @(git diff --no-renames --name-only $proofSource HEAD)
    Require ($LASTEXITCODE -eq 0) 'Could not compare retained verification source inputs.'
    $allowed = '^(docs/|checks/TogetherServer\.CompanionChecks/(LocalPairingLabJourney|SharedWorldJourney)\.cs$|\.github/workflows/release-0331-continuation\.yml$|scripts/(receive-release-0331-candidate|verify-release-0331-prior)\.ps1$)'
    Require (@($changed | Where-Object { $_ -notmatch $allowed }).Count -eq 0) 'Existing, installer, product or dependency inputs changed after retained verification.'
    $evidence = Join-Path $repository ('local-data/ci-evidence/retained-' + $Scope.ToLowerInvariant())
    New-Item -ItemType Directory -Path $evidence -Force | Out-Null
    $headers = @{ Authorization = "Bearer $env:GH_TOKEN"; Accept = 'application/vnd.github+json'; 'X-GitHub-Api-Version' = '2022-11-28' }
    $run = Get-PriorJson "actions/runs/$priorRunId"
    Require ($run.id -eq $priorRunId -and $run.head_sha -ceq $proofSource -and $run.run_attempt -eq 1 -and
        $run.path -ceq '.github/workflows/release-0331-continuation.yml') 'Pinned prior workflow run identity mismatch.'
    $context = [ordered]@{ scope = $Scope; sourceRevision = $candidateSource; candidateSha256 = $candidateHash;
        verificationRevision = $proofSource; runId = $priorRunId; runAttempt = 1; currentVerificationRevision = $currentSource;
        currentRunId = $env:GITHUB_RUN_ID; currentRunAttempt = $env:GITHUB_RUN_ATTEMPT; currentJob = $env:GITHUB_JOB;
        allowedSourceChanges = $changed; fullyAccepted = $false; nativeNotificationVerified = $false;
        boundary = 'Retained exact hosted evidence only; no app, game, listener, browser, native or installer runtime is launched by this proof.' }

    if ($Scope -eq 'Existing') {
        $jobId = 119490251587L
        $job = Get-PriorJson "actions/jobs/$jobId"
        Confirm-JobIdentity $job $jobId 'Complete Existing acceptance'
        Require ($job.status -eq 'completed' -and $job.conclusion -eq 'failure') 'The reviewed partial Existing job must retain its completed failure.'
        $step = @($job.steps | Where-Object name -CEQ 'Complete the Existing suite with the immutable candidate')
        Require ($step.Count -eq 1 -and $step[0].conclusion -eq 'failure') 'The reviewed Existing acceptance step must retain its failure.'
        $artifact = Receive-VerifiedArtifact 12383645716 'continued-existing-evidence' '468c0d3103595d6fba2e8cc8190ea2839f5fa461dd0239e683fc5b1620cc7644' (Join-Path $evidence 'artifact-12383645716')
        $reports = @(Get-ChildItem -LiteralPath $artifact.root -Filter release-gate-report.json -File -Recurse)
        Require ($reports.Count -eq 1) 'The retained Existing report is missing or ambiguous.'
        $report = Read-BoundedJson $reports[0].FullName
        Require ($report.suite -ceq 'Existing' -and $report.candidate.sourceRevision -ceq $candidateSource -and
            $report.candidate.verificationRevision -ceq $proofSource -and $report.candidate.sha256 -ieq $candidateHash -and
            $report.candidate.productVersion -ceq "0.3.3.1+$candidateSource" -and $report.candidate.authenticode -eq 'NotSigned') 'Existing report candidate identity mismatch.'
        $expected = @('Git whitespace check', 'Desktop diagnostic isolation', 'Check runner listener identity',
            'UI lint', 'UI unit tests', 'Locked application restore', 'TogetherServer.Checks', 'TogetherServer.ValheimChecks',
            'TogetherServer.FactorioChecks', 'TogetherServer.TerrariaChecks', 'TogetherServer.MinecraftChecks',
            'TogetherServer.MinecraftSetupChecks', 'TogetherServer.CustomChecks', 'TogetherServer.SharedHistoryChecks',
            'TogetherServer.UpdateChecks', 'Core remote journey', 'World-load packaged rehearsal')
        $checks = @($report.checks)
        Require ($checks.Count -eq 18 -and @($checks.name | Select-Object -Unique).Count -eq 18 -and
            @($checks | Where-Object outcome -CEQ 'passed').Count -eq 17 -and
            @($checks | Where-Object outcome -CEQ 'failed').Count -eq 1) 'Partial Existing report inventory changed.'
        for ($index = 0; $index -lt $expected.Count; $index++) {
            Require ($checks[$index].name -ceq $expected[$index] -and $checks[$index].outcome -ceq 'passed') 'A retained completed Existing stage differs from the seventeen reviewed passes.'
        }
        Require ($checks[17].name -ceq 'Shared Worlds packaged journey' -and $checks[17].outcome -ceq 'failed') 'The reviewed Shared Worlds failure was omitted or changed.'
        Copy-Item -LiteralPath $reports[0].FullName -Destination (Join-Path $evidence 'release-gate-report.json')
        $context.jobId = $jobId
        $context.artifactId = $artifact.id
        $context.artifactZipSha256 = $artifact.zipSha256
        $context.reportSha256 = (Get-FileHash -LiteralPath $reports[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $context.jobConclusion = 'failure'
        $context.partialFact = $true
        $context.incomplete = $true
        $context.checkCount = 18
        $context.passedCheckCount = 17
        $context.failedCheckCount = 1
        $context.failedCheck = 'Shared Worlds packaged journey'
        $context.result = 'Only seventeen completed Existing passes are retained. Shared Worlds remains failed in the prior job; that stage and all nine later stages must pass in the continuation. Full Existing acceptance is incomplete.'
    }
    else {
        $jobId = 119490251553L
        $job = Get-PriorJson "actions/jobs/$jobId"
        Confirm-JobIdentity $job $jobId 'Package unchanged prerelease candidate'
        Require ($job.status -eq 'completed' -and $job.conclusion -eq 'success') 'Pinned installer package job did not pass.'
        $step = @($job.steps | Where-Object name -CEQ 'Build and test the installer around the verified bytes')
        Require ($step.Count -eq 1 -and $step[0].conclusion -eq 'success') 'Pinned installer compilation and acceptance step did not pass.'
        $assetsRoot = Join-Path $repository 'local-data/release-assets'
        $names = @('TogetherServer-win-x64.exe', 'TogetherServer-win-x64.exe.sha256',
            'TogetherServer-Setup-win-x64.exe', 'TogetherServer-Setup-win-x64.exe.sha256', 'release-manifest.json')
        $assetsArtifact = Receive-VerifiedArtifact 12382443933 'windows-release-assets' 'c147f6f381528aa7a3fa01e8913cc961c9f5d3a301696dcb30d20d61ed77c303' $assetsRoot $names
        $manifestPath = Join-Path $assetsRoot 'release-manifest.json'
        $manifestHash = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $manifest = Read-BoundedJson $manifestPath
        Require ($manifest.version -ceq '0.3.3.1' -and $manifest.sourceRevision -ceq $candidateSource -and
            $manifest.verificationRevision -ceq $proofSource -and $manifest.candidateSha256 -ceq $candidateHash -and
            $manifest.verificationRunId -eq $priorRunId -and $manifest.verificationRunAttempt -eq 1 -and
            $manifest.packageJob -ceq 'package' -and $manifest.fullyAccepted -eq $false -and $manifest.reviewOnly -eq $true -and
            $manifest.functionalGatesCheckedAtPackaging -eq $false -and $manifest.nativeNotificationVerified -eq $false) 'Retained package manifest identity or acceptance scope changed.'
        $expectedAssets = @(
            @{ name = 'TogetherServer-win-x64.exe'; sha256 = $candidateHash; bytes = 66131758L; productVersion = "0.3.3.1+$candidateSource" },
            @{ name = 'TogetherServer-Setup-win-x64.exe'; sha256 = $installerHash; bytes = 60590201L; productVersion = '0.3.3.1' }
        )
        Require (@($manifest.assets).Count -eq 2) 'The retained manifest asset count changed.'
        $verifiedAssets = @()
        foreach ($expectedAsset in $expectedAssets) {
            $path = Join-Path $assetsRoot $expectedAsset.name
            $file = Get-Item -LiteralPath $path
            $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
            $info = $file.VersionInfo
            $numericVersion = [Version]::new($info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart, $info.FilePrivatePart)
            $record = @($manifest.assets | Where-Object name -CEQ $expectedAsset.name)
            Require ($record.Count -eq 1 -and $record[0].sha256 -ceq $expectedAsset.sha256 -and
                $record[0].bytes -eq $expectedAsset.bytes -and $record[0].authenticode -eq 'NotSigned' -and
                $file.Length -eq $expectedAsset.bytes -and $hash -ceq $expectedAsset.sha256 -and
                $numericVersion -eq [Version]'0.3.3.1' -and
                $info.ProductVersion.Trim() -ceq $expectedAsset.productVersion -and
                (Get-AuthenticodeSignature -LiteralPath $path).Status -eq 'NotSigned') 'Retained release EXE identity mismatch.'
            $checksum = $path + '.sha256'
            Require ((Get-Item -LiteralPath $checksum).Length -le 256 -and
                (Get-Content -LiteralPath $checksum -Raw).Trim() -ceq "$hash  $($expectedAsset.name)") 'Retained adjacent checksum content mismatch.'
            $verifiedAssets += @{ name = $expectedAsset.name; sha256 = $hash; bytes = $file.Length;
                checksumSha256 = (Get-FileHash -LiteralPath $checksum -Algorithm SHA256).Hash.ToLowerInvariant(); authenticode = 'NotSigned' }
        }
        $wizardArtifact = Receive-VerifiedArtifact 12382304041 'installer-ui-evidence' '8aa4e89629841a8107264d0a5f5814e2853f287b309452758cbc9cd2f42f8222' (Join-Path $evidence 'installer-ui-artifact')
        $summaries = @(Get-ChildItem -LiteralPath $wizardArtifact.root -Filter summary.json -File -Recurse)
        Require ($summaries.Count -eq 1) 'The retained installer wizard summary is missing or ambiguous.'
        $wizard = Read-BoundedJson $summaries[0].FullName
        $cases = @($wizard.cases)
        Require ($wizard.passed -eq $true -and $wizard.sourceRevision -ceq $proofSource -and
            $wizard.candidateSha256 -ceq $candidateHash -and $wizard.installerSha256 -ceq $installerHash -and
            $wizard.candidateAndInstallerUnchanged -eq $true -and $wizard.skipped -eq 0 -and $wizard.cleanupCompleted -eq $true -and
            $cases.Count -eq 3 -and @($cases | Where-Object state -CNE 'Passed').Count -eq 0 -and
            @($cases.choice | Select-Object -Unique).Count -eq 3 -and
            @($cases | Where-Object { $_.choice -cnotin @('Host', 'Friend', 'DecideLater') }).Count -eq 0) 'Retained installer wizard did not pass all three exact choices and cleanup.'
        Require ((Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $manifestHash) 'The original retained release manifest changed during proof.'
        Copy-Item -LiteralPath $summaries[0].FullName -Destination (Join-Path $evidence 'installer-ui-summary.json')
        $context.jobId = $jobId
        $context.artifactId = $assetsArtifact.id
        $context.artifactZipSha256 = $assetsArtifact.zipSha256
        $context.installerUiArtifactId = $wizardArtifact.id
        $context.installerUiZipSha256 = $wizardArtifact.zipSha256
        $context.installerUiSummarySha256 = (Get-FileHash -LiteralPath $summaries[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        $context.originalManifestSha256 = $manifestHash
        $context.assets = $verifiedAssets
        $context.result = 'Exact previously packaged assets, silent installer acceptance and all three actual wizard choices retained without rebuilding or modifying assets.'
    }
    $context | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidence 'context.json') -Encoding utf8
    if ($Scope -eq 'Existing') {
        Write-Host "RETAINED PARTIAL Existing evidence: 17 passed, 1 failed; full acceptance remains incomplete. Run $priorRunId, job $jobId, candidate $candidateHash."
    }
    else { Write-Host "PASS retained Package proof from run $priorRunId, job $jobId, candidate $candidateHash." }
}
finally {
    $headers.Clear()
    Pop-Location
}
