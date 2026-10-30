#!/usr/bin/env node
// Validates a release manifest and prints the order it will run in.
//
//   node validate-manifest.mjs [--manifest <file>] [--rules-dir <dir>] [--summary <out.md>]
//                              [--environments test,staging,prod]
//
// Defaults suit a local run from this folder: ../releases/release.yml checked against
// ../apis/*/release-rules.yaml. The release pipeline points --rules-dir at the downloaded
// packages instead. Exits 1 if the manifest breaks any rule.

import { writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseArgs } from 'node:util';
import { formatPlan, formatSummary, loadManifest, loadRules, validate } from './manifest.mjs';

const here = dirname(fileURLToPath(import.meta.url));
const { values: args } = parseArgs({
  options: {
    manifest: { type: 'string', default: resolve(here, '../releases/release.yml') },
    'rules-dir': { type: 'string', default: resolve(here, '../apis') },
    summary: { type: 'string' },
    environments: { type: 'string' },
  },
});

const manifest = loadManifest(args.manifest, { environments: args.environments?.split(',') });
const { errors, plan } = validate(manifest, loadRules(args['rules-dir']));

if (errors.length) {
  console.error(`✗ ${args.manifest} breaks ${errors.length} rule(s):\n`);
  for (const e of errors) console.error(`  - ${e}\n`);
  // In Azure Pipelines, also raise each error as an annotation on the run.
  if (process.env.TF_BUILD) for (const e of errors) console.log(`##vso[task.logissue type=error]${e}`);
  process.exit(1);
}

console.log(`✓ ${args.manifest} is valid\n`);
console.log(formatPlan(manifest, plan));
if (args.summary) writeFileSync(args.summary, formatSummary(manifest, plan));
