// FOUNDATION MODULE — owned by the platform team. Changes here require platform review
// (see ../../governance/CODEOWNERS.example). Squads consume this module; they do not fork it.
//
// The core idea: this file does not declare `resource storageAccount 'Microsoft.Storage/...'`
// directly. It wraps the public Azure Verified Module (AVM) for storage accounts, and narrows
// AVM's large general-purpose parameter surface down to the handful of decisions this
// organisation actually wants squads making. AVM gives correctness (it's Microsoft-maintained,
// tested, and kept current with API versions); this wrapper gives control (it decides what of
// that surface squads see at all, and what values are legal).

targetScope = 'resourceGroup'

import { mandatoryTags, networkPosture, approvedStorageSku } from '../../shared/types.bicep'

// --- REQUIRED contract: parameters with no default. -----------------------------------------
// In Bicep, "no default" IS "required" — there is no separate required/optional keyword.
// That's the whole mechanism: if platform wants a decision made explicitly at every call site,
// it just doesn't give the parameter a default. The compiler enforces the rest.

@minLength(3)
@maxLength(24)
@description('REQUIRED. Storage account name. No default — the platform will not silently generate one that a squad then can\'t predict or search for.')
param name string

@description('REQUIRED. Azure region for the deployment.')
param location string

@description('REQUIRED. Standard tag contract — every field in `mandatoryTags` must be supplied.')
param tags mandatoryTags

@description('REQUIRED. Network posture — see shared/types.bicep. No default `publicNetworkAccess` on purpose: "secure by accident" is not a posture the platform is willing to offer.')
param network networkPosture

// --- OPTIONAL, but still governed: parameters with a default AND a restricted type. ----------
// This is the "extend within limits" half of the contract. A plain `param sku string = 'Standard_LRS'`
// would let a squad pass anything the underlying resource provider accepts. Typing it as
// `approvedStorageSku` means a squad can override the default, but only to another value the
// platform has already approved — enforced at compile time, not by convention.

@description('OPTIONAL extension point. Defaults to the platform baseline. Overridable only within the approved SKU allow-list.')
param sku approvedStorageSku = 'Standard_LRS'

@description('OPTIONAL extension point. Defaults to false; squads building Data Lake / analytics workloads may enable it.')
param enableHierarchicalNamespace bool = false

// --- A sealed escape hatch for the long tail of AVM properties this contract doesn't promote --
// Every AVM module exposes far more knobs than a platform team wants to review one by one.
// Rather than ban everything not explicitly listed above (too rigid) or accept an open
// `object` blob (too loose — anything could be smuggled through unreviewed), this type gives
// squads a small, named, sealed set of additional overrides. Sealing means the compiler
// rejects any key that isn't one of these two — growing the escape hatch is itself a reviewed
// change to this file.
@sealed()
type advancedOverrides = {
  minimumTlsVersion: ('TLS1_0' | 'TLS1_1' | 'TLS1_2')?
  allowBlobPublicAccess: bool?
}

@description('OPTIONAL, sealed extension object — see `advancedOverrides` above.')
param advanced advancedOverrides = {}

@description('OPTIONAL, deliberately unsealed. Squad-specific tags on top of the mandatory set — free-form because, unlike `tags`, nothing downstream depends on this having a fixed shape.')
param additionalTags object = {}

// --- Composition: call AVM, don't reimplement it. --------------------------------------------
// The version is pinned explicitly. Bumping it is a one-line, reviewed diff to this file —
// squads pick up AVM upgrades when the platform team decides to ship them, not automatically
// on their next `bicep restore`.
module storageAccount 'br/public:avm/res/storage/storage-account:0.14.3' = {
  name: take('${deployment().name}-storage', 64)
  params: {
    name: name
    location: location
    tags: union(tags, additionalTags)
    skuName: sku
    publicNetworkAccess: network.publicNetworkAccess
    networkAcls: network.subnetResourceId != null
      ? {
          defaultAction: 'Deny'
          virtualNetworkRules: [
            {
              id: network.subnetResourceId!
            }
          ]
          ipRules: [for ip in (network.allowedIpRules ?? []): { value: ip }]
        }
      : null
    isHnsEnabled: enableHierarchicalNamespace
    minimumTlsVersion: advanced.?minimumTlsVersion ?? 'TLS1_2'
    allowBlobPublicAccess: advanced.?allowBlobPublicAccess ?? false
  }
}

// --- Outputs: the only thing downstream modules should ever depend on. -----------------------
// Deliberately not re-exporting every AVM output — a narrow output surface is as much a part
// of the contract as a narrow input surface. Add an output here when a real consumer needs it,
// not speculatively.

@description('Resource ID of the created storage account.')
output resourceId string = storageAccount.outputs.resourceId

@description('Primary blob service endpoint.')
output primaryBlobEndpoint string = storageAccount.outputs.primaryBlobEndpointUri
