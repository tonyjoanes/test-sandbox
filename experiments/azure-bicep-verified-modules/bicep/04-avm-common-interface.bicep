// =============================================================================
// 04 — The AVM "Common Interface" (why learning one module teaches you all of them)
// =============================================================================
// Every Azure Verified Module — regardless of which Azure resource it wraps
// — is built against the same published specification (the "AVM Interface
// contract"). That means a fixed set of parameter names show up, meaning the
// same thing, in nearly every module you'll ever use. This file deploys
// three UNRELATED resource types — a storage account, a Key Vault, and a
// virtual network — side by side specifically to make that repetition
// visible. Once you've used one AVM module, you already mostly know how to
// use the next one.
//
// Module paths (avm/res/storage/storage-account, avm/res/key-vault/vault,
// avm/res/network/virtual-network) are verified real paths in the public
// registry; version tags are illustrative — pin the current version for
// your use case. This file focuses on the parameters that REPEAT across
// modules and deliberately omits some other required inputs each module
// individually needs (e.g. a Key Vault SKU) — check IntelliSense/the
// module README for the full required-parameter list before deploying.
// =============================================================================

@description('Azure region for all resources.')
param location string = resourceGroup().location

@description('Principal (object) ID to grant access where relevant.')
param principalId string

var commonTags = {
  environment: 'demo'
  costCentre: 'platform-team'
}

// -----------------------------------------------------------------------------
// name / location / tags — every AVM resource module takes these three,
// spelled exactly the same way, because every ARM resource needs them.
// -----------------------------------------------------------------------------

module storageAccount 'br/public:avm/res/storage/storage-account:0.14.3' = {
  name: 'storageAccountDeployment'
  params: {
    name: 'st${uniqueString(resourceGroup().id)}'
    location: location // <- same param name as every module below
    tags: commonTags // <- same param name as every module below

    // enableTelemetry — present on every AVM module (see 06). Shown once
    // here explicitly; the rest of this file leaves it at its default
    // (true) to keep focus on the parameters that actually vary.
    enableTelemetry: true

    // lock — every AVM module accepts the SAME resource-lock shape, so
    // "protect this from accidental deletion" is one identical block
    // whichever resource type you're deploying.
    lock: {
      kind: 'None' // CanNotDelete | ReadOnly | None
    }

    // roleAssignments — same shape as in 03, same shape as the Key Vault
    // and virtual network modules below. Learn this array once.
    roleAssignments: [
      {
        principalId: principalId
        roleDefinitionIdOrName: 'Storage Blob Data Reader'
        principalType: 'ServicePrincipal'
      }
    ]
  }
}

module keyVault 'br/public:avm/res/key-vault/vault:0.11.0' = {
  name: 'keyVaultDeployment'
  params: {
    name: 'kv-${uniqueString(resourceGroup().id)}'
    location: location // <- identical parameter name, different resource
    tags: commonTags // <- identical parameter name, different resource
    enableTelemetry: true
    lock: {
      kind: 'None'
    }
    roleAssignments: [
      {
        principalId: principalId
        roleDefinitionIdOrName: 'Key Vault Secrets User' // <- same array shape, different role
        principalType: 'ServicePrincipal'
      }
    ]
  }
}

module virtualNetwork 'br/public:avm/res/network/virtual-network:0.5.1' = {
  name: 'virtualNetworkDeployment'
  params: {
    name: 'vnet-demo'
    location: location // <- identical parameter name, third resource in a row
    tags: commonTags // <- identical parameter name, third resource in a row
    enableTelemetry: true
    addressPrefixes: [
      '10.0.0.0/16'
    ]
    subnets: [
      {
        name: 'default'
        addressPrefix: '10.0.0.0/24'
      }
    ]
    // No roleAssignments here — not every parameter appears on every
    // module (a virtual network has no data plane to grant "Reader" on
    // the way a storage account does), but where a concept DOES apply to
    // a resource type, the parameter name and shape used to express it is
    // consistent.
  }
}

// -----------------------------------------------------------------------------
// The payoff: without reading ANY of these three modules' documentation in
// depth, you could already correctly guess that avm/res/compute/virtual-machine
// also takes name/location/tags/lock/roleAssignments/enableTelemetry the
// same way. That consistency — not any single module — is most of what AVM
// is actually selling: institutional knowledge encoded once, in the
// interface, instead of re-learned per resource type.
// -----------------------------------------------------------------------------
