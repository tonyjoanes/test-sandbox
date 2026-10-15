metadata name = 'storage'
metadata description = 'Squad data storage account plus blob containers. Private endpoint, diagnostics and per-container RBAC built in.'
metadata version = '1.0.0'

param name string
param location string
param tags object
param peSubnetId string
param blobPrivateDnsZoneId string
param logAnalyticsWorkspaceId string
param containerNames string[]

@description('Principals that get Storage Blob Data Contributor on each container (not on the account).')
param dataContributorPrincipalIds string[]

var storageBlobDataContributor = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'

resource account 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: name
  location: location
  tags: tags
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    publicNetworkAccess: 'Disabled'
    allowSharedKeyAccess: false
    allowBlobPublicAccess: false
    minimumTlsVersion: 'TLS1_2'
    networkAcls: { defaultAction: 'Deny', bypass: 'None' }
  }

  resource blob 'blobServices' = {
    name: 'default'

    resource containers 'containers' = [for c in containerNames: { name: c }]
  }
}

module pe '../private-endpoint/main.bicep' = {
  name: '${name}-blob-pe'
  params: {
    name: 'pe-${name}-blob'
    location: location
    tags: tags
    subnetId: peSubnetId
    targetResourceId: account.id
    groupId: 'blob'
    privateDnsZoneId: blobPrivateDnsZoneId
  }
}

// Storage diagnostics live on the service (blobServices), not the account; account-level only
// carries metrics.
resource blobDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'to-log-analytics'
  scope: account::blob
  properties: {
    workspaceId: logAnalyticsWorkspaceId
    logs: [{ categoryGroup: 'allLogs', enabled: true }]
    metrics: [{ category: 'Transaction', enabled: true }]
  }
}

// One assignment per (container, principal) pair: a squad that asks for [invoices, exports]
// can write to those two containers and nothing else in the account.
var pairs = flatten(map(containerNames, c => map(dataContributorPrincipalIds, p => { container: c, principalId: p })))

resource containerContributors 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for pair in pairs: {
    name: guid(account.id, pair.container, pair.principalId, storageBlobDataContributor)
    scope: account::blob::containers[indexOf(containerNames, pair.container)]
    properties: {
      principalId: pair.principalId
      principalType: 'ServicePrincipal'
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributor)
    }
  }
]

output id string = account.id
output blobEndpoint string = account.properties.primaryEndpoints.blob
