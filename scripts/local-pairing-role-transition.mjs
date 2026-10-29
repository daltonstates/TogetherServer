// Pure evidence classification. This module never imports or launches a browser/app.
export const guidedHostingStage = 'missing server blocks then one confirmation prepares and Starts';

const methods = new Set(['GET', 'POST', 'PUT', 'DELETE', 'PATCH', 'HEAD', 'OPTIONS']);
const responseCodes = new Set(['HostMode', 'SeparateCopyStarted', 'ModeChanged']);
const guid = '[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{12}';

// Persist identity comparisons, never the input URL, ID, header values or body.
export function roleTransitionRequestFacts({ url, pageOrigin, candidateOrigin, ownerOrigin, originalProfileId,
  method, headers, postData }) {
  const parsed = new URL(url);
  const local = Object.fromEntries(Object.entries(headers ?? {}).map(([key, value]) => [key.toLowerCase(), value]));
  const originLabel = value => value === candidateOrigin ? 'CandidateLoopback' : value === ownerOrigin ? 'OwnerLoopback' : 'Other';
  const profile = parsed.pathname.match(new RegExp(`^/api/local/friend/(${guid})(?:/|$)`, 'u'))?.[1];
  const base = `/api/local/friend/${originalProfileId}/shared-world`;
  const route = parsed.pathname === base ? 'OriginalSharedWorld'
    : parsed.pathname === `${base}/hosting/start` ? 'GuidedStart'
      : parsed.pathname === `${base}/hosting/progress` ? 'OriginalHostingProgress'
        : parsed.pathname === '/api/local/mode/host' ? 'HostMode'
          : parsed.pathname === '/api/local/snapshot' ? 'Snapshot' : 'Other';
  return {
    pageRole: pageOrigin === candidateOrigin ? 'Candidate' : pageOrigin === ownerOrigin ? 'Owner' : 'Other',
    origin: originLabel(parsed.origin), sameOrigin: parsed.origin === pageOrigin,
    method: methods.has(method) ? method : 'Other', route,
    profile: profile === undefined ? 'None' : profile === originalProfileId ? 'Original' : 'Other',
    localHeader: local['x-togetherserver-local'] === '1' ? 'Exact' : local['x-togetherserver-local'] === undefined ? 'Absent' : 'Other',
    originHeader: local.origin === undefined ? 'Absent' : originLabel(local.origin),
    contentType: local['content-type'] === undefined ? 'Absent' : local['content-type'] === 'application/json' ? 'Json' : 'Other',
    query: url.split('#', 1)[0].includes('?') ? 'Present' : 'Absent',
    body: postData === null ? 'Absent' : typeof postData === 'string' ? 'Present' : 'Unknown',
    urlCredentials: parsed.username || parsed.password ? 'Present' : 'Absent'
  };
}

export function roleTransitionPayloadFacts(value) {
  const object = value !== null && typeof value === 'object' && !Array.isArray(value) ? value : null;
  const record = item => item !== null && typeof item === 'object' && !Array.isArray(item);
  return {
    code: responseCodes.has(object?.code) ? object.code : 'Unavailable',
    ok: object?.ok === true,
    mode: object?.mode === 'Host' || object?.mode === 'Friend' ? object.mode : 'Other',
    codeOnlyBody: object?.code === 'HostMode' && Object.keys(object).length === 1,
    emptyBody: false,
    // Structural HostSnapshot proof only. The journey independently asserts the
    // exact running candidate, selected profile, source, hashes and session.
    hostSnapshotShape: object?.mode === 'Host' && record(object.settings) && Array.isArray(object.settings.profiles) &&
      Array.isArray(object.runs) && typeof object.evidence === 'string' && record(object.passwordConfigured) &&
      typeof object.managedWorldsRoot === 'string'
  };
}

// JSON decode failure is not proof of an empty response. The capture caller
// supplies actual response bytes only after the HTTP response completes.
export function roleTransitionBodyFacts(bytes) {
  return { emptyBody: bytes instanceof Uint8Array && bytes.byteLength === 0 };
}

const validStamp = value => Number.isSafeInteger(value?.order) && value.order > 0 &&
  Number.isFinite(value?.ms) && value.ms >= 0;
const before = (left, right) => validStamp(left) && validStamp(right) && left.order < right.order && left.ms <= right.ms;
const inStage = event => event?.request?.stage === guidedHostingStage && event?.response?.stage === guidedHostingStage;
const completed = event => event?.state === 'Finished' && before(event.request, event.response);
const successful = event => completed(event) && Number.isInteger(event.response.status) && event.response.status >= 200 && event.response.status < 300;
const exactRequest = (request, route, method, body, contentType) => request?.pageRole === 'Candidate' &&
  request.origin === 'CandidateLoopback' && request.sameOrigin === true && request.route === route && request.method === method &&
  request.profile === (['OriginalSharedWorld', 'OriginalHostingProgress', 'GuidedStart'].includes(route) ? 'Original' : 'None') &&
  (route === 'Snapshot' ? ['Absent', 'Exact'].includes(request.localHeader) : request.localHeader === 'Exact') &&
  ['Absent', 'CandidateLoopback'].includes(request.originHeader) &&
  request.query === 'Absent' && request.body === body && request.contentType === contentType && request.urlCredentials === 'Absent';

