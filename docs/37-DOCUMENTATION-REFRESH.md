# Documentation refresh

Date: 2026-10-20.

## Delivered organization

The root README previously mixed roughly 77 KB of feature announcements,
user instructions, security internals, local artifacts, and test/release
procedures. It now introduces the product, downloads, first use, feature areas,
game status, key safeguards, and links to the right task guide.

- `docs/guides/` holds installation, hosting/access, joining/chat, connection
  help, backups/shared hosting, and game-specific instructions.
- `docs/development/` holds source layout, builds/staging, testing/acceptance,
  and release/update verification.
- `docs/README.md` indexes task guides, core engineering rules, every existing
  Markdown design/delivery record by topic, and release evidence.
- Existing numbered files remain at their original paths. Their detailed
  implementation and evidence history is preserved.
- `START_NEW_CHAT.md` points at current rules/workflows and removes stale
  0.1.7, temporary pairing-window, unsigned-update, and unsafe test guidance.

User-facing labels were checked against current UI source, including Servers,
This PC settings, Access/Shutdown/Network, Settings & files, Setup & maintenance,
and the Updates page. A read-only worker checked feature claims against source;
its inherited workhorse model's exact name was not exposed.

## Claim and acceptance boundaries

The guides distinguish current source/preview behavior from older stable
downloads, world-only backups from complete setup checkpoints, and planned
hosting moves from offline move kits and separate-history recovery.
Minecraft fixture status, Factorio/Terraria preview limits, Terraria's blocked
Friend/automatic Stop, exact-setup Custom testing, gated real live-save
publication, peer reachability, divergent histories, and Host-dependent chat
remain explicit.

The public stable 0.3.2 and preview 0.3.2.2 release pages were checked on
2026-10-20; the dated installation note links directly to those releases.
No build, release, test, network, or real-game acceptance status is advanced
by this documentation change.

## Validation

PASS: static Markdown parsing/rendering of all 13 changed or added documents,
including 21 tables and seven PowerShell code blocks. All 155 relative links
and anchors resolve. The index covers all 48 current design, delivery, and
reference documents; existing incoming links to changed entry points remain
valid. Titles, fenced commands, UTF-8, and Git whitespace checks pass.

PASS: read-only source review of the feature descriptions and current labels.
Final corrections distinguish welcome-guide buttons from empty-workspace
actions, use Change Host address and Show removed access, include the
Game settings / Server files hierarchy, describe complete world-only backups
accurately, preserve offline solo recovery, and distinguish unsigned digest
checks from signed publisher verification.

No application, game/fixture process, browser automation, native dialog,
installer, or public listener is needed for this slice. Runtime and real-world
acceptance remain recorded in the existing delivery documents.
