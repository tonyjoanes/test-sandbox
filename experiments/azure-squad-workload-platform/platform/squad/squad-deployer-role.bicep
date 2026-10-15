param principalId string

// The custom role from identity/main.bicep, found by the same deterministic name.
var squadWorkloadContributor = guid(subscription().id, 'squad-workload-contributor')

resource assignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(resourceGroup().id, principalId, squadWorkloadContributor)
  properties: {
    principalId: principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', squadWorkloadContributor)
  }
}
