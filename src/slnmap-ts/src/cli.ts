#!/usr/bin/env node
import fs from 'node:fs';
import path from 'node:path';
import { extract, TsConfigError } from './index.js';

function printUsage(): void {
  console.error('Usage: slnmap-ts extract <project-root> --tsconfig <path> --out <file>.json');
}

function main(argv: string[]): void {
  const [command, ...rest] = argv;
  if (command !== 'extract') {
    printUsage();
    process.exit(1);
  }

  let tsconfigPath: string | undefined;
  let outPath: string | undefined;
  const positionals: string[] = [];

  for (let i = 0; i < rest.length; i++) {
    const arg = rest[i]!;
    if (arg === '--tsconfig') {
      tsconfigPath = rest[++i];
    } else if (arg === '--out') {
      outPath = rest[++i];
    } else if (!arg.startsWith('-')) {
      positionals.push(arg);
    } else {
      console.error(`slnmap-ts: unknown option '${arg}'`);
      printUsage();
      process.exit(1);
    }
  }

  const projectRoot = positionals[0];
  if (!projectRoot) {
    console.error('slnmap-ts: missing <project-root>.');
    printUsage();
    process.exit(1);
  }
  if (!outPath) {
    console.error('slnmap-ts: missing required --out <file>.json.');
    printUsage();
    process.exit(1);
  }

  try {
    const artifact = extract({ projectRoot, tsconfigPath });
    const resolvedOut = path.resolve(outPath);
    fs.mkdirSync(path.dirname(resolvedOut), { recursive: true });
    fs.writeFileSync(resolvedOut, `${JSON.stringify(artifact, null, 2)}\n`, 'utf8');
    console.error(
      `slnmap-ts: ${artifact.stats.resolvedCount} resolved, ${artifact.stats.unresolvedCount} unresolved ` +
        `(${artifact.stats.coveragePercent}% coverage) -> ${resolvedOut}`,
    );
  } catch (error) {
    if (error instanceof TsConfigError) {
      console.error(`slnmap-ts: ${error.message}`);
      process.exit(1);
    }
    throw error;
  }
}

main(process.argv.slice(2));
