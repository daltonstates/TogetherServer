import test from 'node:test';
import assert from 'node:assert/strict';
import { guidedHostingStage, roleTransitionRequestFacts, roleTransitionPayloadFacts,
  classifyRoleTransitionRefusal } from './local-pairing-role-transition.mjs';

const candidateOrigin = 'http://127.0.0.1:18102';
const ownerOrigin = 'http://127.0.0.1:18101';
const originalProfileId = '11111111-2222-3333-4444-555555555555';
const base = `/api/local/friend/${originalProfileId}/shared-world`;
const hostSnapshot = { mode: 'Host', settings: { profiles: [] }, runs: [], evidence: 'Synthetic structural proof',
  passwordConfigured: {}, managedWorldsRoot: 'synthetic-private-path-is-not-retained' };
const stamp = order => ({ order, ms: order * 10, stage: guidedHostingStage });
function request(route, method = 'GET', postData = null, overrides = {}) {
  return roleTransitionRequestFacts({ url: candidateOrigin + route, pageOrigin: candidateOrigin, candidateOrigin,
    ownerOrigin, originalProfileId, method, headers: { 'X-TogetherServer-Local': '1',
      ...(method === 'POST' ? { 'Content-Type': 'application/json', Origin: candidateOrigin } : {}) }, postData, ...overrides });
}
function event(facts, began, responded, payload, status = 200) {
  return { request: { ...facts, ...stamp(began) }, response: { ...stamp(responded), status, ...roleTransitionPayloadFacts(payload) }, state: 'Finished' };
}
function scenario() {
  return {
    refusal: event(request(base), 5, 9, { code: 'HostMode' }, 409),
    proof: { startRequestCount: 1,
      starts: [event(request(`${base}/hosting/start`, 'POST', '{"synthetic":true}'), 1, 4, { ok: true, code: 'SeparateCopyStarted' })],
      modeChanges: [event(request('/api/local/mode/host', 'POST'), 6, 8, { ok: true, code: 'ModeChanged' })],
      hostSnapshots: [event(request('/api/local/snapshot'), 10, 11, hostSnapshot)] }
  };
}
const classify = value => classifyRoleTransitionRefusal(value.refusal, value.proof);
const deny = (value, reason) => { const result = classify(value); assert.equal(result.accepted, false); if (reason) assert.equal(result.reason, reason); };

test('accepts only the completed exact refusal with Start, ModeChanged and first Host evidence', () => {
  const value = scenario();
  const result = classify(value);
  assert.equal(result.accepted, true);
  assert.equal(result.evidence.code, 'HostMode');
  assert.equal(result.evidence.successfulStart.response.code, 'SeparateCopyStarted');
  assert.equal(result.evidence.successfulModeChange.response.code, 'ModeChanged');
  assert.equal(result.evidence.firstHostSnapshot.response.mode, 'Host');
  assert.equal(classifyRoleTransitionRefusal(value.refusal, value.proof, 1).reason, 'RefusalLimitExceeded');
});

test('accepts a read dispatched during Start, before its successful response and the mode request', () => {
  const value = scenario();
  Object.assign(value.refusal.request, stamp(2));
  assert.equal(classify(value).accepted, true);
  // The backend mode can already be Host while ModeChanged is still in flight.
  Object.assign(value.proof.modeChanges[0].response, stamp(10));
  assert.equal(classify(value).accepted, true);
});

test('rejects pre-Start reads even when their refusal arrives inside the later handoff', () => {
  const value = scenario();
  Object.assign(value.refusal.request, stamp(1));
  deny(value, 'ReadPredatesStart');
});

test('rejects errors or refusals without the exact one-field HostMode payload', () => {
  for (const payload of [null, {}, { code: 'OtherConflict' }, { code: 'HostMode', message: 'private' }, ['HostMode'], { Code: 'HostMode' }]) {
    const value = scenario();
    Object.assign(value.refusal.response, roleTransitionPayloadFacts(payload));
    deny(value, 'NotExactHostModeResponse');
  }
  for (const status of [200, 400, 401, 403, 404, 408, 410, 500]) {
    const value = scenario(); value.refusal.response.status = status; deny(value);
  }
});

