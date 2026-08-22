import fs from 'node:fs';
import path from 'node:path';
import ts from 'typescript';

/** Raised for a missing/broken tsconfig — caught at the CLI boundary and reported without a
 * stack trace, in the spirit of slnmap's own CLI error conventions (corrective, actionable). */
export class TsConfigError extends Error {}

export interface LoadedProgram {
  program: ts.Program;
  checker: ts.TypeChecker;
  /** Resolved, absolute project root. */
  projectRoot: string;
  /** True for a file path that belongs to the project (excludes node_modules). */
  inProject: (fileName: string) => boolean;
  /** Project-root-relative, forward-slash-normalized path. */
  relativePath: (fileName: string) => string;
}

function normalize(filePath: string): string {
  return path.resolve(filePath).replace(/\\/g, '/');
}

/**
 * Loads a TypeScript program from a project's own tsconfig.json — path aliases, project
 * references, and `moduleResolution` all honored exactly as `tsc` would (investigation §Q1.1,
 * Part 2.1). Fails with a clean, actionable TsConfigError for a missing or invalid tsconfig; the
 * CLI never surfaces a raw TypeScript diagnostic dump or a stack trace to the user.
 */
export function loadProgram(projectRoot: string, tsconfigPath: string): LoadedProgram {
  const absRoot = path.resolve(projectRoot);
  const absConfig = path.resolve(tsconfigPath);

  if (!fs.existsSync(absRoot) || !fs.statSync(absRoot).isDirectory()) {
    throw new TsConfigError(`Project root not found or not a directory: ${absRoot}`);
  }

  if (!fs.existsSync(absConfig)) {
    throw new TsConfigError(`tsconfig not found: ${absConfig}`);
  }

  // TypeScript's own internal diagnostics machinery asserts that a path it is given round-trips
  // through its normalizer unchanged — a raw Windows backslash path fails that assertion with an
  // opaque "Debug Failure" instead of a normal diagnostic. Normalize once, here, before it ever
  // reaches a `ts.*` API.
  const normalizedConfig = normalize(absConfig);

  const configFile = ts.readConfigFile(normalizedConfig, ts.sys.readFile);
  if (configFile.error) {
    throw new TsConfigError(
      `Failed to parse ${absConfig}: ${ts.flattenDiagnosticMessageText(configFile.error.messageText, '\n')}`,
    );
  }

  const parsed = ts.parseJsonConfigFileContent(configFile.config, ts.sys, path.dirname(normalizedConfig));
  if (parsed.errors.length > 0) {
    const messages = parsed.errors
      .map((error) => ts.flattenDiagnosticMessageText(error.messageText, '\n'))
      .join('\n');
    throw new TsConfigError(`Invalid tsconfig at ${absConfig}:\n${messages}`);
  }

  if (parsed.fileNames.length === 0) {
    throw new TsConfigError(
      `No source files resolved from ${absConfig} — check its "include"/"files" entries.`,
    );
  }

  const program = ts.createProgram({
    rootNames: parsed.fileNames,
    options: parsed.options,
    projectReferences: parsed.projectReferences,
  });
  const checker = program.getTypeChecker();

  const rootPrefix = normalize(absRoot) + '/';
  const inProject = (fileName: string): boolean => {
    const n = normalize(fileName);
    return n.startsWith(rootPrefix) && !n.includes('/node_modules/');
  };
  const relativePath = (fileName: string): string => normalize(fileName).slice(rootPrefix.length);

  return { program, checker, projectRoot: absRoot, inProject, relativePath };
}
