param([switch]$Run)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot

# This deliberately does not invoke verify-release or any desktop/console
# fixture journey. --local-pairing-lab permits only windowless synthetic runs.
# A request for this lab never authorizes other active-desktop process tests.
if (!$Run) {
    Write-Output 'Local pairing lab: one Host and two Friends, separate disposable data, pinned loopback HTTPS, pipe-only synthetic game.'
    Write-Output 'No app windows, native consoles, browser, game installation, public listeners, or production data.'
    Write-Output 'Use -Run to build and run this focused lab. Results stay in local-data/local-pairing-lab.'
    return
}

Push-Location $repository
try {
    $appPath = Join-Path $repository 'src/TogetherServer/bin/Release/net10.0-windows/win-x64/TogetherServer.exe'
    $harnessPath = Join-Path $repository 'checks/TogetherServer.CompanionChecks/bin/Release/net10.0-windows/TogetherServer.CompanionChecks.dll'
    $fixturePath = Join-Path $repository 'src/TogetherServer.Fixture/bin/Release/net10.0/TogetherServer.Fixture.exe'
    foreach ($active in @(Get-Process -Name TogetherServer,TogetherServer.Fixture -ErrorAction SilentlyContinue)) {
        if ($active.Path -eq $appPath -or $active.Path -eq $fixturePath) {
            throw 'The lab build output is already in use. Finish that lab before rebuilding; production was left running.'
        }
    }
    # Build the guarded source here: never pass the lab flag to an older app
    # which might ignore it and allocate its normal native console at startup.
    dotnet build checks/TogetherServer.CompanionChecks/TogetherServer.CompanionChecks.csproj -c Release --no-restore -p:CodeOnlyVerification=true
    if ($LASTEXITCODE -ne 0) { throw 'Pairing lab compilation failed.' }
    dotnet build src/TogetherServer.Fixture/TogetherServer.Fixture.csproj -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic fixture compilation failed.' }
    dotnet $harnessPath $appPath --local-pairing-lab-journey --allow-windowless-lab
    if ($LASTEXITCODE -ne 0) { throw 'Pairing lab failed. Inspect its redacted report; remaining stages are not passed.' }
}
finally { Pop-Location }
