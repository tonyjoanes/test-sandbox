// The vending orchestrator — what actually runs when a squad's request (see ../intake/
// request-template.md) is approved. One deployment, tenant-scoped, that creates the
// subscription, places it under the right management group (inheriting the matching guardrail
// policy immediately), grants the squad's group access, wires up the spend cap, and — for
// landing-zone tier only — deploys the spoke network. A squad ends up with a subscription
// that's already governed, budgeted, and (if applicable) networked, in one run.
//
// Deploy with: az deployment tenant create --location <region> --template-file main.bicep
//   --parameters params/sandbox.bicepparam   (or params/landing-zone.bicepparam)

targetScope = 'tenant'

@minLength(3)
@maxLength(63)
@description('REQUIRED. Squad-facing subscription display name, e.g. "sq-payments-sandbox" or "sq-payments-prod".')
param subscriptionDisplayName string

@description('REQUIRED. Billing scope (EA enrollment account or MCA invoice section) this subscription bills against.')
param billingScopeId string

@allowed([
  'sandbox'
  'landingZone'
])
@description('REQUIRED. Which tier this request is for. Decides management group placement (via `managementGroupId`, below), which guardrail policy the subscription inherits, subscription workload type, and whether a spoke network is deployed at all. See ../README.md.')
param tier string

@description('REQUIRED. Full resource ID of the target management group — Sandboxes for tier=sandbox, Landing Zones - Corp or Landing Zones - Online for tier=landingZone (see ../management-groups/mg-hierarchy.bicep outputs). This module does not pick Corp vs Online for you — that stays a human decision per workload.')
param managementGroupId string

@description('REQUIRED. Entra object ID of the squad\'s Owner group.')
param ownerGroupObjectId string

@description('REQUIRED. Monthly spend cap for this subscription.')
param monthlyBudgetAmount int

@description('REQUIRED. Email address(es) notified on spend thresholds.')
param notificationEmails array

@description('REQUIRED only when tier is landingZone (ignored for sandbox, which never deploys a network). Spoke VNet address prefix, e.g. 10.42.8.0/22.')
param spokeAddressPrefix string = ''

@description('REQUIRED only when tier is landingZone. Resource ID of the hub VNet to peer to.')
param hubVnetResourceId string = ''

@description('Azure region for network/budget scaffolding resources.')
param location string = 'uksouth'

module subscriptionAlias 'modules/subscription.bicep' = {
  name: 'sub-${uniqueString(subscriptionDisplayName)}'
  params: {
    subscriptionDisplayName: subscriptionDisplayName
    billingScopeId: billingScopeId
    workload: tier == 'sandbox' ? 'DevTest' : 'Production'
    managementGroupId: managementGroupId
    ownerGroupObjectId: ownerGroupObjectId
  }
}

module rbac 'modules/rbac.bicep' = {
  name: 'rbac-${uniqueString(subscriptionDisplayName)}'
  scope: subscription(subscriptionAlias.outputs.subscriptionId)
  params: {
    groupObjectId: ownerGroupObjectId
    roleName: 'Owner'
  }
}

module budget 'modules/budget.bicep' = {
  name: 'budget-${uniqueString(subscriptionDisplayName)}'
  scope: subscription(subscriptionAlias.outputs.subscriptionId)
  params: {
    monthlyBudgetAmount: monthlyBudgetAmount
    notificationEmails: notificationEmails
  }
}

// Only a landing-zone-tier request deploys a network at all — see network-spoke.bicep's
// header comment for why that absence, not a policy denial, is what keeps a sandbox off the
// corp network.
module network 'modules/network-spoke.bicep' = if (tier == 'landingZone') {
  name: 'network-${uniqueString(subscriptionDisplayName)}'
  scope: subscription(subscriptionAlias.outputs.subscriptionId)
  params: {
    addressPrefix: spokeAddressPrefix
    location: location
    hubVnetResourceId: hubVnetResourceId
  }
}

@description('The vended subscription ID. Hand this to the squad along with their Owner group — guardrails, budget, and (for landing-zone tier) network are already in place; nothing further is needed before they deploy their first workload.')
output subscriptionId string = subscriptionAlias.outputs.subscriptionId
