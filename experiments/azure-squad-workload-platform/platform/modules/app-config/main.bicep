metadata name = 'app-config'
metadata description = 'App Configuration store (Standard: the cheapest tier with private endpoints) with private endpoint, diagnostics and RBAC built in.'
metadata version = '1.0.0'

param name string
param location string
param tags object
param peSubnetId string
param privateDnsZoneId string
param logAnalyticsWorkspaceId string

@description('Principals that get App Configuration Data Reader. This role is store-wide, which is why each squad gets its own store; see README "App Configuration cost".')
param dataReaderPrincipalIds string[]

var appConfigurationDataReader = '516239f1-63e1-4d78-a4de-a74fb236a071'

resource store 'Microsoft.AppConfiguration/configurationStores@2023-03-01' = {
  name: name
  location: location
  tags: tags
  sku: { name: 'standard' }
  properties: {
    publicNetworkAccess: 'Disabled'
    // Entra ID only. Access keys are a shared secret that bypasses RBAC entirely.
    disableLocalAuth: true
    softDeleteRetentionInDays: 1
    enablePurgeProtection: false
  }
}

module pe '../private-endpoint/main.bicep' = {
  name: '${name}-pe'
  params: {
    name: 'pe-${name}'
    location: location
    tags: tags
    subnetId: peSubnetId
    targetResourceId: store.id
    groupId: 'configurationStores'
    privateDnsZoneId: privateDnsZoneId
  }
}

resource diagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'to-log-analytics'
  scope: store
  properties: {
    workspaceId: logAnalyticsWorkspaceId
    logs: [{ categoryGroup: 'allLogs', enabled: true }]
    metrics: [{ category: 'AllMetrics', enabled: true }]
  }
}

resource readers 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for principalId in dataReaderPrincipalIds: {
    name: guid(store.id, principalId, appConfigurationDataReader)
    scope: store
    properties: {
      principalId: principalId
      principalType: 'ServicePrincipal'
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', appConfigurationDataReader)
    }
  }
]

output id string = store.id
output endpoint string = store.properties.endpoint
