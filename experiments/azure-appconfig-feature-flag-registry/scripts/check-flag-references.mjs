#!/usr/bin/env node
// The compile-time check: scans app source for feature-management `.isEnabled("...")` calls
// and fails the build if any referenced flag has no matching file in the registry. This is a
// grep, not a real parser — it matches the literal-string-argument pattern the demo app
// actually uses (see ../app/src/index.mjs), which covers the common case without pulling in a
// JS parser dependency. A flag name built at runtime (`isEnabled(someVariable)`) is invisible
// to it on purpose — see README's Known Gaps.
//
// Run with no arguments to check the real app/ against the real flags/ registry (what CI
// does). Pass two explicit directories to point it elsewhere — what the test fixtures in
// test/fixtures/app-check/ use.

import { readFileSync, readdirSync, statSync } from 'node:fs';
import { join, dirname, extname } from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptDir = dirname(fileURLToPath(import.meta.url));
const [appDirArg, flagsDirArg] = process.argv.slice(2);
const appDir = appDirArg ?? join(scriptDir, '..', 'app');
const flagsDir = flagsDirArg ?? join(scriptDir, '..', 'flags');

const SOURCE_EXTENSIONS = new Set(['.js', '.mjs', '.ts']);
const ISENABLED_CALL = /\.isEnabled\(\s*['"]([a-z0-9]+(?:-[a-z0-9]+)*)['"]/g;

function walk(dir) {
  let files = [];
  for (const entry of readdirSync(dir)) {
    if (entry === 'node_modules') continue;
    const fullPath = join(dir, entry);
    const stat = statSync(fullPath);
    if (stat.isDirectory()) files = files.concat(walk(fullPath));
    else if (SOURCE_EXTENSIONS.has(extname(entry))) files.push(fullPath);
  }
  return files;
}

function findReferences(dir) {
  const references = [];
  for (const filePath of walk(dir)) {
    const lines = readFileSync(filePath, 'utf8').split('\n');
    lines.forEach((line, i) => {
      for (const match of line.matchAll(ISENABLED_CALL)) {
        references.push({ flagName: match[1], file: filePath, line: i + 1 });
      }
    });
  }
  return references;
}

function registryFlagNames(dir) {
  return new Set(
    readdirSync(dir)
      .filter((f) => f.endsWith('.json') && f !== 'schema.json')
      .map((f) => f.replace(/\.json$/, ''))
  );
}

function main() {
  const references = findReferences(appDir);
  const registered = registryFlagNames(flagsDir);

  if (references.length === 0) {
    console.log('No feature-management .isEnabled(...) calls found.');
    process.exit(0);
  }

  let anyMissing = false;
  const referencedNames = new Set();
  for (const ref of references) {
    referencedNames.add(ref.flagName);
    if (registered.has(ref.flagName)) {
      console.log(`  OK   ${ref.flagName}  (${ref.file}:${ref.line})`);
    } else {
      anyMissing = true;
      console.log(`  FAIL ${ref.flagName}  (${ref.file}:${ref.line}) — no ${ref.flagName}.json in the registry`);
    }
  }

  const unused = [...registered].filter((name) => !referencedNames.has(name));
  if (unused.length > 0) {
    console.log('');
    for (const name of unused) {
      console.log(`  !    ${name} is in the registry but not referenced anywhere scanned — candidate for removal`);
    }
  }

  console.log('');
  console.log(anyMissing
    ? 'Build failed: code references a flag that is not in the registry.'
    : `Checked ${references.length} reference(s) against ${registered.size} registered flag(s) — all referenced flags exist.`);
  process.exit(anyMissing ? 1 : 0);
}

main();
