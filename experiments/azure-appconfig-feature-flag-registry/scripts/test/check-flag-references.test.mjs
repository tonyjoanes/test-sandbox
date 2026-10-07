// Runs check-flag-references.mjs as a real child process against fixture app/registry pairs
// and checks the exit code — same black-box approach as validate.test.mjs, testing the
// interface CI actually invokes.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const testDir = dirname(fileURLToPath(import.meta.url));
const checkScript = join(testDir, '..', 'check-flag-references.mjs');
const fixturesDir = join(testDir, '..', 'fixtures', 'app-check');

function runCheck(appDirName, registryDirName) {
  try {
    const stdout = execFileSync(
      'node',
      [checkScript, join(fixturesDir, appDirName), join(fixturesDir, registryDirName)],
      { encoding: 'utf8' },
    );
    return { exitCode: 0, stdout };
  } catch (err) {
    return { exitCode: err.status, stdout: err.stdout };
  }
}

test('a flag referenced in code that exists in the registry passes', () => {
  const { exitCode, stdout } = runCheck('app-good', 'registry');
  assert.equal(exitCode, 0);
  assert.match(stdout, /OK\s+known-flag/);
});

test('a flag referenced in code that is NOT in the registry fails the build', () => {
  const { exitCode, stdout } = runCheck('app-bad', 'registry');
  assert.equal(exitCode, 1);
  assert.match(stdout, /FAIL\s+typo-flag/);
  assert.match(stdout, /no typo-flag\.json in the registry/);
});

test('a registered flag never referenced in code warns but does not fail', () => {
  const { exitCode, stdout } = runCheck('app-good', 'registry-with-unused');
  assert.equal(exitCode, 0);
  assert.match(stdout, /orphaned-flag is in the registry but not referenced/);
});
