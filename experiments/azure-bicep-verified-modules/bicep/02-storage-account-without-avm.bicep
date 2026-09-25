// =============================================================================
// 02 — Secure Storage Account WITHOUT Azure Verified Modules ("the hard way")
// =============================================================================
// This is what it takes to hand-roll a storage account meeting a typical
// baseline security posture: HTTPS-only, TLS 1.2 minimum, no public network
// access, reachable only through a private endpoint (with its DNS wired up),
// diagnostic logs shipped to Log Analytics, and a scoped RBAC role grant.
// Nothing here is WRONG — it's just a lot of resource types, API versions,
// and Azure-specific knowledge (private DNS zone groups, diagnostic category
// names, role definition GUIDs) that every team has to get right,
// independently, every single time they need a storage account.
//
// Compare this file's length and the knowledge it assumes to
// 03-storage-account-with-avm.bicep, which produces the same outcome.
// =============================================================================

@description('Name of the storage account (globally unique, 3-24 lowercase alphanumeric characters).')
@minLength(3)
@maxLength(24)
param storageAccountName string

@description('Azure region for all resources.')
param location string = resourceGroup().location

@description('Resource ID of the subnet the private endpoint attaches to.')
param privateEndpointSubnetId string

@description('Resource ID of the private DNS zone for blob storage (privatelink.blob.core.windows.net).')
param blobPrivateDnsZoneId string

@description('Resource ID of the Log Analytics workspace diagnostic logs are sent to.')
param logAnalyticsWorkspaceId string

@description('Principal (object) ID to grant read access to blob data.')
param readerPrincipalId string

// Well-known built-in role definition GUID for "Storage Blob Data Reader".
// Verify against https://learn.microsoft.com/azure/role-based-access-control/built-in-roles
// before using — a wrong GUID here fails the deployment with an unhelpful
// "role assignment does not exist" error, one more sharp edge this approach
// exposes that a named parameter (see 03) avoids entirely.
var storageBlobDataReaderRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1')

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: storageAccountName
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    publicNetworkAccess: 'Disabled'
    networkAcls: {
      defaultAction: 'Deny'
      bypass: 'AzureServices'
    }
  }
}

// A private endpoint is its own top-level resource, wired to the storage
// account via 'privateLinkServiceId' — not a property ON the storage account.
resource privateEndpoint 'Microsoft.Network/privateEndpoints@2023-09-01' = {
  name: '${storageAccountName}-blob-pe'
  location: location
  properties: {
    subnet: {
      id: privateEndpointSubnetId
    }
    privateLinkServiceConnections: [
      {
        name: '${storageAccountName}-blob-plsc'
        properties: {
          privateLinkServiceId: storageAccount.id
          groupIds: [
            'blob'
          ]
        }
      }
    ]
  }
}

// Without this, the private endpoint exists but nothing resolves its
// private IP automatically — callers would still resolve the storage
// account's PUBLIC DNS name unless something registers a record in the
// private DNS zone. This nested child resource is that "something".
resource privateDnsZoneGroup 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2023-09-01' = {
  parent: privateEndpoint
  name: 'default'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'blob-config'
        properties: {
          privateDnsZoneId: blobPrivateDnsZoneId
        }
      }
    ]
  }
}

// Diagnostic settings are also their own resource, scoped ONTO the storage
// account via 'scope:' — and you must know the exact log/metric CATEGORY
// NAMES the resource type supports (they differ per resource type and
// change over time as Azure adds new ones).
resource diagnosticSettings 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: '${storageAccountName}-diag'
  scope: storageAccount
  properties: {
    workspaceId: logAnalyticsWorkspaceId
    metrics: [
      {
        category: 'Transaction'
        enabled: true
      }
    ]
  }
}

// Role assignments need a deterministic, unique 'name' (a GUID) — Azure
// rejects a random one on every re-deploy as a NEW assignment, so the
// convention is guid(scope, principal, roleDefinitionId) to make re-running
// this template idempotent instead of creating duplicate assignments.
resource roleAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storageAccount.id, readerPrincipalId, storageBlobDataReaderRoleId)
  scope: storageAccount
  properties: {
    roleDefinitionId: storageBlobDataReaderRoleId
    principalId: readerPrincipalId
    principalType: 'ServicePrincipal'
  }
}

output storageAccountId string = storageAccount.id
output primaryBlobEndpoint string = storageAccount.properties.primaryEndpoints.blob

// -----------------------------------------------------------------------------
// Tally: 5 resource types, 2 API versions to track for drift over time, 1
// role GUID to source correctly, and prior knowledge of exactly how private
// endpoints + private DNS zone groups fit together. All of it has to be
// reviewed, tested, and kept current by whoever owns this file — multiplied
// across every team in an org that also needs a secure storage account.
// -----------------------------------------------------------------------------
