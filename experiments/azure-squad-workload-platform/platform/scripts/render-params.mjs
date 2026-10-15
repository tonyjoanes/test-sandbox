#!/usr/bin/env node
// Turns one squad's manifest into an ARM parameters file for platform/squad/main.bicep:
//
//   node render-params.mjs --squad orders > orders.parameters.json
//   az deployment sub create --template-file ../squad/main.bicep --parameters @orders.parameters.json
//
//   node render-params.mjs --list    # every squad, in the order they must be deployed
//
// Refuses to render if *any* manifest is invalid, not only the requested one: a broken
// cross-squad reference (e.g. a subscription to a topic that was just removed) would
// otherwise deploy half a change.

import { deployOrder, loadManifests, validateAll } from './lib/manifests.mjs';

export function toParameters(manifest) {
  const c = manifest.capabilities;
  return {
    $schema: 'https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#',
    contentVersion: '1.0.0.0',
    parameters: {
      squadName: { value: manifest.squad },
      repository: { value: manifest.repository },
      capabilities: {
        value: {
          keyVault: c.keyVault,
          appConfig: c.appConfig,
          storageContainers: c.storage,
          topics: c.topics,
          subscribe: c.subscribe,
          // Bicep gets an explicit object either way, so the template never has to cope with
          // a missing `expose`. A non-public squad gets an empty path.
          expose: { public: c.expose?.public ?? false, path: c.expose?.public ? c.expose.path : '' },
        },
      },
    },
  };
}

function arg(name) {
  const i = process.argv.indexOf(name);
  return i > -1 ? process.argv[i + 1] : undefined;
}

if (import.meta.url === `file://${process.argv[1]}`) {
  const squad = arg('--squad');
  const list = process.argv.includes('--list');
  if (!squad && !list) {
    console.error('usage: render-params.mjs (--squad <name> | --list) [--squads-dir <dir>]');
    process.exit(2);
  }
  const results = validateAll(loadManifests(arg('--squads-dir')));
  const invalid = results.filter((r) => r.errors.length);
  if (invalid.length) {
    console.error(`Refusing to render: ${invalid.map((r) => r.folder).join(', ')} invalid. Run validate.mjs for details.`);
    process.exit(1);
  }
  if (list) {
    // JSON so a workflow can feed it straight into a matrix or a loop.
    process.stdout.write(JSON.stringify(deployOrder(results)) + '\n');
    process.exit(0);
  }
  const target = results.find((r) => r.folder === squad);
  if (!target) {
    console.error(`No manifest at squads/${squad}/workload.yaml`);
    process.exit(1);
  }
  process.stdout.write(JSON.stringify(toParameters(target.manifest), null, 2) + '\n');
}
