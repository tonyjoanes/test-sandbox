// Grants a squad's Entra group a role at subscription scope. Always a group principal, never
// an individually-named user — see README.md for why that's non-negotiable, not a style
// preference. Deployed by main.bicep with `scope: subscription(<newly vended subscription>)`.

targetScope = 'subscription'

@description('REQUIRED. Entra group object ID to grant access to.')
param groupObjectId string

@allowed([
  'Owner'
  'Contributor'
])
@description('REQUIRED. Role to grant at subscription scope.')
param roleName string

// Well-known Azure built-in role definition IDs — the same GUID in every tenant, not a secret.
var roleDefinitionIds = {
  Owner: '8e3af657-a8ff-443c-a75c-2fe8c4bcb635'
  Contributor: 'b24988ac-6180-42a0-ab88-20f7382dd24c'
}

resource roleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(subscription().id, groupObjectId, roleName)
  properties: {
    principalId: groupObjectId
    principalType: 'Group'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleDefinitionIds[roleName])
  }
}
