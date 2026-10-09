param(
    [string]$AppPath = '',
    [switch]$Build,
    [switch]$RequireSignature,
    [switch]$SkipDesktop,
    [ValidateSet('All', 'Existing', 'Qol', 'QolApi', 'Browser', 'Desktop')][string]$Suite = 'All',
    [switch]$AllowInteractiveTests
)
$ErrorActionPreference = 'Stop'
if (!$AllowInteractiveTests) {
    throw 'ForegroundSafety: Full verification can change desktop focus, including console fixtures when -SkipDesktop is set. Use scripts/verify-code-only.ps1 on the active desktop. -AllowInteractiveTests requires explicit owner approval for a separate test PC or dedicated unattended Windows session.'
}
$repository = Split-Path -Parent $PSScriptRoot
$checkEvidence = [Collections.Generic.List[object]]::new()
$evidenceRoot = Join-Path $repository 'local-data/ci-evidence/release-gate'
$candidateIdentity = $null

function Invoke-Checked([string]$Name, [scriptblock]$Command) {
    Write-Host "`n== $Name =="
    $started = [DateTime]::UtcNow
    try {
        & $Command
        if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE." }
        $checkEvidence.Add(@{ name = $Name; outcome = 'passed'; elapsedSeconds = ([DateTime]::UtcNow - $started).TotalSeconds })
    }
    catch {
        $checkEvidence.Add(@{ name = $Name; outcome = 'failed'; elapsedSeconds = ([DateTime]::UtcNow - $started).TotalSeconds })
        throw
    }
}

