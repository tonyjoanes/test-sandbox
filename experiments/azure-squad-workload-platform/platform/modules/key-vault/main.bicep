metadata name = 'key-vault'
metadata description = 'Key Vault with private endpoint, diagnostics and RBAC built in. None of the three can be turned off: there is no parameter for it.'
metadata version = '1.0.0'

param name string
param location string
param tags object
param peSubnetId string
param privateDnsZoneId string
param logAnalyticsWorkspaceId string

@description('Principals (the function app identity) that get Key Vault Secrets User. Read-only on secret values; cannot list or change keys, certificates or access policy.')
param secretReaderPrincipalIds string[]

var keyVaultSecretsUser = '4633458b-17de-408a-b874-0445c86b69e6'

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    tenantId: tenant().tenantId
    sku: { family: 'A', name: 'standard' }
    // RBAC, not access policies: access is granted the same way as for every other resource,
    // shows up in the same role assignment reports, and can't be edited by a vault contributor.
    enableRbacAuthorization: true
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    // Purge protection is off only because this is a PoC that gets torn down and rebuilt with
    // the same names (see the teardown workflow). Once on, it can never be turned off, and the
    // name stays locked for the retention period. In a real platform: on.
    publicNetworkAccess: 'Disabled'
    networkAcls: { defaultAction: 'Deny', bypass: 'None' }
  }
}

module pe '../private-endpoint/main.bicep' = {
  name: '${name}-pe'
  params: {
    name: 'pe-${name}'
    location: location
    tags: tags
    subnetId: peSubnetId
    targetResourceId: vault.id
    groupId: 'vault'
    privateDnsZoneId: privateDnsZoneId
  }
}

resource diagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'to-log-analytics'
  scope: vault
  properties: {
    workspaceId: logAnalyticsWorkspaceId
    logs: [{ categoryGroup: 'allLogs', enabled: true }]
    metrics: [{ category: 'AllMetrics', enabled: true }]
  }
}

resource readers 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for principalId in secretReaderPrincipalIds: {
    name: guid(vault.id, principalId, keyVaultSecretsUser)
    scope: vault
    properties: {
      principalId: principalId
      principalType: 'ServicePrincipal'
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', keyVaultSecretsUser)
    }
  }
]

output id string = vault.id
output uri string = vault.properties.vaultUri
