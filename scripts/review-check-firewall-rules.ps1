param(
    [string]$InventoryPath = '',
    [string[]]$ReviewedRoot = @(),
    [switch]$Apply,
    [string]$ExpectedInventorySha256 = '',
    [string]$OwnerApproval = ''
)

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'firewall-check-rule-review.psm1') -Force

if ([string]::IsNullOrWhiteSpace($InventoryPath)) {
    throw 'Supply the exact owner-reviewed inventory file with -InventoryPath.'
}
Assert-ReviewedRoots $ReviewedRoot

if ($Apply) {
    if ($OwnerApproval -cne 'REMOVE EXACT CHECK RULES' -or
        $ExpectedInventorySha256 -cnotmatch '^[0-9A-Fa-f]{64}$') {
        throw 'Apply requires -OwnerApproval "REMOVE EXACT CHECK RULES" and the reviewed SHA-256.'
    }
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Apply requires an already elevated PowerShell session. This tool does not launch UAC.'
    }
}
elseif ($OwnerApproval -or $ExpectedInventorySha256) {
    throw 'OwnerApproval and ExpectedInventorySha256 are only accepted with -Apply.'
}

$resolvedInventory = (Resolve-Path -LiteralPath $InventoryPath -ErrorAction Stop).ProviderPath
$digest = ''
$inventory = Read-ReviewedInventory $resolvedInventory $ReviewedRoot ([ref]$digest)
if ($Apply -and $digest -cne $ExpectedInventorySha256.ToUpperInvariant()) {
    throw 'Inventory SHA-256 differs from the owner-reviewed value.'
}
if ((Get-FileHash -LiteralPath $resolvedInventory -Algorithm SHA256).Hash -cne $digest) {
    throw 'Inventory changed while being read.'
}

function Get-ExactLiveRule {
    param([object]$ReviewedRecord)

    # Name has already passed the literal no-wildcard auto-generated-name matcher.
    # Query only the PersistentStore, then compare every reviewed field and one app filter.
    $rules = @(Get-NetFirewallRule -PolicyStore PersistentStore `
        -Name $ReviewedRecord.Name -ErrorAction Stop)
    if ($rules.Count -ne 1) { throw 'Live rule is missing or ambiguous.' }
    $filters = @(Get-NetFirewallApplicationFilter `
        -AssociatedNetFirewallRule $rules[0] -ErrorAction Stop)
    $null = Get-ReviewedRuleDecision -ReviewedRecord $ReviewedRecord `
        -LiveRules $rules -ApplicationFilters $filters
    return $rules[0]
}

# All entries must match before the first mutation. Apply then re-queries each entry
# immediately before removing that exact returned rule object. A changed entry stops
# the run; already removed entries remain removed and are reported individually.
foreach ($record in $inventory.HistoricalCheckRunnerRules) {
    $null = Get-ExactLiveRule $record
}
if ((Get-FileHash -LiteralPath $resolvedInventory -Algorithm SHA256).Hash -cne $digest) {
    throw 'Inventory changed during live preflight.'
}

Write-Host "Inventory SHA-256: $digest"
Write-Host "Reviewed roots: $($ReviewedRoot.Count)"
Write-Host "Exact live historical rules: $($inventory.HistoricalCheckRunnerRules.Count)"
Write-Host "Protected release rules in inventory: $($inventory.CurrentReleaseRules.Count)"

if (-not $Apply) {
    Write-Host 'DRY RUN: no firewall rule was changed.'
    $inventory.HistoricalCheckRunnerRules | ForEach-Object {
        [pscustomobject]@{ Decision = 'WouldRemoveExactRule'; Name = $_.Name; Program = $_.Program }
    }
    return
}

$removed = 0
foreach ($record in $inventory.HistoricalCheckRunnerRules) {
    # Recheck the reviewed file itself too, so a mid-run edit cannot widen this batch.
    if ((Get-FileHash -LiteralPath $resolvedInventory -Algorithm SHA256).Hash -cne $digest) {
        throw "Inventory changed after preflight; stopped after $removed exact removals."
    }
    $rule = Get-ExactLiveRule $record
    try {
        $rule | Remove-NetFirewallRule -Confirm:$false -ErrorAction Stop
    }
    catch {
        throw "Exact rule removal failed; stopped after $removed removals. $($_.Exception.Message)"
    }
    $removed++
    Write-Host "Removed exact reviewed rule $removed/$($inventory.HistoricalCheckRunnerRules.Count): $($record.Name)"
}
Write-Host "Removed $removed exact historical check-runner rules. No release rule was selected."
