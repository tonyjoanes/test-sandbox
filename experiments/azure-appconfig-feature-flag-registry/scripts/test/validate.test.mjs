// Runs validate.mjs as a real child process against each fixture and checks the exit code and
// (for the cases where it matters) that the error message actually names the real problem.
// Black-box on purpose: this is testing the same interface CI invokes, not internal functions.

import { test } from 'node:test';
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const testDir = dirname(fileURLToPath(import.meta.url));
const validateScript = join(testDir, '..', 'validate.mjs');
const fixturesDir = join(testDir, 'fixtures');

function runValidator(fixtureName) {
  try {
    const stdout = execFileSync('node', [validateScript, join(fixturesDir, fixtureName)], {
      encoding: 'utf8',
    });
    return { exitCode: 0, stdout };
  } catch (err) {
    return { exitCode: err.status, stdout: err.stdout };
  }
}

test('a correctly-formed flag passes', () => {
  const { exitCode } = runValidator('valid.json');
  assert.equal(exitCode, 0);
});

test('a missing required field fails', () => {
  const { exitCode, stdout } = runValidator('missing-owner.json');
  assert.equal(exitCode, 1);
  assert.match(stdout, /owner/);
});

test('a name that is not kebab-case fails', () => {
  const { exitCode, stdout } = runValidator('bad-name-pattern.json');
  assert.equal(exitCode, 1);
  assert.match(stdout, /pattern/);
});

test('a name that does not match its filename fails, with a message the schema alone could not produce', () => {
  const { exitCode, stdout } = runValidator('name-mismatch.json');
  assert.equal(exitCode, 1);
  assert.match(stdout, /does not match filename/);
});

test('a missing environment fails', () => {
  const { exitCode, stdout } = runValidator('missing-environment.json');
  assert.equal(exitCode, 1);
  assert.match(stdout, /prod/);
});

test('an undeclared extra property fails, because additionalProperties is false', () => {
  const { exitCode, stdout } = runValidator('extra-property.json');
  assert.equal(exitCode, 1);
  assert.match(stdout, /additional|jiraTicket/);
});

test('an invalid email for owner fails', () => {
  const { exitCode, stdout } = runValidator('bad-email.json');
  assert.equal(exitCode, 1);
  assert.match(stdout, /email|format/);
});

test('an expired flag warns but does not fail validation', () => {
  const { exitCode, stdout } = runValidator('expired.json');
  assert.equal(exitCode, 0);
  assert.match(stdout, /in the past/);
});