function evidenceEvent(event) {
  const request = event.request;
  const response = event.response;
  return {
    request: { order: request.order, ms: request.ms, stage: request.stage, pageRole: request.pageRole, origin: request.origin,
      sameOrigin: request.sameOrigin, method: request.method, route: request.route, profile: request.profile,
      localHeader: request.localHeader, originHeader: request.originHeader, contentType: request.contentType,
      query: request.query, body: request.body, urlCredentials: request.urlCredentials },
    response: { order: response.order, ms: response.ms, stage: response.stage, status: response.status,
      code: responseCodes.has(response.code) ? response.code : 'Unavailable', ok: response.ok === true,
      mode: ['Host', 'Friend'].includes(response.mode) ? response.mode : 'Other', codeOnlyBody: response.codeOnlyBody === true,
      emptyBody: response.emptyBody === true, hostSnapshotShape: response.hostSnapshotShape === true },
    state: event.state
  };
}

// A Start response is observed before its body is decoded. Preserve that event's
// order while awaiting JSON, so callback scheduling cannot rewrite the timeline.
// Only one precise read-only refusal may be separated from HTTP failures. A
// requestfailed event is never an input to this exception.
export function classifyRoleTransitionRefusal(event, { startRequestCount, starts, modeChanges, hostSnapshots,
  observationsTruncated = false }, acceptedCount = 0) {
  const denied = reason => ({ accepted: false, reason });
  const progress = event?.request?.route === 'OriginalHostingProgress';
  const exactPayload = progress
    ? event?.response?.emptyBody === true && event.response.code === 'Unavailable' && event.response.codeOnlyBody === false &&
      event.response.ok === false && event.response.mode === 'Other' && event.response.hostSnapshotShape === false
    : event?.response?.code === 'HostMode' && event.response.codeOnlyBody === true && event.response.emptyBody !== true;
  if (event?.response?.status !== 409 || !exactPayload)
    return denied(progress ? 'NotExactEmptyProgressResponse' : 'NotExactHostModeResponse');
  if (!completed(event) || !inStage(event) ||
    !exactRequest(event.request, progress ? 'OriginalHostingProgress' : 'OriginalSharedWorld', 'GET', 'Absent', 'Absent'))
    return denied('RefusalRequestNotExact');
  if (observationsTruncated || startRequestCount !== 1 || starts.length !== 1)
    return denied('SingleStartNotProven');
  const start = starts[0];
  if (!successful(start) || !inStage(start) || start.response.code !== 'SeparateCopyStarted' || start.response.ok !== true ||
    !exactRequest(start.request, 'GuidedStart', 'POST', 'Present', 'Json')) return denied('SuccessfulStartNotProven');
  if (!before(start.request, event.request)) return denied('ReadPredatesStart');
  const firstHost = hostSnapshots.filter(snapshot => snapshot.request?.pageRole === 'Candidate' &&
    snapshot.request.origin === 'CandidateLoopback' && snapshot.request.route === 'Snapshot' && snapshot.response?.mode === 'Host' &&
    before(start.request, snapshot.response)).sort((left, right) => left.response.order - right.response.order)[0];
  if (!firstHost || !successful(firstHost) || !inStage(firstHost) || firstHost.response.hostSnapshotShape !== true ||
    !exactRequest(firstHost.request, 'Snapshot', 'GET', 'Absent', 'Absent')) return denied('FirstHostSnapshotNotProven');
  if (!before(event.response, firstHost.response)) return denied('ReadOutsideTransition');
  const changes = modeChanges.filter(change => change.request?.pageRole === 'Candidate' &&
    before(start.request, change.request) && before(change.request, firstHost.response));
  if (changes.length !== 1) return denied('SingleModeChangeNotProven');
  const mode = changes[0];
  if (!successful(mode) || !inStage(mode) || mode.response.code !== 'ModeChanged' || mode.response.ok !== true ||
    !exactRequest(mode.request, 'HostMode', 'POST', 'Absent', 'Json')) return denied('SuccessfulModeChangeNotProven');
  if (!before(start.response, mode.request) || !before(mode.request, event.response) || !before(mode.response, firstHost.response))
    return denied('TransitionOrderNotProven');
  if (acceptedCount !== 0) return denied('RefusalLimitExceeded');
  return { accepted: true, reason: progress ? 'ConfirmedFriendToHostProgressTransition' : 'ConfirmedFriendToHostTransition', evidence: {
    code: progress ? 'Unavailable' : 'HostMode', status: 409,
    refusal: evidenceEvent(event), successfulStart: evidenceEvent(start), successfulModeChange: evidenceEvent(mode),
    firstHostSnapshot: evidenceEvent(firstHost)
  } };
}
