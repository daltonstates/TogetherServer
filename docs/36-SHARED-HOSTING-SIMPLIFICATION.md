# Shared hosting simplification

Owner direction, 2026-10-21: shared hosting has one server setting and a
permitted Friend's **Host locally** action. Receiving saves, forwarding them,
selecting a verified continuation and preparing hosting are app responsibilities.
This supersedes the user-managed sharing, recovery, default-selection and
planned-move flows in delivery records 26, 30, 31 and 35. Their signed histories
and compatibility code remain protected.

## Delivered behavior

- **Host:** **Allow friends to host this server** enables post-Stop protection
  and signed Receive, EligibleHost and Reshare rights for every approved assigned
  original member. Later enrollment, approval and assignment inherit the same
  policy. Disable withdraws all three rights in the signed roster. Per-device
  access removal, assignment and owner deadlines remain authoritative.
- **Friend:** only a currently permitted server exposes **Host locally**.
  One confirmation explains incoming connections and retaining shared progress.
  The app prepares and starts after verification, showing only actionable
  installation, terms, setup, connection or port blockers. Polling and reopening
  the app never carry a Start intent.
- **Saves:** permitted PCs automatically retain completed copies. A receiver
  can seed a save outward to an approved peer's pinned listener without hosting
  or opening its own listener. Configured signed contact addresses allow retry
  after the destination later enables incoming connections. An advertised
  address remains an unverified contact hint.
- **Latest:** explicit preparation checks reachable peers, complete payloads
  and signed parent ancestry across continuations. Original Host identity,
  wall-clock time and branch-local numbers have no priority. A returning owner
  follows a newer continuation in fresh managed storage. Every existing world
  remains intact.
- **Review:** concurrent progress, an incomplete newest known copy, changed
  local files and damaged signed state block automatic selection. The app
  preserves evidence and does not merge worlds or fall back to a known older
  save. Ordinary sharing screens contain no grant, voter or forwarding editor.

## Implementation boundaries

`SharedSavesEnabled` is the one persisted original-Host policy. Pairing state
and signed roster updates remain separate durable writes; failed publication
retains the existing dirty-roster fence. Startup repair includes disabled
profiles so an interrupted withdrawal cannot leave signed hosting rights active.
Enrollment publishes all affected assignments, rather than only its first
requested server. Legacy per-PC mutation is refused while the policy is on.

Automatic initial receiving requires current pinned signed Receive, EligibleHost
and Reshare rights. Explicit local withdrawal and damaged consent storage still
deny receiving. No remote setting silently opens a Friend listener. The explicit
Host locally network confirmation may enable an already configured incoming
connection, with existing local mode, origin, header and policy gates.

Outbound seeding uses one fixed authenticated operation on the existing pinned
peer transport. The receiver chooses exact missing manifests and bounded chunks;
full-file hashes and current membership are checked before publication. Source
history follows nested continuations back to their signed source. No request
accepts an executable, game command, world path or destination path.

Preparation preserves its selected signed branch independently of older local
profiles. Existing local progress must match its completed publication before
switching. The final latest check is bound to a short-lived observation of signed
save and membership state, rechecked inside the lifecycle/membership launch gate.
A new local observation invalidates Start; background metadata never starts or
stops a game.

## Acceptance and remaining checks

Local validation passed. Hosted rendering and the packaged process journey are
pending at this checkpoint; their exact revision and results will be recorded
below. No result from an earlier candidate is a pass for these changes.

Local automated work is code-only. No app, game, console fixture, browser,
installer or native dialog is launched on the owner's active desktop.

Real save consistency, game load/change/graceful Stop/restart, a three-PC
A-to-B-to-C-to-returning-A session, actual IP/port routing and a friend joining
remain external acceptance requirements. Production live capture stays gated
for each exact installed game; normal safe publication remains after Stop.
An offline Host's unshared running progress cannot be reconstructed.

At least one previously learned approved peer route must work. The app cannot
discover an unknown new address while every remembered peer is unreachable.
There is no cloud directory, relay, automatic NAT traversal or firewall/router/DNS
change. Offline revocations become enforceable when authenticated updates arrive.

## Validation record

Local verification on 2026-10-21:

| Check | Result |
| --- | --- |
| `scripts/verify-code-only.ps1` | PASS: pinned .NET 10.0.301 and Node 24.14.1, locked dependencies, typecheck, lint, full solution compile, all 62 source/synthetic groups, whitespace and desktop diagnostic isolation |
| Full JSDOM suite | PASS: 1,323 tests in 106 files; final notice follow-up passes all 33 focused cases |
| Hosted-renderer source checks | PASS: 31 fixture/guard/inventory tests, script syntax and all seven workflow PowerShell blocks; 18 bounded render plans |
| Production React bundle | PASS: existing JavaScript chunk-size advisory above 500 kB |
| Normal app build embedding the current bundle | PASS: zero warnings/errors |
| Packaged three-app private-address journey | Pending hosted Windows execution |
| Desktop/narrow rendered UI | Pending hosted Windows execution and visual inspection |
| Physical PCs, outside game route and real saved game | Not executed; external acceptance remains open |

One source symbolic-link subcase is explicitly SKIPPED because this account
cannot create the link. Native file-lease and other link checks remain separate
executed source cases. Local file/fake-HTTP checks exercise outbound-only B
seeding C, C's descendant reaching B and returning A, exact retained owner
source without original-world modification, incomplete/fork/tamper rejection,
final-chunk local withdrawal and the in-gate latest-save observation fence.
No local app, fixture process, listener or browser was launched.

Initial checks exposed incorrect synthetic hosting keys/source binding and
unmatched fake endpoint/bind fixtures; those were corrected while retaining
signature and route enforcement. A legacy session-alias denial returned the
server-policy code before its existing membership-read-only classification;
guard precedence was corrected. Initial compile-only property/namespace errors
were also corrected. The final full gate above passed after these changes.

Ignored local evidence: `local-data/simple-hosting-code-only-final.log`,
`local-data/simple-hosting-ui-full-tests.json`,
`local-data/simple-hosting-renderer-source.log`,
`local-data/simple-hosting-bundle-final.log`, and
`local-data/simple-hosting-embedded-build-final.log`.
