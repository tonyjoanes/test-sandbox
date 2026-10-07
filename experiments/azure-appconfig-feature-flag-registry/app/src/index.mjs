#!/usr/bin/env node
// Minimal demo: reads the payments-v2 flag from Azure App Configuration via the official
// feature management library, and branches on it. A real service would gate actual behavior
// (which checkout code path runs); this just prints which path would run, so the demo stays
// legible without a real payments backend behind it.
//
// Connects with DefaultAzureCredential — no connection string, no stored secret. Locally
// this picks up `az login`; in Azure, a managed identity would stand in for it. Same
// "no stored secret" principle as the registry pipeline's OIDC federation — see
// ../../README.md.
//
// This is the ONE piece of the whole registry experiment that genuinely needs a live Azure App
// Configuration store to run end to end — everything else (schema validation, dry-run,
// the compile-time check below) runs with zero Azure access. See README's
// "What's Actually Runnable Here".

import { DefaultAzureCredential } from '@azure/identity';
import { load } from '@azure/app-configuration-provider';
import { FeatureManager, ConfigurationMapFeatureFlagProvider } from '@microsoft/feature-management';

const endpoint = process.env.APPCONFIG_ENDPOINT;
if (!endpoint) {
  console.error('APPCONFIG_ENDPOINT is not set — see ../../README.md\'s "Running This For Real".');
  process.exit(1);
}

const credential = new DefaultAzureCredential();
const appConfig = await load(endpoint, credential, {
  featureFlagOptions: { enabled: true },
});

const featureProvider = new ConfigurationMapFeatureFlagProvider(appConfig);
const featureManager = new FeatureManager(featureProvider);

// The literal string here, "payments-v2", is exactly what
// ../../scripts/check-flag-references.mjs greps for — and exactly what it checks still
// has a matching flags/payments-v2.json in the registry. Rename the flag in one place without
// the other and the compile-time check fails the build.
const paymentsV2Enabled = await featureManager.isEnabled('payments-v2');

console.log(`payments-v2 is ${paymentsV2Enabled ? 'ON' : 'OFF'}`);
console.log(paymentsV2Enabled
  ? '-> routing checkout through the v2 payments flow'
  : '-> routing checkout through the legacy payments flow');
