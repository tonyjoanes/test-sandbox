// Release manifest validation. validate-manifest.mjs is the command-line wrapper around this.
//
// A manifest is an Azure DevOps pipeline file that `extends` pipelines/templates/release.yml.
// The pipeline only types `steps` as an object, so this file holds the actual rules:
//   1. Shape: unique job-safe ids, each step is exactly one of deploy/manual, no unknown keys.
//      Unknown keys matter: Azure DevOps would silently ignore a typo like `depends_on`, and
//      the release would run in the wrong order.
//   2. Completeness: every known API appears exactly once, with an exact x.y.z version.
//   3. Graph: every dependsOn points at a real step, and there are no cycles.
//   4. Release rules: for every API, each entry in its release-rules.yaml `deployAfter` must be
//      met. The other API's version must be at least `minVersion`, and its deploy step must
//      come BEFORE this one (directly or through other steps) in the dependsOn graph.

import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { parse } from 'yaml';

const STEP_ID = /^[A-Za-z_][A-Za-z0-9_]*$/;   // what Azure DevOps accepts as a job name
const VERSION = /^\d+\.\d+\.\d+$/;
const DEPLOY_KEYS = new Set(['id', 'deploy', 'version', 'dependsOn']);
const MANUAL_KEYS = new Set(['id', 'manual', 'instructions', 'environments', 'dependsOn']);

export function loadManifest(manifestPath, { environments } = {}) {
  const doc = parse(readFileSync(manifestPath, 'utf8'));
  const params = doc?.extends?.parameters;
  if (!params) throw new Error(`${manifestPath}: expected "extends.parameters" (a manifest extends the release template)`);

  // Environment names come from, in order: the caller, the manifest's own override, or the
  // release template's default. That way the list always matches what the pipeline runs.
  let envNames = environments;
  if (!envNames && params.environments) envNames = params.environments.map((e) => e.name);
  if (!envNames) {
    const templatePath = resolve(dirname(manifestPath), String(doc.extends.template ?? ''));
    if (!existsSync(templatePath)) {
      throw new Error(`Can't read the release template at ${templatePath} to find the environment names. Pass --environments test,staging,prod`);
    }
    const template = parse(readFileSync(templatePath, 'utf8'));
    const envParam = (template.parameters ?? []).find((p) => p.name === 'environments');
    envNames = (envParam?.default ?? []).map((e) => e.name);
  }

  return { release: params.release, steps: params.steps, environments: envNames };
}

// Reads <rulesDir>/<api>/release-rules.yaml. Locally that's the apis/ folder. In the release
// pipeline it's the downloaded packages, so the rules are the ones from the exact versions
// being released.
export function loadRules(rulesDir) {
  const rules = new Map();
  for (const entry of readdirSync(rulesDir, { withFileTypes: true })) {
    const file = join(rulesDir, entry.name, 'release-rules.yaml');
    if (entry.isDirectory() && existsSync(file)) {
      rules.set(entry.name, { file, ...parse(readFileSync(file, 'utf8')) });
    }
  }
  return rules;
}

export function compareVersions(a, b) {
  const [x, y] = [a, b].map((v) => v.split('.').map(Number));
  for (let i = 0; i < 3; i++) if (x[i] !== y[i]) return x[i] - y[i];
  return 0;
}

