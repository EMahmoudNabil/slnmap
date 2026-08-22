import assert from 'node:assert/strict';
import { after, test } from 'node:test';
import fs from 'node:fs';
import path from 'node:path';
import { extract } from '../src/index.js';
import { TsConfigError } from '../src/program.js';
import { createTempProject, removeTempProject } from './helpers.js';

/** Program-load failure modes (Task A Part 2.1): clean, actionable, no raw TS diagnostic dumps
 * or stack traces bubbling to a caller that only wants a message. */

const cleanups: string[] = [];
after(() => {
  for (const root of cleanups) {
    removeTempProject(root);
  }
});

test('missing tsconfig throws TsConfigError with an actionable message', () => {
  const root = createTempProject({ 'src/index.ts': 'export {};' });
  cleanups.push(root);

  assert.throws(
    () => extract({ projectRoot: root, tsconfigPath: path.join(root, 'does-not-exist.json') }),
    (error: unknown) => error instanceof TsConfigError && /tsconfig not found/.test(error.message),
  );
});

test('broken tsconfig JSON throws TsConfigError, not a raw parse exception', () => {
  const root = createTempProject({ 'src/index.ts': 'export {};' });
  cleanups.push(root);
  fs.writeFileSync(path.join(root, 'tsconfig.json'), '{ this is not valid json');

  assert.throws(
    () => extract({ projectRoot: root }),
    (error: unknown) => error instanceof TsConfigError,
  );
});

test('a project root that does not exist throws TsConfigError', () => {
  assert.throws(
    () => extract({ projectRoot: '/definitely/does/not/exist/anywhere' }),
    (error: unknown) => error instanceof TsConfigError && /not found/.test(error.message),
  );
});
