// Grants ONE CI service principal Contributor on ONE subscription — deliberately Contributor,
// never Owner. Contrast with azure-landing-zone-squad-vending/vending/modules/rbac.bicep,
// which grants the squad's HUMAN Owner group Owner on the same subscription: a pipeline that
// can manage its own subscription's RBAC is a pipeline that can grant itself anything,
// including rights it wasn't supposed to have. Contributor can deploy resources; it can't
// touch role assignments.
//
// Deploy once per tier, scoped to that tier's own subscription — the sandbox CI identity gets
// Contributor on the sandbox subscription only; the landing-zone CI identity gets Contributor
// on the landing-zone subscription only. Never the same principal on both (see ci-identity.bicep).

targetScope = 'subscription'

@description('REQUIRED. Object ID of the CI service principal for THIS tier — see ci-identity.bicep\'s `servicePrincipalObjectId` output.')
param servicePrincipalObjectId string

// Well-known Azure built-in role definition ID — same GUID in every tenant, not a secret.
var contributorRoleId = 'b24988ac-6180-42a0-ab88-20f7382dd24c'

resource ciRoleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(subscription().id, servicePrincipalObjectId, 'Contributor')
  properties: {
    principalId: servicePrincipalObjectId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', contributorRoleId)
  }
}
