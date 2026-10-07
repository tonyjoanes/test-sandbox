#!/usr/bin/env node
// Applies the registry's declared state to one Azure App Configuration store, per environment.
// Default is --dry-run: prints exactly what would run, calls nothing — no Azure credentials
// needed. Only --apply actually shells out to `az`. The dry-run path is the one thing in this
// whole experiment a reader can run with zero setup and still see real output; see
// ../README.md's demo walkthrough.
//
// Despite "runs `az appconfig feature set` ... passing the state" in the brief: `feature set`
// creates/updates a flag's metadata (its description) but does not itself toggle enabled vs
// disabled — the actual state change is `az appconfig feature enable` / `feature disable`.
// This script does both: `feature set` first so the flag exists with an up-to-date
// description, then enable/disable to match the JSON's declared boolean for that environment.

import { readFileSync, readdirSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

const scriptDir = dirname(fileURLToPath(import.meta.url));
const flagsDir = join(scriptDir, '..', 'flags');

function parseArgs(argv) {
  const args = { environment: null, apply: false };
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === '--environment') args.environment = argv[++i];
    else if (argv[i] === '--apply') args.apply = true;
    else if (argv[i] === '--dry-run') args.apply = false;
    else throw new Error(`unrecognized argument: ${argv[i]}`);
  }
  if (!['dev', 'staging', 'prod'].includes(args.environment)) {
    throw new Error('--environment must be one of: dev, staging, prod');
  }
  return args;
}

function loadFlags() {
  return readdirSync(flagsDir)
    .filter((f) => f.endsWith('.json') && f !== 'schema.json')
    .map((f) => JSON.parse(readFileSync(join(flagsDir, f), 'utf8')));
}

function run(cmd, args, apply) {
  const rendered = `${cmd} ${args.map((a) => (a.includes(' ') ? `"${a}"` : a)).join(' ')}`;
  if (!apply) {
    console.log(`  [dry-run] ${rendered}`);
    return { ok: true };
  }
  console.log(`  [apply]   ${rendered}`);
  const result = spawnSync(cmd, args, { stdio: 'inherit' });
  return { ok: result.status === 0 };
}

function main() {
  const args = parseArgs(process.argv.slice(2));
  const storeEnvVar = `APPCONFIG_STORE_${args.environment.toUpperCase()}`;
  const storeName = process.env[storeEnvVar];
  if (!storeName) {
    console.log(`${storeEnvVar} is not set — nothing to target. In CI this means the environment's store isn't configured yet: skip, don't fail (see README's "Running This For Real").`);
    process.exit(0);
  }

  const flags = loadFlags();
  console.log(`Target: ${args.environment} (store: ${storeName}) — ${args.apply ? 'APPLY' : 'DRY RUN'}`);
  console.log('');

  let anyFailed = false;
  for (const flag of flags) {
    const desired = flag.environments[args.environment];
    console.log(`${flag.name} -> ${desired ? 'ON' : 'OFF'}`);

    const setResult = run('az', [
      'appconfig', 'feature', 'set',
      '--name', storeName,
      '--feature', flag.name,
      '--description', flag.description,
      '--yes',
    ], args.apply);

    const toggleResult = run('az', [
      'appconfig', 'feature', desired ? 'enable' : 'disable',
      '--name', storeName,
      '--feature', flag.name,
      '--yes',
    ], args.apply);

    if (!setResult.ok || !toggleResult.ok) anyFailed = true;
  }

  console.log('');
  console.log(anyFailed ? 'One or more flags failed to apply.' : `${args.apply ? 'Applied' : 'Dry-run complete for'} ${flags.length} flag(s).`);
  process.exit(anyFailed ? 1 : 0);
}

main();
