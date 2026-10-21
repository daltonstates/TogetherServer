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
- **Server list:** inactive retained copies collapse into one ordinary server
  entry. A returning owner's active continuation remains under Your servers;
  its sharing controls use the original server's switch and normal Start checks
  the latest save. Active, unknown and recovery-review copies remain visible.
  Stored profiles and worlds are unchanged by this presentation rule.
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

The complete code-only gate, 1,342-test UI suite and final 20-case hosted
rendering pass after correcting an incomplete-vault inventory race and a
separate older-history retention gap. The final immutable packaged candidate
passes the expanded eight-stage journey twice with diagnostics off. Earlier
failed results remain failed and do not establish acceptance for a later
candidate. Hosted evidence uses synthetic games and private addresses on one
runner; real-PC, outside routing and real-game acceptance remain open.

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
| Full JSDOM suite | PASS: final logical-server UI passes all 1,342 tests in 107 files |
| Hosted-renderer source checks | PASS: 31 fixture/guard/inventory tests, script syntax, workflow YAML and all ten workflow PowerShell blocks; expanded to 20 bounded render plans |
| Production React bundle | PASS: existing JavaScript chunk-size advisory above 500 kB |
| Normal app build embedding the current bundle | PASS: zero warnings/errors |
| Packaged three-app private-address journey | PASS: final immutable candidate passes two ordinary diagnostic-disabled eight-stage journeys, original v2 to Friend v3 to returning owner; original world preserved and owned processes cleaned up |
| Desktop/narrow rendered UI | Final logical-server view passes 20/20, zero layout findings/errors; desktop and narrow screenshots inspected |
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

Ignored local evidence: `local-data/simple-hosting-code-only-fixed-final.log`,
`local-data/simple-hosting-ui-logical-final.json`,
`local-data/simple-hosting-renderer-logical-final.log`,
`local-data/simple-hosting-bundle-logical-final.log`, and
`local-data/simple-hosting-embedded-logical-final.log`.

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

### Ordinary run and diagnostic failure

