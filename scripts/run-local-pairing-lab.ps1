param([switch]$Run, [string]$LanAddress, [switch]$Ux)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot

# This deliberately does not invoke verify-release or any desktop/console
# fixture journey. --local-pairing-lab permits only windowless synthetic runs.
# A request for this lab never authorizes other active-desktop process tests.
if (!$Run) {
    if ($Ux) {
        Write-Output 'Automated local Host/Friend UX: real isolated backends, pairing, sharing, chat, hosting and new-Host Connect through the bundled UI.'
        Write-Output 'One contained headless browser; sound muted; all descendants observed for visible windows. No manual app clicks.'
    } elseif ($LanAddress) {
        Write-Output 'Automated hosting lab: one Host and two Friends, separate disposable data, pinned HTTPS on one explicitly selected local LAN address, pipe-only synthetic game.'
        Write-Output 'Guided hosting, signed new-Host discovery and Connect must pass. This remains a same-PC process simulation.'
    } else {
        Write-Output 'Local pairing lab: one Host and two Friends, separate disposable data, pinned loopback HTTPS, pipe-only synthetic game.'
    }
    Write-Output 'No native app windows, game installation, router/firewall changes or production data.'
    Write-Output 'Use -Run to build and run this focused lab. Results stay in local-data/local-pairing-lab.'
    return
}

Push-Location $repository
try {
    $appPath = Join-Path $repository 'src/TogetherServer/bin/Release/net10.0-windows/win-x64/TogetherServer.exe'
    $harnessPath = Join-Path $repository 'checks/TogetherServer.CompanionChecks/bin/Release/net10.0-windows/TogetherServer.CompanionChecks.dll'
    $fixturePath = Join-Path $repository 'src/TogetherServer.Fixture/bin/Release/net10.0/TogetherServer.Fixture.exe'
    if ($Ux -and !$LanAddress) {
        $privateAddresses = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop | Where-Object {
            $_.AddressState.ToString() -eq 'Preferred' -and
            $_.IPAddress -match '^(10\.|192\.168\.|172\.(1[6-9]|2[0-9]|3[01])\.)'
        })
        if ($privateAddresses.Count -ne 1) { throw 'Choose the one assigned private LAN IPv4 address with -LanAddress. No test app was opened.' }
        $LanAddress = $privateAddresses[0].IPAddress
    }
    if ($LanAddress) {
        # A missing Windows application rule can prompt on the active desktop.
        # Reuse existing permission only; never add or change firewall rules.
        $assignment = @(Get-NetIPAddress -AddressFamily IPv4 -IPAddress $LanAddress -ErrorAction Stop)
        if ($assignment.Count -ne 1) { throw 'The selected lab address is not assigned uniquely to this PC.' }
        $connection = Get-NetConnectionProfile -InterfaceIndex $assignment[0].InterfaceIndex -ErrorAction Stop
        $profileName = if ($connection.NetworkCategory.ToString() -eq 'DomainAuthenticated') { 'Domain' } else { $connection.NetworkCategory.ToString() }
        $firewallProfile = Get-NetFirewallProfile -Name $profileName -ErrorAction Stop
        if ($firewallProfile.Enabled.ToString() -eq 'True' -and $firewallProfile.NotifyOnListen.ToString() -ne 'False') {
            $permitted = $false
            $rules = @(Get-NetFirewallApplicationFilter -Program $appPath -ErrorAction Stop | Get-NetFirewallRule -ErrorAction Stop)
            foreach ($rule in $rules) {
                if ($rule.Enabled.ToString() -ne 'True' -or $rule.Direction.ToString() -ne 'Inbound' -or $rule.Action.ToString() -ne 'Allow') { continue }
                if ($rule.Profile.ToString() -ne 'Any' -and @($rule.Profile.ToString() -split ',\s*') -notcontains $profileName) { continue }
                $ports = $rule | Get-NetFirewallPortFilter -ErrorAction Stop
                $addresses = $rule | Get-NetFirewallAddressFilter -ErrorAction Stop
                if (@('TCP', '6', 'Any') -contains $ports.Protocol.ToString() -and
                    $ports.LocalPort -eq 'Any' -and @($addresses.LocalAddress) -contains 'Any' -and @($addresses.RemoteAddress) -contains 'Any') { $permitted = $true }
            }
            if (!$permitted) { throw 'LAN lab refused before launch: existing firewall permission cannot be verified without a possible desktop prompt. No rule was changed.' }
        }
    }
    foreach ($active in @(Get-Process -Name TogetherServer,TogetherServer.Fixture -ErrorAction SilentlyContinue)) {
        if ($active.Path -eq $appPath -or $active.Path -eq $fixturePath) {
            throw 'The lab build output is already in use. Finish that lab before rebuilding; production was left running.'
        }
    }
    # Build the guarded source here: never pass the lab flag to an older app
    # which might ignore it and allocate its normal native console at startup.
    if ($Ux) {
        npm --prefix ui run build --ignore-scripts
        if ($LASTEXITCODE -ne 0) { throw 'Bundled UI compilation failed.' }
    }
    dotnet build checks/TogetherServer.CompanionChecks/TogetherServer.CompanionChecks.csproj -c Release --no-restore -p:CodeOnlyVerification=true
    if ($LASTEXITCODE -ne 0) { throw 'Pairing lab compilation failed.' }
    dotnet build src/TogetherServer.Fixture/TogetherServer.Fixture.csproj -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic fixture compilation failed.' }
    $labArgs = @($harnessPath, $appPath, '--local-pairing-lab-journey', '--allow-windowless-lab')
    if ($LanAddress) { $labArgs += @('--lab-lan-address', $LanAddress) }
    if ($Ux) { $labArgs += '--lab-ux' }
    dotnet @labArgs
    if ($LASTEXITCODE -ne 0) { throw 'Pairing lab failed. Inspect its redacted report; remaining stages are not passed.' }
}
finally { Pop-Location }
