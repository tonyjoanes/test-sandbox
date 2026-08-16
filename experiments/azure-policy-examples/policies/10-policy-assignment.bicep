targetScope = 'subscription'

// ---------------------------------------------------------------------------
// 10 — Assignment: this is what actually turns a definition/initiative on.
// Definitions and initiatives (01-09) are inert until assigned to a scope
// (management group, subscription, or resource group).
//
// Because our initiative includes a `modify` policy (04), the assignment
// needs a managed identity, and that identity needs the role the policy's
// roleDefinitionIds asked for (Tag Contributor) — granted here via a
// separate roleAssignment resource. deployIfNotExists policies need the
// exact same pattern.
// ---------------------------------------------------------------------------

@description('Name of the resource group this baseline is assigned to. Must already exist.')
param targetResourceGroupName string = 'example-rg'

@description('Region for the assignment resource itself (required whenever an identity is attached).')
param location string = 'uksouth'

resource assignment 'Microsoft.Authorization/policyAssignments@2022-06-01' = {
  name: 'governance-baseline-assignment'
  // Assigning at resource-group scope from a subscription-scoped
  // deployment: point `scope` at the target resource group directly.
  scope: resourceGroup(targetResourceGroupName)
  location: location
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    displayName: 'Example governance baseline'
    policyDefinitionId: subscriptionResourceId('Microsoft.Authorization/policySetDefinitions', 'example-governance-baseline')
    parameters: {
      requiredTagName: {
        value: 'Environment'
      }
      environmentValue: {
        value: 'Production'
      }
      allowedLocations: {
        value: [
          'uksouth'
          'ukwest'
        ]
      }
    }
    // Shown to whoever's request gets denied/flagged — replaces Policy's
    // generic error text with something your own teams will recognise.
    nonComplianceMessages: [
      {
        message: 'This resource does not meet the example governance baseline. Check the compliance reason for which rule failed.'
      }
    ]
    // 'Default' = actively enforced (deny/append/modify/deployIfNotExists
    // all take effect). 'DoNotEnforce' = still evaluate and report
    // compliance, but suspend deny/modify/deployIfNotExists — useful for
    // rolling out a new assignment without risking an outage, while still
    // seeing what its impact would be.
    enforcementMode: 'Default'
    // Carve-outs by resource ID prefix, applied before the policy rule
    // itself runs — e.g. exclude a break-glass or shared-platform RG.
    // Prefer exemptions (11) over notScopes for anything that needs a
    // reason, an expiry, or per-policy (rather than whole-assignment)
    // granularity.
    notScopes: []
  }
}

// The identity above needs a role that can write tags before `modify`
// (04) can do anything but report conflicts.
resource tagContributorRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(subscription().id, targetResourceGroupName, assignment.name, 'TagContributor')
  scope: resourceGroup(targetResourceGroupName)
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4a9ae827-6dc8-4573-8ac7-8239d42aa03f') // Tag Contributor
    principalId: assignment.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

output assignmentId string = assignment.id
output assignmentPrincipalId string = assignment.identity.principalId