test('rejects every wrong request identity including mutations, query, empty body and wrong local header', () => {
  const wrongFacts = [
    request(base, 'GET', null, { url: ownerOrigin + base }),
    request(base, 'GET', null, { pageOrigin: ownerOrigin }),
    request(base.replace(originalProfileId, 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee')),
    request(`${base}/hosting`), request(`${base}/`), request(`${base}?`), request(`${base}?profile=private`),
    request(base, 'POST'), request(base, 'PUT'), request(base, 'DELETE'), request(base, 'HEAD'),
    request(base, 'GET', ''), request(base, 'GET', '{}'), request(base, 'GET', null, { postData: undefined }),
    request(base, 'GET', null, { url: candidateOrigin.replace('http://', 'http://synthetic:private@') + base }),
    request(base, 'GET', null, { headers: {} }),
    request(base, 'GET', null, { headers: { 'X-TogetherServer-Local': '0' } }),
    request(base, 'GET', null, { headers: { 'X-TogetherServer-Local': '1', Origin: ownerOrigin } }),
    request(base, 'GET', null, { headers: { 'X-TogetherServer-Local': '1', 'Content-Type': 'application/json' } })
  ];
  for (const facts of wrongFacts) { const value = scenario(); value.refusal.request = { ...value.refusal.request, ...facts }; deny(value); }
});

test('rejects a missing, duplicate, failed, canceled, wrong-code or wrong-origin Start', () => {
  for (const change of [
    value => { value.proof.startRequestCount = 0; value.proof.starts = []; },
    value => { value.proof.startRequestCount = 2; },
    value => { value.proof.starts.push(structuredClone(value.proof.starts[0])); },
    value => { value.proof.observationsTruncated = true; },
    value => { value.proof.starts[0].state = 'Failed'; },
    value => { value.proof.starts[0].state = 'Pending'; },
    value => { value.proof.starts[0].response.ok = false; },
    value => { value.proof.starts[0].response.status = 409; },
    value => { value.proof.starts[0].response.code = 'ModeChanged'; },
    value => { value.proof.starts[0].request.origin = 'OwnerLoopback'; },
    value => { value.proof.starts[0].request.body = 'Absent'; }
  ]) { const value = scenario(); change(value); deny(value); }
});

test('rejects a missing, duplicate, failed or malformed mode change', () => {
  for (const change of [
    value => { value.proof.modeChanges = []; },
    value => { value.proof.modeChanges.push(structuredClone(value.proof.modeChanges[0])); },
    value => { value.proof.modeChanges[0].state = 'Failed'; },
    value => { value.proof.modeChanges[0].response.status = 409; },
    value => { value.proof.modeChanges[0].response.ok = false; },
    value => { value.proof.modeChanges[0].response.code = 'Unavailable'; },
    value => { value.proof.modeChanges[0].request.body = 'Present'; },
    value => { value.proof.modeChanges[0].request.query = 'Present'; },
    value => { value.proof.modeChanges[0].request.localHeader = 'Absent'; },
    value => { value.proof.modeChanges[0].request.origin = 'OwnerLoopback'; }
  ]) { const value = scenario(); change(value); deny(value); }
});

test('rejects missing, failed, stale or malformed first Host snapshot proof', () => {
  for (const change of [
    value => { value.proof.hostSnapshots = []; },
    value => { value.proof.hostSnapshots[0].state = 'Failed'; },
    value => { value.proof.hostSnapshots[0].response.mode = 'Friend'; },
    value => { value.proof.hostSnapshots[0].response.status = 500; },
    value => { value.proof.hostSnapshots[0].request.query = 'Present'; },
    value => { value.proof.hostSnapshots[0].request.localHeader = 'Other'; },
    value => { Object.assign(value.proof.hostSnapshots[0].response, roleTransitionPayloadFacts({ mode: 'Host' })); },
    value => { value.proof.hostSnapshots[0].request.body = 'Present'; },
    value => { Object.assign(value.proof.hostSnapshots[0].response, stamp(7)); },
    value => { const earlier = structuredClone(value.proof.hostSnapshots[0]); Object.assign(earlier.request, stamp(2)); Object.assign(earlier.response, stamp(3)); value.proof.hostSnapshots.push(earlier); }
  ]) { const value = scenario(); change(value); deny(value); }
  const noSnapshotHeader = scenario();
  noSnapshotHeader.proof.hostSnapshots[0].request.localHeader = 'Absent';
  assert.equal(classify(noSnapshotHeader).accepted, true, 'UI getJson snapshot reads legitimately omit the sensitive local header');
});

test('never classifies requestfailed or unfinished HTTP responses as the role transition refusal', () => {
  for (const state of ['Pending', 'Failed', 'Canceled']) {
    const value = scenario(); value.refusal.state = state; deny(value, 'RefusalRequestNotExact');
  }
});

test('requires Start response before mode POST and refusal response after mode begins and before first Host snapshot', () => {
  for (const change of [
    value => { Object.assign(value.proof.starts[0].response, stamp(7)); },
    value => { Object.assign(value.refusal.request, stamp(2)); Object.assign(value.refusal.response, stamp(3)); },
    value => { Object.assign(value.refusal.response, stamp(6)); },
    value => { Object.assign(value.refusal.response, stamp(11)); },
    value => { Object.assign(value.refusal.response, stamp(12)); },
    value => { Object.assign(value.proof.modeChanges[0].response, stamp(12)); },
    value => { value.refusal.response.ms = -1; },
    value => { value.refusal.response.ms = 1; },
    value => { value.refusal.request.stage = 'before Start'; },
    value => { value.refusal.response.stage = 'candidate owner Stop'; },
    value => { value.proof.starts[0].response.stage = 'before Start'; },
    value => { value.proof.modeChanges[0].response.stage = 'after Start'; },
    value => { value.proof.hostSnapshots[0].response.stage = 'after Start'; }
  ]) { const value = scenario(); change(value); deny(value); }
  const tied = scenario();
  for (const event of [tied.refusal, ...tied.proof.starts, ...tied.proof.modeChanges, ...tied.proof.hostSnapshots]) {
    event.request.ms = 100; event.response.ms = 100;
  }
  assert.equal(classify(tied).accepted, true, 'sequence numbers disambiguate equal relative millisecond timestamps');
});

test('safe projections retain no raw URL, profile ID, header, body, message or unexpected code', () => {
  const secret = 'PrivatePersonSecret';
  const facts = roleTransitionRequestFacts({ url: `${candidateOrigin}${base}?${secret}`, pageOrigin: candidateOrigin,
    candidateOrigin, ownerOrigin, originalProfileId, method: secret,
    headers: { 'X-TogetherServer-Local': secret, Origin: secret, 'Content-Type': secret, Authorization: secret }, postData: secret });
  const payload = roleTransitionPayloadFacts({ code: secret, message: secret, ok: secret, mode: secret });
  const value = scenario();
  value.proof.hostSnapshots[0].response.code = secret;
  value.proof.starts[0].response.mode = secret;
  const retained = JSON.stringify({ facts, payload, evidence: classify(value).evidence });
  for (const privateValue of [secret, originalProfileId, candidateOrigin, ownerOrigin, hostSnapshot.managedWorldsRoot])
    assert.equal(retained.includes(privateValue), false);
});
