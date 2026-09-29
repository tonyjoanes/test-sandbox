// Creates the subscription itself and places it under the correct management group AT
// CREATION TIME — not created, then moved in a second step. Placing it any other way leaves a
// window where a brand-new subscription sits outside every guardrail policy assignment.
//
// Illustrative: `Microsoft.Subscription/aliases`' exact property names (particularly inside
// `additionalProperties`) have shifted across API versions and differ between EA and MCA
// billing accounts — check the current schema for your billing type before using this as-is.

targetScope = 'tenant'

@minLength(3)
@maxLength(63)
@description('REQUIRED. Display name for the new subscription, shown in the Azure portal.')
param subscriptionDisplayName string

@description('REQUIRED. EA enrollment account or MCA billing profile/invoice section resource ID this subscription bills against. No default — billing scope is always an explicit, reviewed choice, never inherited implicitly.')
param billingScopeId string

@allowed([
  'Production'
  'DevTest'
])
@description('REQUIRED. DevTest unlocks DevTest pricing on supported resource types. main.bicep sets this to DevTest for sandbox-tier requests, Production for landing-zone tier — a squad never chooses it directly.')
param workload string

@description('REQUIRED. Full resource ID of the target management group (Corp, Online, or Sandboxes) — see ../../management-groups/mg-hierarchy.bicep outputs.')
param managementGroupId string

@description('REQUIRED. Entra object ID of the squad\'s Owner group. The subscription is created with this GROUP as owner — never an individual user, so ownership survives someone leaving the squad without a break-glass scramble.')
param ownerGroupObjectId string

resource subscriptionAlias 'Microsoft.Subscription/aliases@2021-10-01' = {
  name: toLower(replace(subscriptionDisplayName, ' ', '-'))
  properties: {
    workload: workload
    displayName: subscriptionDisplayName
    billingScope: billingScopeId
    additionalProperties: {
      managementGroupId: managementGroupId
      subscriptionOwnerId: ownerGroupObjectId
    }
  }
}

@description('The new subscription\'s ID. Every other vending module (rbac.bicep, budget.bicep, network-spoke.bicep) is deployed against it with `scope: subscription(this)`.')
output subscriptionId string = subscriptionAlias.properties.subscriptionId