Push-Location $repository
try {
    if ($Build -and $AppPath) { throw 'Use either -Build or -AppPath, not both.' }
    if ($Build) {
        Invoke-Checked 'Build release candidate' { & (Join-Path $PSScriptRoot 'build.ps1') -ReleaseName release-candidate }
        $AppPath = Join-Path $repository 'local-data/release-candidate/TogetherServer.exe'
    }
    elseif (!$AppPath) { $AppPath = Join-Path $repository 'local-data/release-candidate/TogetherServer.exe' }

    $AppPath = (Resolve-Path -LiteralPath $AppPath).Path
    $candidateHash = (Get-FileHash -LiteralPath $AppPath -Algorithm SHA256).Hash
    $project = [xml](Get-Content -LiteralPath 'src/TogetherServer/TogetherServer.csproj' -Raw)
    $expectedVersion = [string]$project.Project.PropertyGroup.Version
    $uiManifest = Get-Content -LiteralPath 'ui/package.json' -Raw | ConvertFrom-Json
    if ([string]$uiManifest.version -ne $expectedVersion) {
        throw "UI package version $($uiManifest.version) does not match application version $expectedVersion."
    }
    $fileInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($AppPath)
    $actualVersion = [Version]$fileInfo.FileVersion
    $expectedFileVersion = [Version]$expectedVersion
    if ($actualVersion.Major -ne $expectedFileVersion.Major -or
        $actualVersion.Minor -ne $expectedFileVersion.Minor -or
        $actualVersion.Build -ne $expectedFileVersion.Build) {
        throw "Candidate file version $actualVersion does not match project version $expectedVersion."
    }
    $sourceRevision = (git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $sourceRevision -notmatch '^[0-9a-f]{40}$') {
        throw 'Could not resolve the Git source revision.'
    }
    $expectedSourceIdentity = "$expectedVersion+$sourceRevision"
    if ($fileInfo.ProductVersion -ne $expectedSourceIdentity) {
        throw "Candidate source identity $($fileInfo.ProductVersion) does not match $expectedSourceIdentity."
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $AppPath
    if ($RequireSignature -and ($signature.Status -ne 'Valid' -or !$signature.SignerCertificate)) {
        throw "Candidate Authenticode status is $($signature.Status), not Valid."
    }
    Write-Host "Candidate: $AppPath"
    Write-Host "Version: $actualVersion"
    Write-Host "Source identity: $($fileInfo.ProductVersion)"
    Write-Host "SHA-256: $candidateHash"
    Write-Host "Authenticode: $($signature.Status)"
    $candidateIdentity = @{ sourceRevision = $sourceRevision; productVersion = $fileInfo.ProductVersion;
        sha256 = $candidateHash; authenticode = [string]$signature.Status }

    Invoke-Checked 'Git whitespace check' {
        $emptyTree = '4b825dc642cb6eb9a060e54bf8d69288fbee4904'
        git diff --cached --check $emptyTree
        if ($LASTEXITCODE -ne 0) { throw 'Tracked repository whitespace check failed.' }
        git diff --check
    }
    Invoke-Checked 'Desktop diagnostic isolation' {
        $diagnosticOutput = [IO.Path]::GetFullPath((Join-Path $repository 'src/TogetherServer/DiagnosticOutput.cs'))
        $offenders = @(Get-ChildItem -LiteralPath (Join-Path $repository 'src/TogetherServer') -Filter '*.cs' -File -Recurse |
            Where-Object { [IO.Path]::GetFullPath($_.FullName) -ne $diagnosticOutput } |
            Select-String -Pattern '\bConsole\.' -CaseSensitive)
        if ($offenders.Count -gt 0) {
            $locations = $offenders | ForEach-Object { "$($_.Path):$($_.LineNumber)" }
            throw "Desktop code bypasses DiagnosticOutput: $($locations -join ', ')"
        }
    }
    Invoke-Checked 'Check runner listener identity' {
        $checkFiles = @(Get-ChildItem -LiteralPath (Join-Path $repository 'checks'), (Join-Path $repository 'ui/checks') -File -Recurse |
            Where-Object { $_.Extension -in '.cs', '.ps1', '.mjs' -and
                $_.FullName -notmatch '[\\/](?:obj|bin)[\\/]' })
        $listenerPatterns = @(
            '\bnew\s+(?:System\.Net\.Sockets\.)?(?:TcpListener|UdpClient|Socket)\s*\(',
            '\[(?:System\.)?Net\.Sockets\.(?:TcpListener|UdpClient|Socket)\]::new\s*\(',
            '\.Bind\s*\(', '\.Listen\s*\(', '\bcreateServer\s*\(', '\bcreateSocket\s*\('
        )
        $offenders = @($checkFiles | Select-String -Pattern $listenerPatterns)
        if ($offenders.Count -gt 0) {
            $locations = $offenders | ForEach-Object { "$($_.Path):$($_.LineNumber)" }
            throw "Check runners must not own OS listeners: $($locations -join ', ')"
        }
        $probeOffenders = @($checkFiles | Select-String -Pattern 'PortProbeMode\.LoopbackOnly' |
            Where-Object { $_.Line -notmatch 'GameServerRegistry\.ProbeAddress\s*\(' })
        if ($probeOffenders.Count -gt 0) {
            $locations = $probeOffenders | ForEach-Object { "$($_.Path):$($_.LineNumber)" }
            throw "Check runners must use ObserveOnly for indirect port probes: $($locations -join ', ')"
        }
        $coreChecks = Get-Content -LiteralPath 'checks/TogetherServer.Checks/Program.cs' -Raw
        $routes = [regex]::Matches($coreChecks, 'new CompanionServer\([\s\S]*?\);')
        if ($routes.Count -eq 0 -or @($routes | Where-Object {
            !$_.Value.Contains('inMemoryTransport: builder => builder.UseTestServer()')
        }).Count -gt 0) {
            throw 'Core route checks must use in-memory TestServer; packaged journeys own real sockets.'
        }
    }
    if ($Suite -in 'All', 'Existing') {
        Invoke-Checked 'UI lint' {
            Push-Location ui
            try { npm run lint }
            finally { Pop-Location }
        }
        Invoke-Checked 'UI unit tests' {
            Push-Location ui
            try { npm test }
            finally { Pop-Location }
        }
        Invoke-Checked 'Locked application restore' { dotnet restore src/TogetherServer/TogetherServer.csproj --locked-mode }
        $checkProjects = @(
            'checks/TogetherServer.Checks/TogetherServer.Checks.csproj',
            'checks/TogetherServer.ValheimChecks/TogetherServer.ValheimChecks.csproj',
            'checks/TogetherServer.FactorioChecks/TogetherServer.FactorioChecks.csproj',
            'checks/TogetherServer.TerrariaChecks/TogetherServer.TerrariaChecks.csproj',
            'checks/TogetherServer.MinecraftChecks/TogetherServer.MinecraftChecks.csproj',
            'checks/TogetherServer.MinecraftSetupChecks/TogetherServer.MinecraftSetupChecks.csproj',
            'checks/TogetherServer.CustomChecks/TogetherServer.CustomChecks.csproj',
            'checks/TogetherServer.SharedHistoryChecks/TogetherServer.SharedHistoryChecks.csproj',
            'checks/TogetherServer.UpdateChecks/TogetherServer.UpdateChecks.csproj'
        )
        foreach ($checkProject in $checkProjects) {
            $name = [IO.Path]::GetFileNameWithoutExtension($checkProject)
            Invoke-Checked $name { dotnet run --project $checkProject -c Release }
        }
        Invoke-Checked 'Core remote journey' {
            dotnet run --project checks/TogetherServer.CompanionChecks/TogetherServer.CompanionChecks.csproj -c Release -- $AppPath --core-remote-journey
        }
        Invoke-Checked 'World-load packaged rehearsal' {
            dotnet run --project checks/TogetherServer.CompanionChecks/TogetherServer.CompanionChecks.csproj -c Release -- $AppPath --world-load-rehearsal
        }
        Invoke-Checked 'Shared Worlds packaged journey' {
            dotnet run --project checks/TogetherServer.CompanionChecks/TogetherServer.CompanionChecks.csproj -c Release -- $AppPath --shared-world-journey
        }
        Invoke-Checked 'Staging LiveSave owner action' {
            dotnet run --project checks/TogetherServer.CompanionChecks/TogetherServer.CompanionChecks.csproj -c Release -- $AppPath --live-save-action
        }
        Invoke-Checked 'Shared LiveSave packaged transfer journey' {
            dotnet run --project checks/TogetherServer.CompanionChecks/TogetherServer.CompanionChecks.csproj -c Release -- $AppPath --shared-live-transfer-journey
        }
        Invoke-Checked 'TogetherServer.CompanionChecks' {
            dotnet run --project checks/TogetherServer.CompanionChecks/TogetherServer.CompanionChecks.csproj -c Release -- $AppPath
        }
        Invoke-Checked 'Solution formatting' { dotnet format TogetherServer.slnx --verify-no-changes --no-restore }
        Invoke-Checked 'Packaged served smoke' { & checks/served-smoke.ps1 -AppPath $AppPath }
        Invoke-Checked 'Production plus staging isolation smoke' { & checks/staging-smoke.ps1 -AppPath $AppPath }
        if (!$SkipDesktop) {
            Invoke-Checked 'Packaged hidden desktop smoke' { & checks/desktop-smoke.ps1 -AppPath $AppPath -Port 0 -AllowInteractiveTests }
            Invoke-Checked 'Packaged interactive desktop smoke' { & checks/desktop-smoke.ps1 -AppPath $AppPath -Port 0 -Interactive -AllowInteractiveTests }
        }
        else { Write-Host 'SKIP packaged hidden desktop smoke (-SkipDesktop was supplied).' }

        Invoke-Checked 'Update handoff smoke' { & checks/update-handoff-smoke.ps1 -AppPath $AppPath }
    }

    if ($Suite -in 'All', 'Qol', 'QolApi') {
        Invoke-Checked 'TogetherServer.FeatureChecks' {
            dotnet run --project checks/TogetherServer.FeatureChecks/TogetherServer.FeatureChecks.csproj -c Release
        }
        Invoke-Checked 'QoL packaged API journey' {
            dotnet run --project checks/TogetherServer.CompanionChecks/TogetherServer.CompanionChecks.csproj -c Release -- $AppPath --qol-api-journey
        }
    }
    if ($Suite -in 'All', 'Qol', 'Browser') {
        Invoke-Checked 'QoL bundled browser journeys' {
            node ui/checks/qol-browser-smoke.mjs --app-path $AppPath --allow-interactive-tests --output-dir local-data/ci-evidence/qol-browser
        }
    }
    if ($Suite -in 'All', 'Qol', 'Desktop') {
        if (!$SkipDesktop) {
            Invoke-Checked 'QoL native desktop smoke' { & checks/qol-desktop-smoke.ps1 -AppPath $AppPath -AllowInteractiveTests }
        }
        else { Write-Host 'SKIP QoL native desktop smoke (-SkipDesktop was supplied).' }
    }

    $finalHash = (Get-FileHash -LiteralPath $AppPath -Algorithm SHA256).Hash
    if ($finalHash -ne $candidateHash) { throw 'Candidate bytes changed during verification.' }
    Write-Host "`nPASS exact candidate remained $candidateHash through serial verification."
}
finally {
    New-Item -ItemType Directory -Path $evidenceRoot -Force | Out-Null
    @{ suite = $Suite; candidate = $candidateIdentity; checks = @($checkEvidence.ToArray());
        boundary = 'Separate approved Windows session; synthetic and loopback evidence does not establish real game, WAN, join or save acceptance.' } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $evidenceRoot 'release-gate-report.json') -Encoding utf8
    Pop-Location
}
