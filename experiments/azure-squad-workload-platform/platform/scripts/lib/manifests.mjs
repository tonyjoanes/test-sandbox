// Shared by validate.mjs and render-params.mjs: load every squads/<squad>/workload.yaml, check
// each one against the schema, then run the checks a schema can't express because they need
// to see more than one file (or the file's location).

import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { basename, dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import Ajv2020 from 'ajv/dist/2020.js';
import { parse } from 'yaml';

const here = dirname(fileURLToPath(import.meta.url));
export const platformDir = join(here, '..', '..');
export const defaultSquadsDir = join(platformDir, 'squads');

const schema = JSON.parse(readFileSync(join(platformDir, 'schema', 'workload.schema.json'), 'utf8'));
// useDefaults fills in the schema's `default`s, so render-params.mjs always sees every
// capability key (keyVault: false rather than missing) and Bicep never has to guess.
const ajv = new Ajv2020({ allErrors: true, strict: true, useDefaults: true });
const validateSchema = ajv.compile(schema);

export function loadManifests(squadsDir = defaultSquadsDir) {
  return readdirSync(squadsDir, { withFileTypes: true })
    .filter((d) => d.isDirectory())
    .map((d) => join(squadsDir, d.name, 'workload.yaml'))
    .filter((file) => existsSync(file))
    .map((file) => {
      const folder = basename(dirname(file));
      try {
        return { file, folder, manifest: parse(readFileSync(file, 'utf8')) };
      } catch (err) {
        return { file, folder, manifest: undefined, parseError: err.message };
      }
    });
}

// Returns one result per manifest: { folder, file, manifest, errors[] }. A manifest is only
// deployable if its own errors are empty; a cross-file error is reported on every file
// involved, so whichever squad's PR introduced the clash sees it on their own file.
export function validateAll(entries) {
  const results = entries.map((e) => ({ ...e, errors: [] }));

  for (const r of results) {
    if (r.parseError) {
      r.errors.push(`not valid YAML: ${r.parseError}`);
      continue;
    }
    if (!validateSchema(r.manifest)) {
      for (const e of validateSchema.errors) r.errors.push(`${e.instancePath || '(root)'} ${e.message}`);
      continue;
    }
    if (r.manifest.squad !== r.folder) {
      r.errors.push(`squad "${r.manifest.squad}" does not match its folder "squads/${r.folder}/" — CODEOWNERS is per folder, so this would let one squad edit another's resources`);
    }
  }

  const valid = results.filter((r) => r.errors.length === 0);

  // Front Door routes and APIM API paths share one namespace across every squad.
  const byPath = new Map();
  for (const r of valid) {
    const expose = r.manifest.capabilities.expose;
    if (expose?.public) byPath.set(expose.path, [...(byPath.get(expose.path) ?? []), r]);
  }
  for (const [path, owners] of byPath) {
    if (owners.length > 1) {
      for (const r of owners) r.errors.push(`expose.path "${path}" is also claimed by: ${owners.filter((o) => o !== r).map((o) => o.folder).join(', ')}`);
    }
  }

  // A subscription to a topic nobody publishes would deploy fine and then fail at runtime.
  const published = new Set(valid.flatMap((r) => r.manifest.capabilities.topics.map((t) => `${r.manifest.squad}.${t}`)));
  for (const r of valid) {
    for (const topic of r.manifest.capabilities.subscribe) {
      if (!published.has(topic)) r.errors.push(`subscribe "${topic}" — no squad publishes that topic (published: ${[...published].join(', ') || 'none'})`);
    }
  }

  return results;
}

// Publishers before subscribers: a subscription can only be created on a topic that already
// exists, so a squad that consumes another squad's topic deploys after it. Ties keep
// alphabetical order so the plan is stable between runs. Cycles are fine (A subscribes to B's
// topic and B to A's) on every run after the first; on the first, the second squad's deploy
// creates what the first one was waiting for, and a re-run converges.
export function deployOrder(results) {
  const publisherOf = new Map();
  for (const r of results) for (const t of r.manifest.capabilities.topics) publisherOf.set(`${r.manifest.squad}.${t}`, r.folder);

  const ordered = [];
  const visiting = new Set();
  const byFolder = new Map(results.map((r) => [r.folder, r]));
  const visit = (folder) => {
    if (ordered.includes(folder) || visiting.has(folder)) return;
    visiting.add(folder);
    for (const topic of byFolder.get(folder).manifest.capabilities.subscribe) {
      const publisher = publisherOf.get(topic);
      if (publisher && publisher !== folder) visit(publisher);
    }
    ordered.push(folder);
  };
  for (const folder of [...byFolder.keys()].sort()) visit(folder);
  return ordered;
}