export function validate(manifest, rules) {
  const errors = [];
  const { release, environments } = manifest;
  const steps = Array.isArray(manifest.steps) ? manifest.steps : [];

  if (typeof release !== 'string' || !release.trim()) errors.push('`release` must be a non-empty name, e.g. 2026.11.04 (quote it so YAML keeps it a string).');
  if (!Array.isArray(manifest.steps) || steps.length === 0) errors.push('`steps` must be a non-empty list.');

  checkRules(rules, errors);

  // 1. Step shape
  const byId = new Map();
  const deployStepFor = new Map();   // api -> step
  for (const [i, step] of steps.entries()) {
    const where = `steps[${i}]${step?.id ? ` (${step.id})` : ''}`;
    if (!step || typeof step !== 'object') { errors.push(`${where}: must be a mapping.`); continue; }

    if (typeof step.id !== 'string' || !STEP_ID.test(step.id)) {
      errors.push(`${where}: \`id\` is required and may only contain letters, digits and _ (it becomes an Azure DevOps job name).`);
    } else if (byId.has(step.id)) {
      errors.push(`${where}: duplicate id "${step.id}".`);
    } else {
      byId.set(step.id, step);
    }

    const isDeploy = 'deploy' in step;
    const isManual = 'manual' in step;
    if (isDeploy === isManual) {
      errors.push(`${where}: must have exactly one of \`deploy\` (an API) or \`manual\` (a pause for a person).`);
      continue;
    }

    const allowed = isDeploy ? DEPLOY_KEYS : MANUAL_KEYS;
    for (const key of Object.keys(step)) {
      if (!allowed.has(key)) errors.push(`${where}: unknown key \`${key}\`. ${isDeploy ? 'Deploy' : 'Manual'} steps allow: ${[...allowed].join(', ')}.`);
    }

    if (step.dependsOn !== undefined && !(Array.isArray(step.dependsOn) && step.dependsOn.every((d) => typeof d === 'string'))) {
      errors.push(`${where}: \`dependsOn\` must be a list of step ids, e.g. [deploy_customers].`);
    }

    if (isDeploy) {
      if (!rules.has(step.deploy)) {
        errors.push(`${where}: unknown API "${step.deploy}". Known APIs: ${[...rules.keys()].join(', ')}.`);
      } else if (deployStepFor.has(step.deploy)) {
        errors.push(`${where}: ${step.deploy} is already deployed by step "${deployStepFor.get(step.deploy).id}". Each API appears once per release.`);
      } else {
        deployStepFor.set(step.deploy, step);
      }
      if (typeof step.version !== 'string' || !VERSION.test(step.version)) {
        errors.push(`${where}: \`version\` must be an exact package version like 1.8.0, got ${JSON.stringify(step.version)}.`);
      }
    } else {
      if (typeof step.instructions !== 'string' || !step.instructions.trim()) {
        errors.push(`${where}: manual steps need \`instructions\`. They're what the approver sees when the release pauses.`);
      }
      if (step.environments !== undefined) {
        if (!Array.isArray(step.environments) || step.environments.length === 0) {
          errors.push(`${where}: \`environments\` must be a non-empty list. Leave it out to pause in every environment.`);
        } else {
          for (const env of step.environments) {
            if (!environments.includes(env)) errors.push(`${where}: unknown environment "${env}". Environments: ${environments.join(', ')}.`);
          }
        }
      }
    }
  }

  // 2. Completeness
  for (const api of rules.keys()) {
    if (!deployStepFor.has(api)) {
      errors.push(`${api} is missing. List every API in every release with its exact version, even unchanged ones (they're skipped at deploy time). That way the manifest always describes all of live.`);
    }
  }

  // 3. Graph
  for (const step of byId.values()) {
    for (const dep of asList(step.dependsOn)) {
      if (dep === step.id) errors.push(`${step.id}: depends on itself.`);
      else if (!byId.has(dep)) errors.push(`${step.id}: dependsOn "${dep}", but there's no step with that id.`);
    }
  }
  const cycle = findCycle(byId);
  if (cycle) errors.push(`dependsOn has a cycle, so nothing in it could ever start: ${cycle.join(' → ')}.`);

  // Stop before the rule checks if the graph itself is broken. Their messages would be noise.
  if (errors.length) return { errors, plan: null };

  // 4. Release rules
  const ancestors = ancestorSets(byId);
  for (const [api, step] of deployStepFor) {
    for (const rule of rules.get(api).deployAfter ?? []) {
      const other = deployStepFor.get(rule.api);
      const because = rule.reason ? ` Why: ${rule.reason}` : '';
      if (rule.minVersion && compareVersions(other.version, rule.minVersion) < 0) {
        errors.push(`${api} ${step.version} needs ${rule.api} >= ${rule.minVersion}, but this release pins ${rule.api} ${other.version}.${because}`);
      }
      if (!ancestors.get(step.id).has(other.id)) {
        errors.push(`${api} must be deployed after ${rule.api}, but step "${step.id}" doesn't depend on "${other.id}", directly or through another step.${because} Fix: add ${other.id} to ${step.id}'s dependsOn.`);
      }
    }
  }

  return { errors, plan: errors.length ? null : buildPlan(byId) };
}

