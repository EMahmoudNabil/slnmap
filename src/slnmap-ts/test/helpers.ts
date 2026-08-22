import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

/** Writes a small temp project (files map: relative path -> source text) plus a tsconfig.json,
 * and returns its root directory. Used for unit tests that need a REAL ts.Program (import
 * resolution, path aliases, barrel re-exports) rather than a mocked one. */
export function createTempProject(files: Record<string, string>, tsconfigOverrides: object = {}): string {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'slnmap-ts-test-'));

  const tsconfig = {
    compilerOptions: {
      target: 'ES2020',
      module: 'ESNext',
      moduleResolution: 'Bundler',
      strict: true,
      esModuleInterop: true,
      baseUrl: '.',
      paths: { '@/*': ['src/*'] },
      ...(tsconfigOverrides as Record<string, unknown>),
    },
    include: ['src/**/*'],
  };
  fs.writeFileSync(path.join(root, 'tsconfig.json'), JSON.stringify(tsconfig, null, 2));

  for (const [relativePath, contents] of Object.entries(files)) {
    const fullPath = path.join(root, relativePath);
    fs.mkdirSync(path.dirname(fullPath), { recursive: true });
    fs.writeFileSync(fullPath, contents);
  }

  return root;
}

export function removeTempProject(root: string): void {
  fs.rmSync(root, { recursive: true, force: true });
}
