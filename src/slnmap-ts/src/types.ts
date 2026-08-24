/**
 * The JSON artifact schema this package emits. Node/edge identity (fqn, span offsets, ingestion
 * into the slnmap graph) is Task B's concern — this package never touches SQLite (investigation
 * §Q1: JSON artifact architecture). Schema per reports/ts-extractor-investigation.md §Q1/§Q2,
 * extended here with a `stats.byCategory` breakdown (Task A Part 2.6).
 */

/** The seven closed unresolved categories — investigation §Q3.3, plus `string-concatenation`
 * (v0.12.2, foreign-patterns-trial finding #3: a `+`-built URL with a non-constant operand was
 * previously silently dropped instead of disclosed — see resolve.ts). Nothing falls through
 * uncategorized. */
export type UnresolvedCategory =
  | 'dynamic-base-url'
  | 'runtime-computed-segment'
  | 'non-constant-identifier'
  | 'unrecognized-callee'
  | 'dynamic-import-or-indirection'
  | 'resolution-depth-exceeded'
  | 'string-concatenation';

/** The four resolution tiers from the spike's classification (§Q3.2) — derivable from the
 * template string alone (presence/count of `{*}`), but recorded for readability and for the
 * acceptance-run breakdown in Part 4. */
export type ResolutionTier = 'literal' | 'const-resolved' | 'template-folded' | 'template-param-holes';

export interface ResolvedCallSite {
  kind: 'FrontendCallSite';
  verb: string;
  template: string;
  resolutionTier: ResolutionTier;
  file: string;
  line: number;
  column: number;
  /** Character offsets from `ts.Node.getStart()`/`.getEnd()` — the same units as Roslyn's
   * `TextSpan`, so ingestion needs no reinterpretation for the `span_start`/`span_end` DB columns
   * (schemaVersion 2, Task B Part 0). */
  spanStart: number;
  spanEnd: number;
}

export interface UnresolvedCallSiteRecord {
  kind: 'UnresolvedCallSite';
  verb: string;
  category: UnresolvedCategory;
  reason: string;
  file: string;
  line: number;
  column: number;
  spanStart: number;
  spanEnd: number;
}

export type CallSiteRecord = ResolvedCallSite | UnresolvedCallSiteRecord;

export interface ExtractionStats {
  resolvedCount: number;
  unresolvedCount: number;
  coveragePercent: number;
  byCategory: Record<string, number>;
}

export interface ExtractionArtifact {
  schemaVersion: 2;
  producer: 'slnmap-ts';
  producerVersion: string;
  project: {
    root: string;
    tsconfig: string;
  };
  stats: ExtractionStats;
  callSites: CallSiteRecord[];
}
