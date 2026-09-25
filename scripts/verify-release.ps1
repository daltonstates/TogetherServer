param(
    [string]$AppPath = '',
    [switch]$Build,
    [switch]$RequireSignature,
    [switch]$SkipDesktop
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot

function Invoke-Checked([string]$Name, [scriptblock]$Command) {
    Write-Host "`n== $Name =="
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$Name failed with exit code $LASTEXITCODE." }
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

    Invoke-Checked 'Git whitespace check' {
        $emptyTree = '4b825dc642cb6eb9a060e54bf8d69288fbee4904'
        git diff --cached --check $emptyTree
        if ($LASTEXITCODE -ne 0) { throw 'Tracked repository whitespace check failed.' }
        git diff --check
    }
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
        'checks/TogetherServer.MinecraftChecks/TogetherServer.MinecraftChecks.csproj',
        'checks/TogetherServer.MinecraftSetupChecks/TogetherServer.MinecraftSetupChecks.csproj',
        'checks/TogetherServer.CustomChecks/TogetherServer.CustomChecks.csproj',
        'checks/TogetherServer.UpdateChecks/TogetherServer.UpdateChecks.csproj'
    )
    foreach ($checkProject in $checkProjects) {
        $name = [IO.Path]::GetFileNameWithoutExtension($checkProject)
        Invoke-Checked $name { dotnet run --project $checkProject -c Release }
    }
    Invoke-Checked 'TogetherServer.CompanionChecks' {
        dotnet run --project checks/TogetherServer.CompanionChecks/TogetherServer.CompanionChecks.csproj -c Release -- $AppPath
    }
    Invoke-Checked 'Solution formatting' { dotnet format TogetherServer.slnx --verify-no-changes --no-restore }
    Invoke-Checked 'Packaged served smoke' { & checks/served-smoke.ps1 -AppPath $AppPath }
    Invoke-Checked 'Production plus staging isolation smoke' { & checks/staging-smoke.ps1 -AppPath $AppPath }
    if (!$SkipDesktop) {
        Invoke-Checked 'Packaged hidden desktop smoke' { & checks/desktop-smoke.ps1 -AppPath $AppPath -Port 0 }
    }
    else { Write-Host 'SKIP packaged hidden desktop smoke (-SkipDesktop was supplied).' }

    if ($signature.Status -eq 'Valid' -and $signature.SignerCertificate) {
        Invoke-Checked 'Signed update handoff smoke' { & checks/update-handoff-smoke.ps1 -AppPath $AppPath }
    }
    elseif ($RequireSignature) { throw 'Signed update handoff could not run without a valid candidate signature.' }
    else { Write-Host 'SKIP signed update handoff: development candidate is not Authenticode-valid.' }

    $finalHash = (Get-FileHash -LiteralPath $AppPath -Algorithm SHA256).Hash
    if ($finalHash -ne $candidateHash) { throw 'Candidate bytes changed during verification.' }
    Write-Host "`nPASS exact candidate remained $candidateHash through serial verification."
}
finally { Pop-Location }
