import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, mkdirSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { deployOrder, loadManifests, validateAll } from '../lib/manifests.mjs';
import { toParameters } from '../render-params.mjs';

// Each test builds its own squads/ folder, so a fixture is just a map of folder -> YAML.
function squads(files) {
  const dir = mkdtempSync(join(tmpdir(), 'squads-'));
  for (const [folder, yaml] of Object.entries(files)) {
    mkdirSync(join(dir, folder));
    writeFileSync(join(dir, folder, 'workload.yaml'), yaml);
  }
  return validateAll(loadManifests(dir));
}

const manifest = (squad, capabilities = '', repo = `contoso/${squad}-service`) =>
  `apiVersion: platform/v1\nsquad: ${squad}\nrepository: ${repo}\ncapabilities:\n${capabilities}`;

const errorsFor = (results, folder) => results.find((r) => r.folder === folder).errors;

test('the real manifests in squads/ are valid', () => {
  const results = validateAll(loadManifests());
  assert.ok(results.length >= 2);
  for (const r of results) assert.deepEqual(r.errors, [], r.folder);
});

test('valid manifest gets schema defaults filled in', () => {
  const [r] = squads({ orders: manifest('orders', '  keyVault: true\n') });
  assert.deepEqual(r.errors, []);
  assert.deepEqual(r.manifest.capabilities.topics, []);
  assert.equal(r.manifest.capabilities.appConfig, false);
});

test('unknown capability is rejected', () => {
  const r = squads({ orders: manifest('orders', '  cosmosDb: true\n') });
  assert.match(errorsFor(r, 'orders').join(), /must NOT have additional properties/);
});

test('squad name must match its folder', () => {
  const r = squads({ orders: manifest('payments', '  keyVault: true\n') });
  assert.match(errorsFor(r, 'orders').join(), /does not match its folder/);
});

test('squad name too long for Key Vault naming is rejected', () => {
  const r = squads({ averyverylongsquad: manifest('averyverylongsquad', '  keyVault: true\n') });
  assert.match(errorsFor(r, 'averyverylongsquad').join(), /must match pattern/);
});

test('public expose without a path is rejected', () => {
  const r = squads({ orders: manifest('orders', '  expose: { public: true }\n') });
  assert.match(errorsFor(r, 'orders').join(), /must have required property 'path'/);
});

test('two squads claiming the same path both get the error', () => {
  const r = squads({
    orders: manifest('orders', '  expose: { public: true, path: /orders }\n'),
    returns: manifest('returns', '  expose: { public: true, path: /orders }\n'),
  });
  assert.match(errorsFor(r, 'orders').join(), /also claimed by: returns/);
  assert.match(errorsFor(r, 'returns').join(), /also claimed by: orders/);
});

test('subscribing to a topic nobody publishes is rejected', () => {
  const r = squads({
    orders: manifest('orders', '  topics: [order-placed]\n'),
    payments: manifest('payments', '  subscribe: [orders.order-cancelled]\n'),
  });
  assert.deepEqual(errorsFor(r, 'orders'), []);
  assert.match(errorsFor(r, 'payments').join(), /no squad publishes that topic/);
});

test('subscribing to a published topic is accepted', () => {
  const r = squads({
    orders: manifest('orders', '  topics: [order-placed]\n'),
    payments: manifest('payments', '  subscribe: [orders.order-placed]\n'),
  });
  for (const x of r) assert.deepEqual(x.errors, []);
});

test('invalid YAML is reported, not thrown', () => {
  const r = squads({ orders: 'squad: [unclosed' });
  assert.match(errorsFor(r, 'orders').join(), /not valid YAML/);
});

test('toParameters always emits a full expose object', () => {
  const [r] = squads({ orders: manifest('orders', '  storage: [invoices]\n') });
  const p = toParameters(r.manifest).parameters;
  assert.equal(p.squadName.value, 'orders');
  assert.deepEqual(p.capabilities.value.storageContainers, ['invoices']);
  assert.deepEqual(p.capabilities.value.expose, { public: false, path: '' });
});

test('deploy order puts a topic publisher before its subscribers', () => {
  const r = squads({
    alpha: manifest('alpha', '  subscribe: [zulu.thing-happened]\n'),
    zulu: manifest('zulu', '  topics: [thing-happened]\n'),
    mike: manifest('mike', '  keyVault: true\n'),
  });
  assert.deepEqual(deployOrder(r), ['zulu', 'alpha', 'mike']);
});

test('deploy order terminates on a subscription cycle', () => {
  const r = squads({
    aaa: manifest('aaa', '  topics: [a-done]\n  subscribe: [bbb.b-done]\n'),
    bbb: manifest('bbb', '  topics: [b-done]\n  subscribe: [aaa.a-done]\n'),
  });
  assert.equal(deployOrder(r).length, 2);
});
