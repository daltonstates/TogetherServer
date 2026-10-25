// Pure identity checks shared by the hosted renderer and code-only tests.
export function uiReviewBuild(indexSha256, verificationRevision, context = undefined) {
  const revision = value => typeof value === 'string' && /^[0-9a-f]{40}$/u.test(value);
  const hash = value => typeof value === 'string' && /^[0-9a-f]{64}$/u.test(value);
  const invalid = () => { throw new Error('Candidate UI provenance is invalid or differs from the rendered index.'); };
  if (!hash(indexSha256) || verificationRevision !== null && !revision(verificationRevision)) invalid();
  if (context === undefined) return { indexSha256, sourceRevision: verificationRevision, verificationRevision };
  if (!context || typeof context !== 'object' || Array.isArray(context) ||
      !revision(context.sourceRevision) || !revision(verificationRevision) ||
      context.verificationRevision !== verificationRevision ||
      typeof context.candidateRunId !== 'string' || !/^[1-9][0-9]*$/u.test(context.candidateRunId) ||
      !hash(context.candidateSha256) || context.rebuilt !== false || context.appLaunched !== false ||
      context.fullyAccepted !== false || !Array.isArray(context.uiAssets) ||
      context.uiAssets.length < 1 || context.uiAssets.length > 64) invalid();
  const names = new Set();
  for (const asset of context.uiAssets) {
    if (!asset || typeof asset !== 'object' || Array.isArray(asset) || typeof asset.asset !== 'string' ||
        !/^[a-zA-Z0-9_.\/-]+$/u.test(asset.asset) || asset.asset.startsWith('/') ||
        asset.asset.split('/').some(part => !part || part === '.' || part === '..') ||
        names.has(asset.asset.toLowerCase()) || !hash(asset.sha256) ||
        !Number.isSafeInteger(asset.sizeBytes) || asset.sizeBytes < 0) invalid();
    names.add(asset.asset.toLowerCase());
  }
  const index = context.uiAssets.filter(asset => asset.asset === 'index.html');
  if (index.length !== 1 || index[0].sha256 !== indexSha256) invalid();
  return { indexSha256, sourceRevision: context.sourceRevision, verificationRevision,
    candidateRunId: context.candidateRunId, candidateSha256: context.candidateSha256 };
}
