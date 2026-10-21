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

Local validation and final hosted rendering passed. The latest packaged run
passed the full journey with lab diagnostics enabled. An earlier returning-owner
review blocker has not been explained; two bounded hosted runs without a
diagnostic sink remain pending. Earlier failed results remain failed and do not
establish acceptance for a later candidate.

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
| Full JSDOM suite | PASS: final switch-first UI passes all 1,327 tests in 106 files |
| Hosted-renderer source checks | PASS: 31 fixture/guard/inventory tests, script syntax, workflow YAML and all ten workflow PowerShell blocks; 18 bounded render plans |
| Production React bundle | PASS: existing JavaScript chunk-size advisory above 500 kB |
| Normal app build embedding the current bundle | PASS: zero warnings/errors |
| Packaged three-app private-address journey | Full diagnostic-enabled run passes; ordinary runs without diagnostic collection pending |
| Desktop/narrow rendered UI | Final switch-first view passes 18/18, zero layout findings/errors; desktop and narrow screenshots inspected |
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

Ignored local evidence: `local-data/simple-hosting-code-only-diagnostic-final.log`,
`local-data/simple-hosting-ui-final-cleanup.json`,
`local-data/simple-hosting-renderer-source.log`,
`local-data/simple-hosting-bundle-final-cleanup.log`, and
`local-data/simple-hosting-embedded-build-diagnostic.log`.

### Initial hosted run and bounded corrections

[Run 39649848998](https://github.com/daltonstates/TogetherServer/actions/runs/39649848998)
tested source `b1c66523b22ca1dd1524eefffdd592bec0ebb7fb`.
The render job `119394990792` failed its two owner-on cases because Playwright's
native checked-state assertion ran before the controlled asynchronous save
settled. The other 16 cases passed with zero layout findings or JavaScript
errors. The corrected helper clicks normally and waits for the canonical
checked and enabled state without changing the DOM or submitting a second
request. Its 31 source checks pass.

The packaged job `119394990327` passed isolated setup, automatic signed grants
and receiving, ordinary Friend preparation/Start, and the listener-less peer's
verified descendant receipt. It then failed `SimpleAppIdentityMismatch` before
the returning Host's HTTP startup had been awaited. The retained identity has a
nonzero start time and no identity-review flag. The corrected test uses the
existing lab's health-before-identity order and also verifies that the local
listener belongs to that exact process. Dispatcher compilation passes with
zero warnings/errors; no product code changed for this correction.

This partial packaged result does not prove the remaining outbound relay,
returning owner Start or policy withdrawal. Cleanup reported `jobEmpty: true`.
The initial candidate SHA-256 was
`ee5a1f540b39c3b4430cdf56632557de1c6e55df601068dddb80648f0bbd1956`.
Downloaded evidence was hash-verified: render artifact `12368574582`
(`5e4fa2efbbd197223dd27ecb2e808c867f646220fb8da19f31cc34162f28406e`)
and journey artifact `12370105570`
(`ddb3d6cec736486b9cdff847b7c6163e1275702c2efd2beb6765e983332bc445`).
Representative Host, desktop/narrow confirmation, installation, connection and
conflict screenshots were inspected; no overflow or obscured action was found.

### Hosted retry and final presentation review

[Run 39651442020](https://github.com/daltonstates/TogetherServer/actions/runs/39651442020)
tested source `3c26900dec03e14b5982d2c06dd62f2264a953f0`.
Render job `119400509182` passed all 18 plans with 45 screenshots, zero layout
findings, zero fatal/JavaScript errors and zero skipped cases. Hash-verified
artifact `12370281434` has ZIP SHA-256
`a8c5e54a5ddac3bb69707778bfec9e08a76ffbdc1cfd8cf969f3932a0bb85c10`.
Visual inspection found no overflow, but identified an obsolete governance
heading above the Host switch. That heading and implementation-testing prose
were removed; the final switch-first presentation passes 46 focused tests,
typecheck/lint, the full 1,327-test JSDOM suite and a fresh React bundle.

Packaged job `119400509069` successfully restarted the original Host and verified
the relayed descendant's signature, every file hash and payload. The producer
was offline, the receiver had no listener, and the original world's bytes
remained unchanged. Normal owner Start then returned
`SharedSaveHistoryNeedsReview`; it did not start an older world. This remains a
failed acceptance case, with successful relay receipt distinguished from Start.
Cleanup again reported `jobEmpty: true` and no identity-review flags. The
candidate SHA-256 was
`42a9c5a343887d5d9b5935cc71eb988dba2230f94b6fcd8456d7eb8d9cdc8f00`;
hash-verified journey artifact `12370127178` has ZIP SHA-256
`c82febb791c079926f44a71103cd5449a5a86e5dcf49a59a91d6e69c804227ed`.

The focused source regression was tightened to retain a real original signed
publication and owner metadata, dispose the original service, make the producing
peer unavailable, and forward its descendant from a listener-less receiver to a
fresh owner service. Latest selection and exact owner retention pass in that
file/fake-HTTP case. This does not replace the failed packaged result.
The regression also repeats latest selection after owner preparation and
announcement, matching the ordinary Start service's final recheck. That focused
case and the full 62-group code-only gate pass. The current embedded app build
also passes with zero warnings/errors. An earlier redirected Windows PowerShell
invocation stopped on an intentional source-test stderr warning; it was
incomplete and is not counted as a gate pass. The isolated child-script rerun
above completed all registered groups.

The next hosted candidate retains at most 16 fixed selection-phase, reason,
exception-type and candidate-count records in its disposable lab report. It
records no paths, addresses, keys, save identities, payloads or raw errors.
Only validated lab mode can install that diagnostic sink. The next candidate
additionally requires an explicit diagnostic opt-in; ordinary application and
lab runs have no sink. All fail-closed source decisions remain unchanged.

### Diagnostic candidate and final UI evidence

[Run 39655442203](https://github.com/daltonstates/TogetherServer/actions/runs/39655442203)
tests source `73a9df4c8d9900b40d0901ee3ca60a26aaa9f06f`.
Render job `119414332802` passes all 18 plans with 44 screenshots, zero failed
or skipped cases, zero layout findings and zero fatal errors. The final desktop
and narrow Host-on screenshots were visually inspected: the policy switch is
first, obsolete governance headings are absent, and the action remains visible.
The earlier inspected Friend dialogs have unchanged source in this candidate.
Hash-verified render artifact `12372121018` has ZIP SHA-256
`8ea3b6d2e978fe925d9a5e2f5f4d14565eb333eeb1a22899189f8f4bc7c581c6`.
Packaged job `119414333213` passes all seven stages in 134,606 ms. Both Friend
and returning owner Start succeed on their first attempt. The owner uses the
relayed descendant in a fresh continuation, original bytes remain unchanged,
global disable withdraws signed rights, and cleanup reports `jobEmpty: true`
with no identity-review flags. This is same-runner private-address synthetic
evidence. It is a pass for this candidate and does not explain or erase the
earlier rejection.

Candidate SHA-256:
`20ce4afc2ee9202aeb6054cc5d11ecb82e1abc59838c666c0e5459d9c955a9c7`.
Hash-verified journey artifact `12370752852` has ZIP SHA-256
`ac3040bbec26d68b505052dfd204c5131ecdde912b1b468a18aa003a54476773`.

The next bounded hosted check runs the ordinary journey twice against one
immutable candidate with diagnostic collection explicitly off. If either fails,
a separate diagnostic attempt may collect fixed failure metadata, but the job
retains its ordinary failure even if that diagnostic attempt succeeds.
