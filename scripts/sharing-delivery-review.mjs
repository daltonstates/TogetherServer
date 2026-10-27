// Pure synthetic capture plans and assertions. No IO, browser setup or app launch.
const deliveryRegion = 'Background save transfer for Weekend shared world';
export const sharingDeliveryCases = ['sending', 'timeout-metadata', 'historical-receipt'].map(state => ({
  id: `sharing-delivery-${state}`, scenario: 'host-peers', workspace: 'Host', sharingDelivery: state,
  section: ['Selected server sections', 'backups', 'Backups'],
  steps: [{ nav: 'World protection sections', button: 'Sharing & hosting' }],
  screen: { role: 'region', name: deliveryRegion }
}));

export function applySharingDeliveryFixture(state, testCase, now) {
  if (!testCase.sharingDelivery) return;
  if (!sharingDeliveryCases.some(item => item.sharingDelivery === testCase.sharingDelivery) ||
      state.scenario !== 'host-peers' || state.worldPeers.length !== 1 || !Number.isFinite(Date.parse(now)))
    throw new Error('Sharing delivery fixture does not match its fixed synthetic world.');
  const progress = patch => ({ state: 'Working', phase: 'Sending', operation: 'Seed', httpStep: 'Exchange',
    result: 'None', error: 'None', httpStatus: null, startedUtc: new Date(Date.parse(now) - 12000).toISOString(), updatedUtc: now,
    elapsedMilliseconds: 12000, submittedBytes: 12582912, receivedBytes: 8192, completedFiles: 21,
    requestedFileIndex: 21, requestedOffset: 12582912, exactReceiptAccepted: false,
    totalBytes: 33554432, totalFiles: 40, publicationNumber: 6, receiptPublicationNumber: null, ...patch });
  const group = state.worldPeers[0];
  group.state.replication.lastSuccessfulSyncUtc = now;
  state.sharingEnabled = true;
  let current, lastResult, lastFailure = null;
  if (testCase.sharingDelivery === 'sending') {
    current = progress({});
    lastResult = progress({ state: 'Pending', result: 'ByteBudget' });
  } else if (testCase.sharingDelivery === 'timeout-metadata') {
    current = progress({ phase: 'Metadata', operation: 'Metadata', httpStep: 'Challenge', submittedBytes: 128,
      receivedBytes: 0, completedFiles: 0, requestedFileIndex: null, requestedOffset: null });
    lastResult = progress({ state: 'Succeeded', phase: 'Metadata', operation: 'Metadata', result: 'NoPayload', httpStatus: 200 });
    lastFailure = progress({ state: 'Failed', result: 'Failed', error: 'Timeout', elapsedMilliseconds: 120000,
      startedUtc: new Date(Date.parse(now) - 180000).toISOString(),
      updatedUtc: new Date(Date.parse(now) - 60000).toISOString() });
  } else {
    current = progress({ state: 'Pending', phase: 'Offering', result: 'SeedPaused', exactReceiptAccepted: true,
      submittedBytes: 4096, receivedBytes: 4096, completedFiles: 0, receiptPublicationNumber: 5 });
    lastResult = progress({ state: 'Succeeded', phase: 'Receipt', operation: 'Receipt', result: 'KnownReceiptAccepted',
      exactReceiptAccepted: true, receiptPublicationNumber: 5 });
  }
  group.backgroundTransfers = [{ routeOrdinal: 1, recipientRole: 'OriginalHost', current, lastResult, lastFailure }];
}

export function assertSharingDeliveryEvidence(testCase, evidence) {
  if (!testCase.sharingDelivery) return;
  if (!evidence.visible || evidence.permissionsOpen || evidence.diagnosticsOpen || evidence.detailsOpen ||
      !/Background save transfer/u.test(evidence.text) || !/Original Host/u.test(evidence.text) ||
      !/This pass:/u.test(evidence.text) || /\b100\s*%/u.test(evidence.text) ||
      evidence.apiRequests.some(request => request.method !== 'GET'))
    throw new Error('Sharing delivery must be visible without permissions or diagnostics and must remain read-only.');
  const accepted = /Verified receipt for save \d+ accepted/u.test(evidence.text);
  if (testCase.sharingDelivery === 'sending' &&
      (!/Sending save files/u.test(evidence.text) || !/In progress/u.test(evidence.text) ||
       !/More bytes remain for another background pass/u.test(evidence.text) || accepted))
    throw new Error('Sending must retain its pending pass and cannot invent receipt confirmation.');
  if (testCase.sharingDelivery === 'timeout-metadata' &&
      (!/Checking world metadata/u.test(evidence.text) || !/Previous save transfer issue/u.test(evidence.text) ||
       !/did not respond in time/u.test(evidence.text) || accepted))
    throw new Error('Fresh metadata must preserve the previous payload timeout without confirming a receipt.');
  if (testCase.sharingDelivery === 'historical-receipt' &&
      (!/Verified receipt for save 5 accepted/u.test(evidence.text) || !/Save 6/u.test(evidence.text) ||
       /Verified receipt for save 6 accepted/u.test(evidence.text)))
    throw new Error('A historical receipt must name only its explicitly accepted save.');
}

export async function assertSharingDeliveryUi(page, testCase, record) {
  if (!testCase.sharingDelivery) return;
  const region = page.getByRole('region', { name: deliveryRegion, exact: true });
  const evidence = { visible: await region.isVisible(), text: await region.innerText(),
    permissionsOpen: await page.locator('.world-peer-permissions[open]').count() > 0,
    detailsOpen: await page.locator('.protection-sharing-panel details[open]').count() > 0,
    diagnosticsOpen: await region.locator('details[open]').count() > 0, apiRequests: record.apiRequests };
  assertSharingDeliveryEvidence(testCase, evidence);
  record.assertions.push({ sharingDelivery: testCase.sharingDelivery, visibleWithoutPermissions: true,
    allSharingDetailsClosed: true, transferDiagnosticsClosed: true, observationOnly: true, historicalReceiptOnly: true });
}
