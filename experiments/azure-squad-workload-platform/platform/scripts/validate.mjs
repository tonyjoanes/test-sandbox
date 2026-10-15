#!/usr/bin/env node
// Validates every squads/<squad>/workload.yaml. CI runs this on every PR, with no Azure
// access. Pass --squads-dir to point at a different folder (the tests use fixtures this way).
// Exit code 0 only if every manifest passed.

import { loadManifests, validateAll } from './lib/manifests.mjs';

const dirFlag = process.argv.indexOf('--squads-dir');
const squadsDir = dirFlag > -1 ? process.argv[dirFlag + 1] : undefined;

const results = validateAll(loadManifests(squadsDir));
if (results.length === 0) {
  console.error('No squads/*/workload.yaml files found.');
  process.exit(1);
}

for (const r of results) {
  console.log(`${r.errors.length ? 'FAIL' : '  OK'}  squads/${r.folder}/workload.yaml`);
  for (const e of r.errors) console.log(`        ${e}`);
}

const failed = results.filter((r) => r.errors.length).length;
console.log(`\n${results.length - failed}/${results.length} manifest(s) valid.`);
process.exit(failed ? 1 : 0);
