param([switch]$AllowInteractiveTests)
$ErrorActionPreference = 'Stop'
if (!$AllowInteractiveTests -or $env:GITHUB_ACTIONS -ne 'true' -or
    $env:RUNNER_OS -ne 'Windows' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Frozen UI review requires explicit opt-in on a dedicated hosted Windows runner. Never run on the owner desktop.'
}
$repository = Split-Path -Parent $PSScriptRoot
Push-Location $repository
try {
    $context = Get-Content local-data/ci-evidence/frozen-hosting-recheck/candidate-context.json -Raw | ConvertFrom-Json
    $package = Get-Content local-data/release-candidate/package-evidence.json -Raw | ConvertFrom-Json
    $candidate = (Resolve-Path local-data/release-candidate/TogetherServer.exe).Path
    $uiRoot = (Resolve-Path ui/dist).Path
    $uiPrefix = $uiRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $evidence = New-Item -ItemType Directory -Path local-data/ci-evidence/frozen-ui-review -Force
    if ($context.verificationRevision -cne $env:GITHUB_SHA -or
        $package.app.sha256 -cne $context.candidateSha256 -or $package.assembly.matchesCurrentBuild -ne $true -or
        @($package.assets).Count -lt 1 -or @($package.assets).Count -gt 64) { throw 'Frozen UI provenance differs.' }
    function Assert-FrozenAssets {
        if ((Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash.ToLowerInvariant() -cne $context.candidateSha256 -or
            @(Get-ChildItem -LiteralPath $uiRoot -File -Recurse -Force).Count -ne @($package.assets).Count) {
            throw 'Frozen application or UI inventory differs.'
        }
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($asset in $package.assets) {
            $path = [IO.Path]::GetFullPath((Join-Path $uiRoot $asset.asset))
            if (!$path.StartsWith($uiPrefix, [StringComparison]::OrdinalIgnoreCase) -or !$names.Add($asset.asset) -or
                $asset.sha256 -cnotmatch '^[0-9a-f]{64}$' -or !(Test-Path -LiteralPath $path -PathType Leaf)) {
                throw 'Frozen UI manifest is unsafe or incomplete.'
            }
            $file = Get-Item -LiteralPath $path
            if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $file.Length -ne $asset.sizeBytes -or
                (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $asset.sha256) {
                throw 'A rendered UI asset differs from the frozen package.'
            }
        }
    }
    function Export-Capture([string]$source, [string]$name) {
        if (!(Test-Path -LiteralPath $source -PathType Container)) { return }
        $destination = New-Item -ItemType Directory -Path (Join-Path $evidence.FullName $name) -Force
        foreach ($file in Get-ChildItem -LiteralPath $source -File) {
            if ($file.Extension -notin '.json', '.png') { continue }
            if ($file.Name -notmatch '^[a-zA-Z0-9_.-]+\.(json|png)$' -or $file.Length -gt 16MB -or
                ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Capture export exceeds its bounds.' }
            Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $destination.FullName $file.Name)
        }
    }
    Assert-FrozenAssets
    $context | ConvertTo-Json -Depth 6 | Set-Content (Join-Path $evidence.FullName 'candidate-context.json') -Encoding utf8
    $renderContextPath = Join-Path $evidence.FullName 'render-context.json'
    @{sourceRevision=$context.candidateSource;verificationRevision=$context.verificationRevision;
        candidateRunId=[string]$context.candidateRunId;candidateSha256=$context.candidateSha256;
        uiAssets=@($package.assets);rebuilt=$false;appLaunched=$false;fullyAccepted=$false} |
        ConvertTo-Json -Depth 7 | Set-Content $renderContextPath -Encoding utf8
    $previousRenderContext = $env:TOGETHERSERVER_UI_CANDIDATE_CONTEXT
    $env:TOGETHERSERVER_UI_CANDIDATE_CONTEXT = $renderContextPath
    node --test scripts/ui-redesign-fixtures.test.mjs scripts/local-pairing-role-transition.test.mjs
    if ($LASTEXITCODE -ne 0) { throw 'Pure UI observation checks failed.' }
    foreach ($scope in @('simple-shared-hosting', 'save-transfer')) {
        $output = if ($scope -eq 'save-transfer') { 'local-data/save-transfer-visual-review' } else { 'local-data/ui-redesign-visual-review' }
        try {
            # Explicit hosted opt-in. Render the downloaded UI; never rebuild it.
            node scripts/render-ui-redesign.mjs "--$scope-only" --allow-isolated-browser-tests --hosted-runner github-actions
            if ($LASTEXITCODE -ne 0) { throw 'Frozen UI capture failed.' }
            $manifest = Get-Content (Join-Path $output 'manifest.json') -Raw | ConvertFrom-Json
            $index = @($package.assets | Where-Object asset -ceq 'index.html')
            if ($index.Count -ne 1 -or $manifest.build.indexSha256 -cne $index[0].sha256 -or
                $manifest.build.sourceRevision -cne $context.candidateSource -or
                $manifest.build.verificationRevision -cne $context.verificationRevision -or
                $manifest.build.candidateSha256 -cne $context.candidateSha256 -or
                $manifest.build.candidateRunId -cne [string]$context.candidateRunId -or $manifest.coverage.scope -cne $scope -or
                $manifest.status -cne 'captured-awaiting-visual-inspection' -or $manifest.summary.planned -lt 1 -or
                $manifest.summary.planned -ne $manifest.summary.attempted -or $manifest.summary.failed -ne 0 -or
                $manifest.summary.skipped -ne 0 -or $manifest.summary.layoutFindings -ne 0 -or @($manifest.fatalErrors).Count -ne 0 -or
                ($scope -eq 'save-transfer' -and $manifest.summary.planned -ne 12)) { throw 'Frozen capture evidence is incomplete or differs.' }
        }
        finally { Export-Capture $output $scope; Assert-FrozenAssets }
    }
    dotnet restore checks/TogetherServer.CompanionChecks/TogetherServer.CompanionChecks.csproj --locked-mode -p:CodeOnlyVerification=true
    if ($LASTEXITCODE -ne 0) { throw 'Frozen UI dispatcher restore failed.' }
    dotnet build checks/TogetherServer.CompanionChecks/TogetherServer.CompanionChecks.csproj -c Release --no-restore -p:CodeOnlyVerification=true
    if ($LASTEXITCODE -ne 0) { throw 'Frozen UI dispatcher build failed.' }
    Assert-FrozenAssets
    $upInterfaces = @(Get-NetAdapter | Where-Object Status -eq 'Up' | ForEach-Object InterfaceIndex)
    $lan = @(Get-NetIPAddress -AddressFamily IPv4 | Where-Object {
        $_.AddressState.ToString() -eq 'Preferred' -and $_.InterfaceIndex -in $upInterfaces -and
        $_.IPAddress -match '^(10\.|192\.168\.|172\.(1[6-9]|2[0-9]|3[01])\.)'
    } | Sort-Object InterfaceIndex, IPAddress | Select-Object -First 1)
    if ($lan.Count -ne 1) { throw 'The isolated UI lab needs one assigned private runner IPv4 address.' }
    $labRoot = Join-Path $repository 'local-data/local-pairing-lab'
    $before = @(if (Test-Path -LiteralPath $labRoot) { Get-ChildItem -LiteralPath $labRoot -Directory | ForEach-Object Name })
    $labExit = $null
    try {
        dotnet checks/TogetherServer.CompanionChecks/bin/Release/net10.0-windows/TogetherServer.CompanionChecks.dll $candidate --local-pairing-lab-journey --allow-windowless-lab --lab-lan-address $lan[0].IPAddress --lab-ux
        $labExit = $LASTEXITCODE
    }
    finally {
        Assert-FrozenAssets
        $created = @(Get-ChildItem -LiteralPath $labRoot -Directory | Where-Object { $_.Name -notin $before })
        if ($created.Count -ne 1 -or $created[0].Name -notmatch '^run-[0-9a-f]{32}$') { throw 'An exact new isolated UI report was not identified.' }
        $reportPath = Join-Path $created[0].FullName 'report.json'
        $uxRoot = Join-Path $created[0].FullName 'ux'
        $uxPath = Join-Path $uxRoot 'report.json'
        if ((Get-Item -LiteralPath $reportPath).Length -gt 262144 -or (Get-Item -LiteralPath $uxPath).Length -gt 262144) { throw 'Isolated UI evidence is oversized.' }
        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        $ux = Get-Content -LiteralPath $uxPath -Raw | ConvertFrom-Json
        $report | Select-Object schema, runId, startedUtc, endedUtc, durationMs, mode, physicalPcCount, outcome, failedStage, failureCode, stages, uxStagePassed, cleanup, windowObservation, acceptance |
            ConvertTo-Json -Depth 12 | Set-Content (Join-Path $evidence.FullName 'pairing-report.json') -Encoding utf8
        $ux | Select-Object ok, stages, errors, failedRequests, canceledRequests, roleTransitionRefusals, controlPolicyChanges, boundaries, guidedHosting, preparationDiagnostics, hostingRequestDiagnostics, hostingUiTransitions, signedDiscovery, automaticSharing, copyIntegrity, failurePathCoverage, failure |
            ConvertTo-Json -Depth 12 | Set-Content (Join-Path $evidence.FullName 'pairing-ux-report.json') -Encoding utf8
        $pictures = @(Get-ChildItem -LiteralPath $uxRoot -File -Filter '*.png')
        if ($pictures.Count -lt 1 -or $pictures.Count -gt 64) { throw 'Masked UI screenshots are missing or exceed their bound.' }
        foreach ($picture in $pictures) {
            if ($picture.Name -notmatch '^(?:[0-9]{2}-[a-z0-9-]+|failure-[0-9]+)\.png$' -or $picture.Length -gt 16MB -or
                ($picture.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Masked UI evidence is unsafe.' }
            Copy-Item -LiteralPath $picture.FullName -Destination (Join-Path $evidence.FullName $picture.Name)
        }
    }
    if ($labExit -ne 0 -or $report.outcome -cne 'Passed' -or @($report.stages).Count -ne 3 -or
        @($report.stages | Where-Object state -ne 'Passed').Count -ne 0 -or $report.cleanup.jobActiveProcesses -ne 0 -or
        $report.windowObservation.state -cne 'Passed' -or $report.windowObservation.peakVisibleOwnedWindows -ne 0 -or
        $ux.ok -ne $true -or @($ux.stages).Count -ne 10 -or @($ux.stages | Where-Object passed -ne $true).Count -ne 0 -or
        @($ux.errors).Count -ne 0 -or @($ux.failedRequests).Count -ne 0 -or
        $ux.failurePathCoverage.hostingGrantWithdrawal -ne $true -or $ux.failurePathCoverage.localConsentAndCancel -ne $true) {
        throw 'Frozen two-ended UI did not pass every original stage and consent/permission boundary.'
    }
    Write-Host 'PASS frozen hosting, transfer-phase and actual two-ended UI review; physical PC/game acceptance remains open.'
}
finally {
    $env:TOGETHERSERVER_UI_CANDIDATE_CONTEXT = $previousRenderContext
    Pop-Location
}