[Run 39656885865](https://github.com/daltonstates/TogetherServer/actions/runs/39656885865)
tests source `52b4af1fddcaac1a7a39b84f5756be98743d68ad`.
Render job `119419404844` passed. Packaged job `119419405630` failed: its first
ordinary run timed out waiting for the listener-less receiver's descendant.
The separate diagnostic retry received and forwarded that descendant, but
returning owner Start then failed `SharedSaveHistoryNeedsReview`. Both attempts
reported an empty owned job at cleanup. The second ordinary attempt did not run;
the diagnostic retry is not an ordinary pass.

The diagnostic shows two successful latest-selection passes, with both local
payloads verified and one unique descendant. The following failure is specifically
`PeerSync / ReviewRequired / InvalidDataException`, before another inventory
selection. It is distinct from the separately identified exact-retention gap.
Candidate SHA-256:
`3b8b7cc7dbe1b19589f186e1ee15dabb90802fc2b643667e8834de39cd3850c8`.
Hash-verified journey artifact `12371318349` has ZIP SHA-256
`f8fec2a2abb227c6fa018c101607caccd59c63e91bb049cedb7631abfdaa2b98`.

The expanded source regression starts from two original publications, then a
Friend descendant. Its first failure independently exposed inventory reading a
new incoming branch directory's proof before that transfer had a completed
pointer. That incomplete directory must be skipped; a present corrupt pointer
must still fail closed. The regression will also exercise retaining older signed
manifests across the new owner's prepared source branch. The packaged journey
now requires two linked original saves before the Friend takes over, rather
than testing only a first publication.

### Reproduced fixes

Two independent source failures were reproduced before correction:

- Incoming history staging created a branch directory before promoting its
  proof and completed pointer. Inventory read the proof first and failed on
  that unfinished directory, including while seeding searched for a parent
  manifest. Inventory now reads the completed pointer first and skips absent
  pointers. A present invalid pointer still blocks the group.
- A newly prepared owner base kept only the Friend's current source segment.
  Latest selection could verify older canonical ancestry globally, while exact
  retention searched only its selected vault. Retention now resolves a missing
  exact hash/number from other managed manifests in the same world. Full parent
  and signer verification still runs; branch identity and payload source stay
  pinned.

The expanded file/fake-HTTP regression passes after both fixes, including
original v1-to-v2, a Friend v3, outbound relay with the producer offline, fresh
owner source retention, a deliberately unfinished owner incoming vault and
corrupt completed-pointer refusal. The existing recovery-selection group also
passes known-newest-without-payload, damaged newest, missing ancestry and fork
refusal. Both focused groups and the full 62-group code-only gate pass with zero
build warnings/errors. The expanded packaged journey remains pending at this
checkpoint. These cases prove
the two fixes independently; they do not establish the exact cause of every
earlier hosted failure.

### Final logical-server presentation checkpoint

The returning owner no longer sees its continuation labeled as hosting for a
friend. Exact local-original identity takes precedence over a same-ID saved
Friend connection. Normal Start from a retained child targets its original
local server, then selects the actual newly running continuation. Both the
sharing switch and read-only peer details target the original policy. The
obsolete split-save confirmation and its legacy Start route are removed from
the ordinary local Start path.

Only inactive duplicate rows are hidden. Running, Unknown, failed, recovery
review and Offline-with-process-identity rows remain visible. No settings,
profiles or worlds are deleted. Focused JSDOM validation passes 144/144 cases;
the full suite passes 1,342/1,342 in 107 files. Typecheck, full lint, the React
bundle and the normal app build embedding that bundle pass with zero compile
warnings/errors. A new desktop/narrow hosted case starts from
a current continuation, original and older retained copy, and checks the one
logical row, original policy request and absence of a guest label. The bounded
render plan now contains 20 cases.

### Final combined candidate

[Run 39661708341](https://github.com/daltonstates/TogetherServer/actions/runs/39661708341)
is checking immutable source `2cf8814580e237fa9a627622a2e40240bc318ff6`.
Render job `119436115605` includes the returning-owner continuation at both
widths. Packaged job `119436115261` requires two ordinary diagnostic-disabled
journeys against one executable, including two linked original publications
before the Friend's descendant and outbound-only relay. Both pass all eight
stages in 150,454 ms and 155,932 ms. Each records source version 2 and descendant
version 3, one Friend Start attempt, one owner Start attempt, no diagnostic
report, empty owned jobs at cleanup and no process identity-review flags.
The original world's bytes remain unchanged, and signed global withdrawal
passes. This is same-runner private-address synthetic evidence.

Candidate SHA-256:
`88468dd7abb91ecbb5037ffd1b37c2df97b66d7973e8589b202730b24ae1a175`.
Hash-verified journey artifact `12375331637` has ZIP SHA-256
`fe6e1553a4b19b10ea47b151b973c437262f80a0d42dd96c773d5b3d9873cf65`.

Render job `119436115605` failed only the two new returning-owner cases; 18/20
passed with zero layout, JavaScript, console or fatal errors. The assertion
incorrectly treated the active continuation's earlier Overview save-status
read as a policy-editor read. The captured request trace contains the correct
original status and governance requests when Sharing opens. The corrected
check identifies the policy editor by its governance read, still requires its
paired original status read, and rejects any child governance request. All 31
renderer source checks pass, including this exact trace and a wrong-policy
counterexample. No product code changed. Desktop and narrow failure screenshots
were inspected: one local server row, the original switch checked and enabled,
and no guest label or layout obstruction. They remain failure evidence until
the corrected hosted check passes.

Hash-verified render artifact `12373028771` has ZIP SHA-256
`54f62dd91bdac424fc535e936de201548170394ec99578431965ffca40a35ef9`.

### Corrected final UI check

[Run 39662205609](https://github.com/daltonstates/TogetherServer/actions/runs/39662205609)
checks source `67f66591453544fffa183d13d815b278dc0af0d5`, with no product changes
after the combined candidate. Render job `119437881698` passes all 20 cases,
54 screenshots, zero skips, layout findings, JavaScript, console and fatal
errors. The final returning-owner desktop/narrow screens and narrow Friend
network confirmation were visually inspected. The single local server row,
original sharing switch and one-confirmation Friend action are unobstructed.

Hash-verified render artifact `12375755731` has ZIP SHA-256
`8d9aac8ea993fcaace4c724afe33701e6653347a25f1625a8ed516d0b73d4e66`.
Its built index SHA-256 is
`6daccc7ea4d02a3d42cc5a9184cc19060d5997a9f6125afa7e83bbf66936a8e1`.
Packaged job `119437882319` passes both ordinary diagnostic-disabled journeys
in 174,908 ms and 174,573 ms. Each passes all eight stages with original version
2 and Friend descendant version 3, including listener-less receipt, outbound
relay with the producer offline and normal returning-owner Start. Each Friend
and owner Start succeeds on its first attempt. Original bytes remain unchanged,
global disable withdraws signed rights, cleanup reports `jobEmpty: true`, all
owned processes are gone and no identity-review flags remain. Neither run
records a diagnostic report; no diagnostic retry ran.

Final candidate SHA-256:
`d4a78b2f66b06e0d9a079a8956d509ed6184a8ad135cc51149014847094dd958`.
Product version: `0.3.3+67f66591453544fffa183d13d815b278dc0af0d5`.
Hash-verified journey artifact `12375691958` has ZIP SHA-256
`69c6770d866b72fc7af196296ff89f4e8e471195acd3bb1a423b569f228c5547`.
Its candidate record binds the same source revision and run ID as the final
render evidence. The two raw reports and candidate record were inspected after
artifact hash verification. Ignored downloaded evidence is under
`local-data/simple-hosting-hosted-67f6659/`.

The following documentation-only commit records this evidence without changing
the tested product or harness. No release, tag, production installation, public
network change or real-world access was performed. The real-PC and real-game
acceptance boundaries above remain unchanged.
