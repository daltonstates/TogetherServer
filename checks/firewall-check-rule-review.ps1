param([string]$InventoryPath = '')

# Pure/static checks only. This script imports no live firewall adapter and calls no
# NetSecurity cmdlet. Supplying InventoryPath reads JSON from disk only.
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) `
    'scripts/firewall-check-rule-review.psm1') -Force

function Assert-Equal($Expected, $Actual, [string]$Label) {
    if ($Expected -cne $Actual) { throw "${Label}: expected '$Expected', got '$Actual'." }
}

function Assert-Rejected([scriptblock]$Action, [string]$Label) {
    try { & $Action | Out-Null }
    catch { return }
    throw "$Label was accepted."
}

function Copy-Record($Value) {
    return ($Value | ConvertTo-Json -Depth 8 | ConvertFrom-Json)
}

$root = 'G:\repo\TogetherServer'
$program = "$root\checks\TogetherServer.Checks\bin\Release\net10.0-windows\TogetherServer.Checks.exe"
$record = [pscustomobject]@{
    Name = "TCP Query User{01234567-89AB-CDEF-0123-456789ABCDEF}$program"
    DisplayName = 'TogetherServer.Checks'
    Program = $program
    Enabled = 'True'
    Profile = 'Public'
    Direction = 'Inbound'
    Action = 'Allow'
}
$releaseProgram = "$root\local-data\release\TogetherServer.exe"
$release = [pscustomobject]@{
    Name = "TCP Query User{12345678-89AB-CDEF-0123-456789ABCDEF}$releaseProgram"
    DisplayName = 'TogetherServer'
    Program = $releaseProgram
    Enabled = 'True'
    Profile = 'Public'
    Direction = 'Inbound'
    Action = 'Allow'
}
$inventory = [pscustomobject]@{
    Schema = 1
    CapturedUtc = '2000-01-01T00:00:00Z'
    PolicyStore = 'PersistentStore'
    HistoricalCheckRunnerRules = @($record)
    CurrentReleaseRules = @($release)
}
Assert-ReviewedInventory $inventory @($root)

$live = [pscustomobject]@{
    Name = $record.Name
    DisplayName = $record.DisplayName
    Enabled = $record.Enabled
    Profile = $record.Profile
    Direction = $record.Direction
    Action = $record.Action
    PolicyStoreSource = 'PersistentStore'
}
$filter = [pscustomobject]@{ Program = $record.Program }
$decision = Get-ReviewedRuleDecision $record @($live) @($filter)
Assert-Equal 'WouldRemoveExactRule' $decision.Decision 'Mock dry-run decision'
Assert-Equal $record.Name $decision.Name 'Exact selected name'

foreach ($field in @('Name', 'DisplayName', 'Enabled', 'Profile', 'Direction',
        'Action', 'PolicyStoreSource')) {
    $changed = Copy-Record $live
    $changed.$field = 'changed'
    Assert-Rejected { Get-ReviewedRuleDecision $record @($changed) @($filter) } `
        "Changed live $field"
}
Assert-Rejected { Get-ReviewedRuleDecision $record @() @($filter) } 'Missing live rule'
Assert-Rejected { Get-ReviewedRuleDecision $record @($live, $live) @($filter) } `
    'Duplicate live rules'
Assert-Rejected { Get-ReviewedRuleDecision $record @($live) @() } 'Missing app filter'
Assert-Rejected { Get-ReviewedRuleDecision $record @($live) @($filter, $filter) } `
    'Duplicate app filters'
$changedFilter = [pscustomobject]@{ Program = "$root\local-data\release\TogetherServer.exe" }
Assert-Rejected { Get-ReviewedRuleDecision $record @($live) @($changedFilter) } `
    'Production live app path'
$changedFilter.Program = "$root\checks\TogetherServer.Checks\bin\Debug\net10.0-windows\TogetherServer.Checks.exe"
Assert-Rejected { Get-ReviewedRuleDecision $record @($live) @($changedFilter) } `
    'Changed live app path'

