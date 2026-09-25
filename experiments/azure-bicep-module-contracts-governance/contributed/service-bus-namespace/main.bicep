// CONTRIBUTED MODULE — owned by the requesting squad (see RFC linked in the PR that added
// this file), not platform. Lives in the shared catalog because other squads asked for it too,
// but platform did not author it and is not implicitly on the hook for it — see
// ../../governance/contributed-module-checklist.md.
//
// This is what "no foundation module exists yet for this resource type" looks like: the
// contributing squad wraps AVM directly, same as a foundation module would, and follows the
// same required/optional/sealed shape using the shared contract types — but the closed
// allow-list below is declared LOCALLY, not added to shared/types.bicep. Extending the org-wide
// vocabulary is a platform-reviewed RFC; declaring a closed type scoped to your own module is
// not, and doing the latter instead of the former is exactly how a squad can move without
// waiting on platform for something that only this module needs.

targetScope = 'resourceGroup'

import { mandatoryTags, networkPosture } from '../../shared/types.bicep'

@minLength(6)
@maxLength(50)
@description('REQUIRED. Namespace name.')
param name string

@description('REQUIRED. Azure region.')
param location string

@description('REQUIRED. Standard tag contract — same shared type every module in this catalog uses.')
param tags mandatoryTags

@description('REQUIRED. Network posture — same shared type every module in this catalog uses.')
param network networkPosture

@allowed([
  'Standard'
  'Premium'
])
@description('OPTIONAL extension point, closed to this module\'s own allow-list. Basic tier deliberately excluded: it has no VNet integration, which would silently conflict with `network.subnetResourceId`.')
param sku string = 'Standard'

@sealed()
@description('Sealed escape hatch, scoped to what this squad has actually needed so far. Widening it is a PR against this file, reviewed by the owning squad — no platform sign-off required, since it does not touch shared/types.bicep.')
type advancedOverrides = {
  zoneRedundant: bool?
  maximumThroughputUnits: int?
}

@description('OPTIONAL, sealed — see `advancedOverrides` above.')
param advanced advancedOverrides = {}

module serviceBusNamespace 'br/public:avm/res/service-bus/namespace:0.11.2' = {
  name: take('${deployment().name}-sbns', 64)
  params: {
    name: name
    location: location
    tags: tags
    skuObject: {
      name: sku
    }
    publicNetworkAccess: network.publicNetworkAccess
    networkRuleSets: network.subnetResourceId != null
      ? {
          virtualNetworkRules: [
            {
              subnetResourceId: network.subnetResourceId!
            }
          ]
          ipRules: [for ip in (network.allowedIpRules ?? []): { ipMask: ip, action: 'Allow' }]
          defaultAction: 'Deny'
        }
      : null
    zoneRedundant: advanced.?zoneRedundant ?? false
    premiumMessagingPartitions: sku == 'Premium' ? (advanced.?maximumThroughputUnits ?? 1) : null
  }
}

@description('Resource ID of the created namespace.')
output resourceId string = serviceBusNamespace.outputs.resourceId

@description('Fully qualified namespace host name, for SDK connection configuration.')
output namespaceHostName string = '${name}.servicebus.windows.net'
