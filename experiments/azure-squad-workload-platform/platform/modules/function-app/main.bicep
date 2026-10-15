metadata name = 'function-app'
metadata description = 'Flex Consumption function app with a system-assigned identity, VNet integration, its own private host storage, App Insights and diagnostics. The squad deploys code into it; the platform owns everything about it except the code.'
metadata version = '1.0.0'

param name string
param location string
param tags object
param logAnalyticsWorkspaceId string
param peSubnetId string
param blobPrivateDnsZoneId string

@description('Outbound: the function reaches Key Vault, App Config and storage through this subnet (delegated to Microsoft.App/environments, which Flex Consumption requires).')
param integrationSubnetId string

@description('Inbound: the only subnet allowed to call the app, i.e. APIM. See the access restriction comment below for why this is not a private endpoint.')
param apimSubnetId string

@description('Extra app settings the squad composition passes in: endpoints of whatever the manifest asked for. Never secrets; the app reads those from Key Vault with its own identity.')
param appSettings object

var storageBlobDataOwner = 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
var monitoringMetricsPublisher = '3913510d-42f4-4e42-8a64-420c390055eb'
var hostStorageName = take('st${replace(name, '-', '')}host', 24)

// Host storage: deployment packages and runtime state. Separate from the squad's data storage
// so a squad's container RBAC never touches the runtime's.
resource hostStorage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: hostStorageName
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

    resource deployments 'containers' = {
      name: 'deployments'
    }
  }
}

module hostStoragePe '../private-endpoint/main.bicep' = {
  name: '${hostStorageName}-blob-pe'
  params: {
    name: 'pe-${hostStorageName}-blob'
    location: location
    tags: tags
    subnetId: peSubnetId
    targetResourceId: hostStorage.id
    groupId: 'blob'
    privateDnsZoneId: blobPrivateDnsZoneId
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: 'appi-${name}'
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalyticsWorkspaceId
    DisableLocalAuth: true
  }
}

resource plan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: 'asp-${name}'
  location: location
  tags: tags
  kind: 'functionapp'
  sku: { tier: 'FlexConsumption', name: 'FC1' }
  properties: { reserved: true }
}

resource site 'Microsoft.Web/sites@2024-04-01' = {
  name: name
  location: location
  tags: tags
  kind: 'functionapp,linux'
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    virtualNetworkSubnetId: integrationSubnetId
    // Not 'Disabled' + private endpoint, which is what the original plan said. Disabling public
    // access also makes the SCM (deploy) endpoint private, and a GitHub-hosted runner can't
    // reach a private SCM endpoint without a self-hosted runner in the VNet. So: the app's
    // public endpoint is closed by access restriction to the APIM subnet only, while SCM stays
    // reachable for deploys and accepts Entra tokens only (basic auth is off, below).
    publicNetworkAccess: 'Enabled'
    siteConfig: {
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      ipSecurityRestrictionsDefaultAction: 'Deny'
      ipSecurityRestrictions: [
        { name: 'apim-only', action: 'Allow', priority: 100, vnetSubnetResourceId: apimSubnetId }
      ]
      scmIpSecurityRestrictionsUseMain: false
      scmIpSecurityRestrictionsDefaultAction: 'Allow'
      appSettings: concat(
        [
          // Identity-based host storage: no connection string, no account key.
          { name: 'AzureWebJobsStorage__accountName', value: hostStorage.name }
          { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
          { name: 'APPLICATIONINSIGHTS_AUTHENTICATION_STRING', value: 'Authorization=AAD' }
        ],
        map(items(appSettings), s => { name: s.key, value: s.value })
      )
    }
    functionAppConfig: {
      deployment: {
        storage: {
          type: 'blobContainer'
          value: '${hostStorage.properties.primaryEndpoints.blob}deployments'
          authentication: { type: 'SystemAssignedIdentity' }
        }
      }
      scaleAndConcurrency: { maximumInstanceCount: 40, instanceMemoryMB: 2048 }
      runtime: { name: 'node', version: '20' }
    }
  }

  resource noFtpBasicAuth 'basicPublishingCredentialsPolicies' = {
    name: 'ftp'
    properties: { allow: false }
  }

  resource noScmBasicAuth 'basicPublishingCredentialsPolicies' = {
    name: 'scm'
    properties: { allow: false }
  }
}

resource hostStorageOwner 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(hostStorage.id, site.id, storageBlobDataOwner)
  scope: hostStorage
  properties: {
    principalId: site.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataOwner)
  }
}

// App Insights has local (instrumentation key) auth disabled, so telemetry is only accepted
// from an identity holding this role.
resource telemetryPublisher 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(appInsights.id, site.id, monitoringMetricsPublisher)
  scope: appInsights
  properties: {
    principalId: site.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', monitoringMetricsPublisher)
  }
}

resource diagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'to-log-analytics'
  scope: site
  properties: {
    workspaceId: logAnalyticsWorkspaceId
    logs: [{ category: 'FunctionAppLogs', enabled: true }]
    metrics: [{ category: 'AllMetrics', enabled: true }]
  }
}

output id string = site.id
output defaultHostName string = site.properties.defaultHostName
output principalId string = site.identity.principalId
