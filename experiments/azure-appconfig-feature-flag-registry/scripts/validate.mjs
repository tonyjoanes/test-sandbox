#!/usr/bin/env node
// Validates every flag in ../flags/*.json against schema.json, plus two checks the schema
// itself can't express: the filename has to match the flag's own `name`, and a flag shouldn't
// already be past its expiry. Run with no arguments to validate the whole registry (what CI
// does on every PR); pass explicit file paths to validate just those (what the test fixtures
// in __fixtures__/ use, and what you'd run locally against a single flag you're editing).
//
// Exit code is the actual signal CI acts on: 0 only if every file passed. A past-expiry flag
// is a warning, not a failure — see README.md's Known Gaps for why that's deliberately not a
// hard stop yet.

import { readFileSync, readdirSync } from 'node:fs';
import { basename, dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import Ajv2020 from 'ajv/dist/2020.js';
import addFormats from 'ajv-formats';

const scriptDir = dirname(fileURLToPath(import.meta.url));
const flagsDir = join(scriptDir, '..', 'flags');
const schemaPath = join(flagsDir, 'schema.json');

const ajv = new Ajv2020({ allErrors: true, strict: true });
addFormats(ajv);
const schema = JSON.parse(readFileSync(schemaPath, 'utf8'));
const validateAgainstSchema = ajv.compile(schema);

function discoverFlagFiles() {
  return readdirSync(flagsDir)
    .filter((f) => f.endsWith('.json') && f !== 'schema.json')
    .map((f) => join(flagsDir, f));
}

function validateOne(filePath) {
  const fileLabel = basename(filePath);
  const errors = [];
  let flag;

  try {
    flag = JSON.parse(readFileSync(filePath, 'utf8'));
  } catch (err) {
    return { fileLabel, ok: false, errors: [`not valid JSON: ${err.message}`], warnings: [] };
  }

  if (!validateAgainstSchema(flag)) {
    for (const e of validateAgainstSchema.errors) {
      errors.push(`${e.instancePath || '(root)'} ${e.message}`);
    }
  }

  const expectedName = basename(filePath, '.json');
  if (flag.name !== undefined && flag.name !== expectedName) {
    errors.push(`name "${flag.name}" does not match filename "${expectedName}.json" — the schema can't catch this, only the filename check can`);
  }

  const warnings = [];
  if (typeof flag.expiry === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(flag.expiry)) {
    const today = new Date().toISOString().slice(0, 10);
    if (flag.expiry < today) {
      warnings.push(`expiry ${flag.expiry} is in the past — flag should be removed or its expiry extended`);
    }
  }

  return { fileLabel, ok: errors.length === 0, errors, warnings };
}

function main() {
  const argPaths = process.argv.slice(2);
  const filePaths = argPaths.length > 0 ? argPaths : discoverFlagFiles();

  if (filePaths.length === 0) {
    console.log('No flag files found.');
    process.exit(0);
  }

  let anyFailed = false;
  for (const filePath of filePaths) {
    const result = validateOne(filePath);
    if (result.ok) {
      console.log(`  OK   ${result.fileLabel}`);
    } else {
      anyFailed = true;
      console.log(`  FAIL ${result.fileLabel}`);
      for (const e of result.errors) console.log(`         - ${e}`);
    }
    for (const w of result.warnings) console.log(`         ! ${w}`);
  }

  console.log('');
  console.log(anyFailed ? 'Validation failed.' : `Validated ${filePaths.length} flag(s) — all clean.`);
  process.exit(anyFailed ? 1 : 0);
}

main();
