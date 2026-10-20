# Releases and updates

[Developer guide](README.md) · [Testing](testing.md) · [User update steps](../guides/getting-started.md#install-updates-when-you-are-ready)

Release preparation runs process, desktop, update, and installer checks.
Use an explicitly approved separate PC or dedicated unattended Windows session.
The scripts refuse without `-AllowInteractiveTests`; that switch does not
authorize running them on the owner's active desktop.

## Prepare an immutable candidate

1. Start from a clean checkout with reviewed changes committed. Choose a new
   numeric `MAJOR.MINOR.PATCH` or `MAJOR.MINOR.PATCH.REVISION` version, keep the
   application and UI versions aligned, and commit the change.
2. Confirm the matching `vVERSION` tag is unused locally and on origin, and
   that `local-data\github-release\vVERSION` does not exist. The preparation
   script also enforces these checks and requires a version above existing
   numeric tags.
3. Install Inno Setup 6 on the test machine for installer packaging. Use the
   Windows SDK's `signtool.exe` only when owner-provided signing is available.
4. In the approved session, run:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/prepare-github-release.ps1 -AllowInteractiveTests
```

Optional signing uses `-SigningCertificateThumbprint CERT_THUMBPRINT` or
`TOGETHERSERVER_SIGNING_THUMBPRINT`. A requested signature must validate; the
script does not silently fall back after a signing failure. Without a
certificate, it prepares unsigned assets, reports **NotSigned**, and uses the
applicable digest-verified update checks. Signing uses SHA-256 and an RFC 3161
timestamp. Do not invent or request signing credentials without authorization.

The script builds and verifies one exact app candidate, then builds and
smoke-tests the installer containing those same app bytes. It refuses to
replace an installed copy during disposable installer smoke or overwrite an
existing release directory. Outputs under `local-data\github-release\vVERSION`:

| Asset | Purpose |
| --- | --- |
| `TogetherServer-Setup-win-x64.exe` | Per-user installer. |
| `TogetherServer-Setup-win-x64.exe.sha256` | Installer checksum. |
| `TogetherServer-win-x64.exe` | Portable app and the fixed in-app update asset. |
| `TogetherServer-win-x64.exe.sha256` | Portable checksum. |

Preparation does not create a tag or publish. Obtain explicit owner
authorization for those actions. Keep candidate bytes unchanged through
verification, installer packaging, publication, and public-download checks.
Record exact source, run/job/artifact identities, hashes, signature state,
passes/failures/skips, and real-world limits in the release delivery document.

After authorized publication, independently download all four assets without
authentication and verify names, sizes, versions, digests, and stable/prerelease
classification. A private build or upload result does not prove public bytes.

## Stable and preview channels

Stable update checks use GitHub's `/releases/latest`. Per-PC
**Include prerelease versions** checks the 100 most recently created published
releases and chooses the highest eligible numeric version. Switching it off
invalidates a prepared preview, returns to stable checks, and never downgrades.

Use `vMAJOR.MINOR.PATCH` or `vMAJOR.MINOR.PATCH.REVISION` tags for either channel.
Suffix tags such as `v0.3.3-preview.1` are unsupported by this updater. Mark a
preview as a GitHub prerelease. Every later preview needs a higher numeric
version. Promoting a verified preview to stable uses the same assets, without
rebuilding them. The published 0.3.2 updater only recognizes three-part tags,
so its users must directly install a four-part preview initially.

Previews use production settings, credentials, and worlds. The checkbox is
not staging isolation. Before distributing a preview, test it in both
Host/Friend directions with the current stable app and verify data/update
compatibility. See the [prerelease design](../30-PRERELEASE-UPDATES.md) and
[release evidence index](../README.md#release-evidence).

## How the app verifies an update

Installation always requires the user's **Update and restart** action. The
updater checks only this repository's eligible fixed portable asset, bounds
its size, verifies the release SHA-256 digest, and requires its file version
to match the tag. The helper checks hashes again immediately before and
after replacement.

If the installed app has a valid Authenticode signature, the candidate and
helper must also have valid signatures from the same publisher public key.
An invalid or unverifiable installed signature disables updates. Unsigned
installations use size, version, and GitHub digest checks without a publisher
identity check. Current unsigned releases may show Unknown Publisher.
Their trust root is the GitHub repository, release metadata, and digest.

The UI separates **Verified publisher**, **GitHub digest only**,
**Signature rejected**, and **Development build**. A digest match is not a
Windows publisher identity or SmartScreen reputation claim. Development/staging
does not consume release updates.

Before handoff, active or unresolved managed runs block exit. The app creates
a bounded same-user state checkpoint tied to the current EXE hash, target
version, and storage schema. Both app and helper validate it. The helper
keeps `.previous` beside the running EXE, restores it if post-replacement
verification fails, and reopens the same filename.

## Data and rollback

Updates keep settings, pairings, worlds, and completed backups in their
existing data directory. Storage schema 5 migrates older schema 1–4 roots
marker-first; protected pairing snapshots retain schema 3. Existing grants
are preserved, and new sharing grants do not turn on by inference. Older
binaries reject a newer storage marker.

The update checkpoint is bounded local control-state recovery material, not a
complete world backup or automatic schema rollback. Returning to an older
binary requires a matching complete pre-migration data backup. Never lower a
marker manually. Review quarantined or interrupted state through owner recovery.

For current measured build and release status, follow the specific delivery
record rather than copying its transient paths into the README.
