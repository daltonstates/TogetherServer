# Historical check-runner firewall rules

`scripts/review-check-firewall-rules.ps1` is a standalone, manual owner tool for old Windows Firewall rules created for `TogetherServer.Checks.exe` and `TogetherServer.CompanionChecks.exe`. It is not called by TogetherServer, builds, or release verification. It does not change router, DNS, listeners, or any production `TogetherServer.exe` rule.

## Review and use

1. Keep the schema-1 inventory JSON in ignored local data. Inspect its `HistoricalCheckRunnerRules` and `CurrentReleaseRules` entries. Supply each **exact** repository or worktree directory in `-ReviewedRoot`; the worktree parent alone is refused. Removed worktree directories may still be reviewed roots because the rule stores their historical literal paths. Do not edit an inventory after reviewing its SHA-256.
2. Run the pure checks below when desktop firewall access is inappropriate. They parse the inventory and use mock live-rule objects; they never call a firewall cmdlet:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File checks/firewall-check-rule-review.ps1 -InventoryPath '<ignored reviewed inventory.json>'
   ```

3. Later, when it is safe to query the desktop firewall, run the default dry-run in a PowerShell session that permits local scripts. Keep the root array and script invocation in the same session; `powershell -File` may not bind an array argument reliably. The dry-run reads live rules but changes none. Supply the exact roots you reviewed, for example:

   ```powershell
   $inventory = '<ignored reviewed inventory.json>'
   $roots = @('G:\repo\TogetherServer', 'G:\repo\TogetherServer-worktrees\<exact worktree>')
   & .\scripts\review-check-firewall-rules.ps1 -InventoryPath $inventory -ReviewedRoot $roots
   ```

   Review the listed exact names, root count, historical count, protected release count, and inventory SHA-256. Any missing, changed, duplicate, or ambiguous live entry stops preflight.

4. Only after that review, open an elevated PowerShell session yourself and pass the dry-run SHA-256, the same inventory and roots, and the literal approval phrase:

   ```powershell
   & .\scripts\review-check-firewall-rules.ps1 -InventoryPath $inventory -ReviewedRoot $roots -Apply -ExpectedInventorySha256 '<dry-run SHA-256>' -OwnerApproval 'REMOVE EXACT CHECK RULES'
   ```

The tool never starts UAC. Apply first compares every listed live field and application path against the inventory, then freshly re-queries each rule immediately before passing that exact rule object to `Remove-NetFirewallRule`. It stops on any mismatch or failure; earlier successful removals in that run stay removed. It never uses a wildcard delete. If a run stops partway through, make a new reviewed inventory of the remaining rules before another Apply.

Only enabled Public inbound Allow entries in `PersistentStore` are eligible. Their display name, executable basename, exact `TCP Query User{GUID}` or `UDP Query User{GUID}` name plus full Program path, and literal `checks\...\bin\Debug|Release\net10.0` or `net10.0-windows` location under a supplied root must agree. Release entries are protected and cannot qualify. No live desktop firewall validation is claimed by the pure checks.
