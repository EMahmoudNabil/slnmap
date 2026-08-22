import path from 'node:path';
import { loadProgram } from './program.js';
import { extractCallSites } from './walk.js';
import { buildArtifact } from './artifact.js';
import type { ExtractionArtifact } from './types.js';

export { TsConfigError } from './program.js';
export type { CallSiteRecord, ExtractionArtifact, ResolutionTier, UnresolvedCategory } from './types.js';

export interface ExtractOptions {
  /** The frontend project's root directory. */
  projectRoot: string;
  /** Defaults to `<projectRoot>/tsconfig.json`. */
  tsconfigPath?: string;
}

/** Programmatic entry point: loads the program, walks it, and returns the JSON artifact object
 * (the CLI is a thin wrapper that serializes this to a file — investigation §Q1). */
export function extract(options: ExtractOptions): ExtractionArtifact {
  const tsconfigPath = options.tsconfigPath ?? path.join(options.projectRoot, 'tsconfig.json');
  const loaded = loadProgram(options.projectRoot, tsconfigPath);
  const callSites = extractCallSites(loaded);
  const tsconfigRelative =
    path.relative(loaded.projectRoot, path.resolve(tsconfigPath)).replace(/\\/g, '/') || 'tsconfig.json';
  return buildArtifact(tsconfigRelative, callSites);
}
