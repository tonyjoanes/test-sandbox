import assert from 'node:assert/strict';
import { dirname, resolve } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { compareVersions, formatPlan, loadManifest, loadRules, validate } from '../manifest.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const root = resolve(here, '../..');
const rules = loadRules(resolve(root, 'apis'));
const realManifest = () => loadManifest(resolve(root, 'releases/release.yml'));
const fixture = (name) => loadManifest(resolve(here, 'fixtures', name));

// Runs validate() on the real manifest after `change` has edited a copy of its steps.
function validateWith(change) {
  const manifest = realManifest();
  manifest.steps = structuredClone(manifest.steps);
  change(manifest.steps, manifest);
  return validate(manifest, rules);
}
const step = (steps, id) => steps.find((s) => s.id === id);
const errorsMatching = (result, pattern) => result.errors.filter((e) => pattern.test(e));

test('the real manifest is valid and runs in the expected waves', () => {
  const manifest = realManifest();
  const { errors, plan } = validate(manifest, rules);
  assert.deepEqual(errors, []);
  assert.deepEqual(plan.map((wave) => wave.map((s) => s.id)), [
    ['deploy_identity', 'deploy_catalog'],
    ['deploy_customers'],
    ['backfill_billing_profiles'],
    ['deploy_payments'],
    ['deploy_orders'],
    ['checkout_smoke_test'],
  ]);
  assert.match(formatPlan(manifest, plan), /PAUSE: Run the billing-profile backfill \(staging, prod\)/);
});

test('environment names come from the release template default', () => {
  assert.deepEqual(realManifest().environments, ['test', 'staging', 'prod']);
});

test('B deployed without depending on A is rejected, with the fix in the message', () => {
  const { errors } = validate(fixture('payments-before-customers.yml'), rules);
  assert.equal(errors.length, 1);
  assert.match(errors[0], /payments-api must be deployed after customers-api/);
  assert.match(errors[0], /billing-profile/);   // the rule's reason travels with the error
  assert.match(errors[0], /add deploy_customers to deploy_payments's dependsOn/);
});

test('a dependency reached through another step (here, a manual step) counts', () => {
  // deploy_payments -> backfill_billing_profiles -> deploy_customers in the real manifest.
  assert.equal(step(realManifest().steps, 'deploy_payments').dependsOn[0], 'backfill_billing_profiles');
  assert.deepEqual(validate(realManifest(), rules).errors, []);
});

test('a dependency pinned below its minVersion is rejected even when the order is right', () => {
  const { errors } = validate(fixture('customers-too-old.yml'), rules);
  assert.equal(errors.length, 1);
  assert.match(errors[0], /payments-api 2\.3\.1 needs customers-api >= 1\.2\.0, but this release pins customers-api 1\.1\.4/);
});

test('versions compare numerically, not as strings', () => {
  assert.ok(compareVersions('1.10.0', '1.9.0') > 0);
  assert.equal(compareVersions('2.3.0', '2.3.0'), 0);
  const result = validateWith((s) => { step(s, 'deploy_catalog').version = '10.0.0'; });
  assert.deepEqual(result.errors, []);
});

test('every API must be listed', () => {
  const result = validateWith((s) => {
    s.splice(s.findIndex((x) => x.id === 'deploy_orders'), 1);
    step(s, 'checkout_smoke_test').dependsOn = ['deploy_payments'];
  });
  assert.equal(errorsMatching(result, /orders-api is missing/).length, 1);
});

test('an API listed twice, or an unknown API, is rejected', () => {
  const twice = validateWith((s) => s.push({ id: 'deploy_orders_again', deploy: 'orders-api', version: '1.8.1' }));
  assert.equal(errorsMatching(twice, /already deployed by step "deploy_orders"/).length, 1);
  const unknown = validateWith((s) => s.push({ id: 'deploy_search', deploy: 'search-api', version: '1.0.0' }));
  assert.equal(errorsMatching(unknown, /unknown API "search-api"/).length, 1);
});

test('versions must be exact', () => {
  const result = validateWith((s) => { step(s, 'deploy_orders').version = 'latest'; });
  assert.equal(errorsMatching(result, /exact package version/).length, 1);
});

test('a typo in a key is an error, not silently ignored', () => {
  const result = validateWith((s) => {
    const payments = step(s, 'deploy_payments');
    payments.depends_on = payments.dependsOn;
    delete payments.dependsOn;
  });
  assert.equal(errorsMatching(result, /unknown key `depends_on`/).length, 1);
});

test('cycles are rejected and named', () => {
  const result = validateWith((s) => { step(s, 'deploy_identity').dependsOn = ['deploy_orders']; });
  const [cycle] = errorsMatching(result, /cycle/);
  assert.match(cycle, /deploy_identity → deploy_orders → .* → deploy_identity/);
});

test('dependsOn must point at real steps', () => {
  const result = validateWith((s) => { step(s, 'deploy_orders').dependsOn.push('deploy_paymnets'); });
  assert.equal(errorsMatching(result, /dependsOn "deploy_paymnets", but there's no step with that id/).length, 1);
});

test('ids must be unique and usable as job names', () => {
  const dup = validateWith((s) => { step(s, 'deploy_catalog').id = 'deploy_identity'; });
  assert.equal(errorsMatching(dup, /duplicate id "deploy_identity"/).length, 1);
  const bad = validateWith((s) => { step(s, 'deploy_catalog').id = 'deploy-catalog'; });
  assert.equal(errorsMatching(bad, /letters, digits and _/).length, 1);
});

test('manual steps need instructions and real environment names', () => {
  const noInstructions = validateWith((s) => { delete step(s, 'checkout_smoke_test').instructions; });
  assert.equal(errorsMatching(noInstructions, /need `instructions`/).length, 1);
  const badEnv = validateWith((s) => { step(s, 'checkout_smoke_test').environments = ['production']; });
  assert.equal(errorsMatching(badEnv, /unknown environment "production"/).length, 1);
});

test('a step must be deploy or manual, not both', () => {
  const result = validateWith((s) => { step(s, 'deploy_orders').manual = 'Also do a thing'; });
  assert.equal(errorsMatching(result, /exactly one of `deploy`.*or `manual`/).length, 1);
});
