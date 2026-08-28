import type { CallSiteRecord, ExtractionArtifact } from './types.js';

/** Bump alongside package.json's version; kept separate so the emitted artifact records exactly
 * which extractor build produced it even if the schema itself hasn't changed. */
const PRODUCER_VERSION = '0.3.0';

export function buildArtifact(tsconfigRelative: string, callSites: CallSiteRecord[]): ExtractionArtifact {
  const resolvedCount = callSites.filter((c) => c.kind === 'FrontendCallSite').length;
  const unresolvedCount = callSites.length - resolvedCount;

  const byCategory: Record<string, number> = {};
  for (const c of callSites) {
    if (c.kind === 'UnresolvedCallSite') {
      byCategory[c.category] = (byCategory[c.category] ?? 0) + 1;
    }
  }

  const total = resolvedCount + unresolvedCount;
  const coveragePercent = total === 0 ? 100 : Math.round((resolvedCount / total) * 1000) / 10;

  return {
    schemaVersion: 2,
    producer: 'slnmap-ts',
    producerVersion: PRODUCER_VERSION,
    project: { root: '.', tsconfig: tsconfigRelative },
    stats: { resolvedCount, unresolvedCount, coveragePercent, byCategory },
    callSites,
  };
}
