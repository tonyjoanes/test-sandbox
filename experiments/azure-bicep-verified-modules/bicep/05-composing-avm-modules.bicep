// =============================================================================
// 05 — Composing Several AVM Modules Into a Small Secure Web App
// =============================================================================
// One module is nice; the real payoff is composing several to build an
// actual architecture. This wires up:
//
//   user-assigned identity
//         │  (granted a role on the vault below)
//         ▼
//   key vault  ──stores a secret──▶  app setting on the web app (via Key
//                                     Vault reference, never the raw value)
//         ▲
//         │  (same identity also used to run the web app — no passwords
//         │   anywhere in this file)
//   app service plan ──▶ web app (site)
//
// Every arrow above is a module output feeding into another module's
// input — 'outputs.resourceId', 'outputs.principalId' — the same
// module-composition mechanic as calling local module files, just with the
// building blocks coming from the registry instead of your own repo.
//
// Module paths for key-vault, storage, web/serverfarm and web/site are
// verified against the source repo; managed-identity/user-assigned-identity
// follows the same documented naming convention but wasn't independently
// re-checked in this session — confirm it via IntelliSense before relying
// on it. Version tags throughout are illustrative.
// =============================================================================

@description('Azure region for all resources.')
param location string = resourceGroup().location

@description('A secret value to store in Key Vault (e.g. a third-party API key). Never given a default — must be supplied at deploy time.')
@secure()
param apiKeySecretValue string

var namePrefix = 'avmdemo${uniqueString(resourceGroup().id)}'

// --- 1. Identity the web app will run as, with no stored credentials ---------
module identity 'br/public:avm/res/managed-identity/user-assigned-identity:0.4.1' = {
  name: 'identityDeployment'
  params: {
    name: '${namePrefix}-id'
    location: location
  }
}

// --- 2. Key Vault, granting ONLY that identity access to read secrets --------
module keyVault 'br/public:avm/res/key-vault/vault:0.11.0' = {
  name: 'keyVaultDeployment'
  params: {
    name: 'kv-${namePrefix}'
    location: location
    enableRbacAuthorization: true
    roleAssignments: [
      {
        principalId: identity.outputs.principalId
        roleDefinitionIdOrName: 'Key Vault Secrets User'
        principalType: 'ServicePrincipal'
      }
    ]
    // Secret content — shape shown is illustrative; confirm the module's
    // current 'secrets' parameter structure before using this for real.
    secrets: [
      {
        name: 'api-key'
        value: apiKeySecretValue
      }
    ]
  }
}

// --- 3. Storage account, also readable only by the same identity -------------
module storageAccount 'br/public:avm/res/storage/storage-account:0.14.3' = {
  name: 'storageAccountDeployment'
  params: {
    name: 'st${namePrefix}'
    location: location
    skuName: 'Standard_LRS'
    kind: 'StorageV2'
    allowSharedKeyAccess: false // no access keys at all — RBAC only
    roleAssignments: [
      {
        principalId: identity.outputs.principalId
        roleDefinitionIdOrName: 'Storage Blob Data Contributor'
        principalType: 'ServicePrincipal'
      }
    ]
  }
}

// --- 4. App Service plan (the compute the web app runs on) -------------------
module appServicePlan 'br/public:avm/res/web/serverfarm:0.4.1' = {
  name: 'appServicePlanDeployment'
  params: {
    name: '${namePrefix}-plan'
    location: location
    skuName: 'B1'
    kind: 'Linux'
    reserved: true // required for Linux plans
  }
}

// --- 5. The web app itself, tying everything above together ------------------
module webApp 'br/public:avm/res/web/site:0.13.0' = {
  name: 'webAppDeployment'
  params: {
    name: '${namePrefix}-app'
    location: location
    kind: 'app,linux'
    serverFarmResourceId: appServicePlan.outputs.resourceId

    // Same 'managedIdentities' shape appears on every AVM module that
    // supports identity — run the app AS the identity created above,
    // instead of a system-assigned identity or (worse) a stored secret.
    managedIdentities: {
      userAssignedResourceIds: [
        identity.outputs.resourceId
      ]
    }

    // The app reads the secret through a Key Vault reference — Azure
    // resolves this at runtime using the app's own identity; the secret
    // value itself never appears in this template, in deployment history,
    // or in app configuration exports.
    appSettingsKeyValuePairs: {
      API_KEY: '@Microsoft.KeyVault(VaultName=${keyVault.outputs.name};SecretName=api-key)'
      AZURE_CLIENT_ID: identity.outputs.clientId // tells the app which user-assigned identity to authenticate as
    }
  }
}

output webAppHostName string = webApp.outputs.defaultHostname

// -----------------------------------------------------------------------------
// This whole file is five modules wired together by hand — a legitimate,
// common way to compose AVM resource modules. For architectures common
// enough that Microsoft/the community have already standardised them, a
// PATTERN module (the 'avm/ptn/...' namespace, as opposed to 'avm/res/...')
// may replace most or all of this file with one module call — e.g. the
// 'app-service-lza' pattern category (verified as a real category in the
// registry) targets exactly this "web app + supporting resources" shape.
// Pattern module internals weren't verified in this session — check
// https://aka.ms/AVM for the exact module path, current parameters, and
// whether it fits your requirements before reaching for it over manual
// composition like this file demonstrates.
// -----------------------------------------------------------------------------
