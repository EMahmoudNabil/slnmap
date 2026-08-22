import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { test } from 'node:test';
import { extract } from '../src/index.js';

// Node 18 has no import.meta.dirname; derive it the portable way.
const here = path.dirname(fileURLToPath(import.meta.url));
const FIXTURE_ROOT = path.resolve(here, '..', '..', '..', '..', 'tests', 'fixtures-ts', 'frontend-fixture');
const EXPECTED_PATH = path.join(FIXTURE_ROOT, 'expected-callsites.json');

test('extract() against the investigation fixture matches the golden expected-callsites.json exactly', () => {
  const expected = JSON.parse(fs.readFileSync(EXPECTED_PATH, 'utf8'));
  const actual = extract({ projectRoot: FIXTURE_ROOT });

  // producerVersion is allowed to drift across releases; everything else must match exactly,
  // including call-site ORDER (the determinism contract sorts by file/line/column).
  assert.equal(actual.schemaVersion, expected.schemaVersion);
  assert.equal(actual.producer, expected.producer);
  assert.deepEqual(actual.stats, expected.stats);
  assert.deepEqual(actual.callSites, expected.callSites);
});

test('extraction is deterministic: two runs on unchanged input produce byte-identical call sites', () => {
  const first = extract({ projectRoot: FIXTURE_ROOT });
  const second = extract({ projectRoot: FIXTURE_ROOT });
  assert.deepEqual(first.callSites, second.callSites);
  assert.deepEqual(first.stats, second.stats);
});

test('the 5 resolved templates match RouteTemplateCrossStackSpecTests.cs\'s oracle exactly', () => {
  // Byte-identical templates are the CRITICAL requirement (Task A Part 2.5): if this ever
  // mismatches, the fix is here or in the investigation's design, never in the C# oracle.
  const artifact = extract({ projectRoot: FIXTURE_ROOT });
  const resolved = artifact.callSites.filter((c) => c.kind === 'FrontendCallSite') as Array<{
    verb: string;
    template: string;
  }>;
  const byTemplate = new Set(resolved.map((c) => `${c.verb} ${c.template}`));

  assert.ok(byTemplate.has('GET /UserTasks/assigned-tasks-with-summary'), 'Pair 1 (Case A)');
  assert.ok(byTemplate.has('GET /Committees'), 'Pair 2 (Case C)');
  assert.ok(byTemplate.has('POST /TaskCenter/{*}/{*}/reminder'), 'Pair 3 (Case B fan-out)');
  assert.ok(byTemplate.has('GET /UserProfiles/current'), 'Pair 4 (param-vs-literal siblings)');
  assert.ok(byTemplate.has('POST /organizationusers'), 'Pair 5 (the dangling bug)');
  assert.equal(resolved.length, 5);
});
