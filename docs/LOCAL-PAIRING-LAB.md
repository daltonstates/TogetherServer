# Local pairing and shared-save lab

The lab runs disposable synthetic Host/Friend instances and can exercise their
bundled interfaces with Playwright. It checks pairing, sharing consent, chat,
completed-copy transfer, guided hosting and connection recovery.

Run it only on an explicitly approved separate test PC or unattended Windows
session. Windowless apps and a headless browser do not establish focus safety.
Use the [code-only checks](development/testing.md#on-the-owners-active-desktop)
on the owner's active desktop. Past permission for a particular lab run does
not authorize another run.

## Prepare and run

Use the pinned .NET SDK and Node version, locked dependencies, and the locked
Playwright Chromium headless shell. Read
[the runner](../scripts/run-local-pairing-lab.ps1) before choosing a mode.
Without `-Run`, it only describes the requested lab.

In the approved test session:

```powershell
powershell -NoProfile -File scripts/run-local-pairing-lab.ps1 -Run -Ux
```

`-Ux` drives separate Host and Friend interfaces. Omit it for the protocol
rehearsal. Supply `-LanAddress <assigned-private-IPv4>` when address selection
is ambiguous; this still uses two ends on the same PC.

The runner builds the guarded current source, uses separate disposable data,
checks existing firewall permission, and refuses unsafe prerequisites. It does
not create firewall rules. Do not pass lab flags to an older production EXE.

## Results and limits

Redacted reports and optional masked UX captures go under ignored
`local-data/local-pairing-lab/run-*/`. Keep outcomes, failures, skips and cleanup
results together. Credentials and private protected test state stay out of Git.

A successful lab proves only the exercised synthetic, same-PC path. It does
not prove physical LAN/WAN transfer, real game readiness or occupancy, a game
join, live-save acceptance, or recognizable progress after restart. Use the
[two-PC checklist](TWO-PC-CONNECTION-TEST.md) and
[acceptance requirements](04-IMPLEMENTATION-AND-ACCEPTANCE.md) for those gates.