foreach ($field in @('Name', 'DisplayName', 'Program', 'Enabled', 'Profile',
        'Direction', 'Action')) {
    $changed = Copy-Record $record
    $changed.$field = 'changed'
    Assert-Rejected { Assert-HistoricalCheckRule $changed @($root) } `
        "Changed inventory $field"
}
$changed = Copy-Record $record
$changed.Program = $releaseProgram
$changed.Name = "TCP Query User{01234567-89AB-CDEF-0123-456789ABCDEF}$releaseProgram"
Assert-Rejected { Assert-HistoricalCheckRule $changed @($root) } 'Production inventory path'
$changed = Copy-Record $record
$changed.Program = $changed.Program.Replace('\TogetherServer.Checks.exe',
    '\TogetherServer.CompanionChecks.exe')
$changed.Name = "TCP Query User{01234567-89AB-CDEF-0123-456789ABCDEF}$($changed.Program)"
Assert-Rejected { Assert-HistoricalCheckRule $changed @($root) } 'Runner/exe mismatch'
$changed = Copy-Record $record
$changed.Name = $changed.Name.Replace('TCP Query User', 'TCP Query Users')
Assert-Rejected { Assert-HistoricalCheckRule $changed @($root) } 'Nonstandard name'
Assert-Rejected { Assert-HistoricalCheckRule $record @('G:\repo\Other') } `
    'Unreviewed root'
Assert-Rejected { Assert-ReviewedRoots @('G:\repo\TogetherServer\..\TogetherServer') } `
    'Path traversal root'
Assert-Rejected { Assert-ReviewedRoots @('G:\repo\TogetherServer-worktrees') } `
    'Worktree parent instead of exact root'

$changedInventory = Copy-Record $inventory
$changedInventory.HistoricalCheckRunnerRules = @($record, $record)
Assert-Rejected { Assert-ReviewedInventory $changedInventory @($root) } `
    'Duplicate reviewed names'
$changedInventory = Copy-Record $inventory
$changedInventory.HistoricalCheckRunnerRules = @($release)
Assert-Rejected { Assert-ReviewedInventory $changedInventory @($root) } `
    'Production item in historical array'
$changedInventory = Copy-Record $inventory
$changedInventory.PolicyStore = 'ActiveStore'
Assert-Rejected { Assert-ReviewedInventory $changedInventory @($root) } `
    'Wrong policy store'
$changedInventory = Copy-Record $inventory
$changedInventory.HistoricalCheckRunnerRules[0] | Add-Member NoteProperty Extra 'unsupported'
Assert-Rejected { Assert-ReviewedInventory $changedInventory @($root) } `
    'Unsupported inventory field'

$invalidJsonPath = Join-Path ([IO.Path]::GetTempPath()) `
    ("togetherserver-firewall-review-$([guid]::NewGuid().ToString('N')).json")
try {
    [IO.File]::WriteAllText($invalidJsonPath, '{"Schema":1,',
        (New-Object Text.UTF8Encoding($false)))
    Assert-Rejected { Read-ReviewedInventory $invalidJsonPath @($root) } `
        'Malformed JSON inventory'
    $duplicateJson = ($inventory | ConvertTo-Json -Depth 8) -replace '^\s*\{',
        '{"Schema":1,'
    [IO.File]::WriteAllText($invalidJsonPath, $duplicateJson,
        (New-Object Text.UTF8Encoding($false)))
    Assert-Rejected { Read-ReviewedInventory $invalidJsonPath @($root) } `
        'Duplicate JSON key'
}
finally { Remove-Item -LiteralPath $invalidJsonPath -ErrorAction SilentlyContinue }

if ($InventoryPath) {
    # Deriving roots here is for a static test only. The cleanup command requires
    # the owner to pass every exact reviewed root explicitly.
    $raw = Get-Content -LiteralPath $InventoryPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $roots = @($raw.HistoricalCheckRunnerRules | ForEach-Object {
        if ($_.Program -match '^(.*)\\checks\\(?:TogetherServer\.Checks|TogetherServer\.CompanionChecks)\\bin\\') {
            $matches[1]
        }
        else { throw 'Inventory has a non-check-runner path.' }
    } | Sort-Object -Unique)
    $parsedDigest = ''
    $reviewed = Read-ReviewedInventory $InventoryPath $roots ([ref]$parsedDigest)
    Assert-Equal (Get-FileHash -LiteralPath $InventoryPath -Algorithm SHA256).Hash `
        $parsedDigest 'Digest of the parsed inventory bytes'
    Assert-Equal 66 $reviewed.HistoricalCheckRunnerRules.Count 'Current static historical count'
    Assert-Equal 2 $reviewed.CurrentReleaseRules.Count 'Current static release count'
    Write-Host "PASS static inventory: 66 historical rules, 2 protected release rules, $($roots.Count) exact roots."
}

Write-Host 'PASS pure matcher and mock decision checks; no desktop firewall queried or changed.'
