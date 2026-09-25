// =============================================================================
// 01 — Bicep Fundamentals (no Azure Verified Modules yet)
// =============================================================================
// Bicep is a domain-specific language that compiles down to an ARM (Azure
// Resource Manager) JSON template — the same engine that's always deployed
// Azure resources. You write Bicep, `bicep build` (or the deployment CLI
// under the hood) transpiles it to ARM JSON, and Azure Resource Manager
// deploys that. Bicep exists because hand-writing ARM JSON is painful:
// no comments, no loops without ARM's copy/copyIndex gymnastics, no type
// checking, deeply nested brackets for simple things.
//
// This file is the syntax primer — every construct used across the rest of
// this experiment, without any Azure Verified Modules involved. Read this
// first if Bicep itself (not just AVM) is new to you.
// =============================================================================

// 'targetScope' declares what level this template deploys INTO — resourceGroup
// (default, most common), subscription, managementGroup, or tenant. Left
// implicit here since 'resourceGroup' is the default.
targetScope = 'resourceGroup'

// --- Parameters --------------------------------------------------------------
// Decorators (the @-prefixed lines) add validation and documentation that
// show up as IntelliSense hints in VS Code and as portal UI hints when this
// template is deployed through the Azure Portal's "Custom deployment" form.

@description('Short environment name, used as a naming prefix.')
@allowed([
  'dev'
  'test'
  'prod'
])
param environmentName string = 'dev'

@description('Azure region for all resources. Defaults to the resource group''s own region.')
param location string = resourceGroup().location

@description('Number of storage containers to create in the loop example below.')
@minValue(1)
@maxValue(5)
param containerCount int = 2

// @secure() prevents a parameter's value from being logged or shown in
// deployment history/outputs — use it for anything sensitive. Declared
// here purely to show the decorator; 05-composing-avm-modules.bicep uses
// one for real (a Key Vault secret value).
@description('Example of a secure parameter — intentionally unused below.')
@secure()
param exampleSecretValue string = ''

@description('Tags applied to every resource in this template.')
param tags object = {
  environment: environmentName
  managedBy: 'bicep'
}

// --- Variables -----------------------------------------------------------
// Computed once, referenced anywhere below. 'uniqueString()' derives a
// deterministic-but-unique suffix from its inputs (here, the resource
// group's ID) — the standard pattern for globally-unique names like
// storage accounts, since re-running the same deployment produces the same
// suffix instead of a random one.
var namePrefix = '${environmentName}-demo'
var storageAccountName = 'st${uniqueString(resourceGroup().id)}'

// --- A plain resource ------------------------------------------------------
// The shape is always: resource <symbolicName> '<resourceType>@<apiVersion>' = { ... }
// The symbolic name (storageAccount) is a Bicep-only identifier used to
// reference this resource elsewhere in the file (storageAccount.id,
// storageAccount.properties.xxx) — it is NOT the Azure resource name (that's
// the 'name:' property inside).
resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: storageAccountName
  location: location
  tags: tags
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
  }
}

// --- A nested child resource -------------------------------------------------
// 'parent:' establishes the resource hierarchy without needing to compose
// its full resource ID by hand.
resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-01-01' = {
  parent: storageAccount
  name: 'default'
}

// --- A loop --------------------------------------------------------------
// 'for <item> in <collection>:' stamps out one resource per iteration — the
// Bicep equivalent of ARM JSON's copy/copyIndex, without the boilerplate.
// range(0, n) generates [0, 1, ..., n-1].
resource containers 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = [
  for i in range(0, containerCount): {
    parent: blobService
    name: 'container-${i}'
    properties: {
      publicAccess: 'None'
    }
  }
]

// --- A conditional resource ------------------------------------------------
// 'if (<condition>)' only deploys this resource when the expression is true
// — here, only in prod, an environment-gated resource is common for things
// like extra locks or higher-tier SKUs.
resource productionLock 'Microsoft.Authorization/locks@2020-05-01' = if (environmentName == 'prod') {
  name: '${namePrefix}-lock'
  scope: storageAccount
  properties: {
    level: 'CanNotDelete'
  }
}

// --- Calling a module --------------------------------------------------------
// 'module' is how Bicep composes multiple templates together — either a
// LOCAL file (a relative path, e.g. './modules/network.bicep') or a
// REGISTRY reference. This is the exact same 'module' keyword used to
// consume Azure Verified Modules in every other file in this folder — an
// AVM module is just a registry-hosted Bicep file someone else maintains.
// Shown here (not deployed by this file alone) to preview the syntax:
//
//   module logAnalytics 'br/public:avm/res/operational-insights/workspace:0.9.0' = {
//     name: 'logAnalyticsDeployment'
//     params: {
//       name: '${namePrefix}-law'
//       location: location
//     }
//   }
//
// 'br/public:' is a built-in alias for the Microsoft Container Registry
// (MCR) instance that hosts the public Bicep registry — no authentication
// needed to pull from it. See 03/04/05/06 for this used for real.

// --- Outputs ---------------------------------------------------------------
// Values other templates (or a calling module, or a deployment pipeline)
// can read back after this template deploys. Never output secrets — that's
// exactly what @secure() parameters combined with NOT echoing them into
// outputs is for.
output storageAccountId string = storageAccount.id
output storageAccountName string = storageAccount.name
output primaryBlobEndpoint string = storageAccount.properties.primaryEndpoints.blob