function checkRules(rules, errors) {
  for (const [api, r] of rules) {
    if (r.api !== api) errors.push(`${r.file}: \`api\` is "${r.api}" but the folder is "${api}".`);
    for (const dep of r.deployAfter ?? []) {
      if (!rules.has(dep.api)) errors.push(`${r.file}: deployAfter refers to unknown API "${dep.api}".`);
      if (dep.minVersion !== undefined && !VERSION.test(String(dep.minVersion))) errors.push(`${r.file}: minVersion for ${dep.api} must look like 1.2.0.`);
      if (!dep.reason) errors.push(`${r.file}: deployAfter ${dep.api} needs a \`reason\`. It's shown to whoever has to fix a failing release.`);
    }
  }
}

const asList = (v) => (Array.isArray(v) ? v : []);

function findCycle(byId) {
  const state = new Map();   // id -> 'visiting' | 'done'
  const path = [];
  const visit = (id) => {
    if (state.get(id) === 'done' || !byId.has(id)) return null;
    if (state.get(id) === 'visiting') return [...path.slice(path.indexOf(id)), id];
    state.set(id, 'visiting');
    path.push(id);
    for (const dep of asList(byId.get(id).dependsOn)) {
      const found = visit(dep);
      if (found) return found;
    }
    path.pop();
    state.set(id, 'done');
    return null;
  };
  for (const id of byId.keys()) {
    const found = visit(id);
    if (found) return found;
  }
  return null;
}

function ancestorSets(byId) {
  const memo = new Map();
  const of = (id) => {
    if (!memo.has(id)) {
      const set = new Set();
      for (const dep of asList(byId.get(id).dependsOn)) {
        set.add(dep);
        for (const a of of(dep)) set.add(a);
      }
      memo.set(id, set);
    }
    return memo.get(id);
  };
  for (const id of byId.keys()) of(id);
  return memo;
}

// Groups steps into waves: a step's wave is one more than the latest wave it depends on. Every
// step in a wave can run at the same time. This is the order the pipeline will actually run.
function buildPlan(byId) {
  const wave = new Map();
  const waveOf = (id) => {
    if (!wave.has(id)) wave.set(id, 1 + Math.max(0, ...asList(byId.get(id).dependsOn).map(waveOf)));
    return wave.get(id);
  };
  const waves = [];
  for (const step of byId.values()) (waves[waveOf(step.id) - 1] ??= []).push(step);
  return waves;
}

export function describeStep(step, environments) {
  if (step.deploy) return `deploy ${step.deploy} ${step.version}`;
  const where = step.environments ? step.environments.join(', ') : environments.join(', ');
  return `PAUSE: ${step.manual} (${where})`;
}

export function formatPlan(manifest, plan) {
  const lines = [`Release ${manifest.release}: ${manifest.environments.join(' → ')}`];
  plan.forEach((steps, i) => {
    lines.push(`  ${i + 1}. ${steps.map((s) => describeStep(s, manifest.environments)).join('  |  ')}`);
  });
  return lines.join('\n');
}

// The markdown shown on the pipeline run's "Release" tab.
export function formatSummary(manifest, plan) {
  const deploys = plan.flat().filter((s) => s.deploy);
  const manuals = plan.flat().filter((s) => s.manual);
  const out = [
    `# Release ${manifest.release}`,
    '',
    `Promoted through: ${manifest.environments.join(' → ')}`,
    '',
    '## Versions',
    '',
    '| API | Version |',
    '|---|---|',
    ...deploys.map((s) => `| ${s.deploy} | ${s.version} |`),
    '',
    '## Order',
    '',
    'Steps on the same line run in parallel.',
    '',
    ...plan.map((steps, i) => `${i + 1}. ${steps.map((s) => describeStep(s, manifest.environments)).join(' · ')}`),
  ];
  if (manuals.length) {
    out.push('', '## Manual steps');
    for (const s of manuals) {
      out.push('', `### ${s.manual}`, '', `Environments: ${(s.environments ?? manifest.environments).join(', ')}`, '', s.instructions.trim());
    }
  }
  return out.join('\n') + '\n';
}
