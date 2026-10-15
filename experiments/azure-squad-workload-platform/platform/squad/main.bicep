// One squad's resource group, built from its manifest. The platform pipeline runs this with
// the platform deployer identity, once per squads/<squad>/workload.yaml:
//
//   node scripts/render-params.mjs --squad orders > orders.parameters.json
//   az deployment sub create -l uksouth -f squad/main.bicep -p @orders.parameters.json
//
// Everything here is idempotent: re-running with an unchanged manifest changes nothing, and
// adding a capability to the manifest adds exactly that capability.

targetScope = 'subscription'

import { platformNames, squadNames, privateDnsZones } from '../naming.bicep'

param location string = deployment().location

@description('From the manifest. Already validated against the schema by render-params.mjs.')
param squadName string

@description('From the manifest: the repo whose "production" environment may deploy code here.')
param repository string

@description('From the manifest, with every key present (render-params.mjs fills defaults).')
param capabilities capabilitiesType

@sealed()
type capabilitiesType = {
  keyVault: bool
  appConfig: bool
  storageContainers: string[]
  topics: string[]
  subscribe: string[]
  expose: { public: bool, path: string }
}

var platform = platformNames(subscription().subscriptionId)
var squad = squadNames(subscription().subscriptionId, squadName)
var zones = privateDnsZones()
var tags = { squad: squadName }

var backboneRg = resourceGroup(platform.backboneResourceGroup)
var vnetId = resourceId(subscription().subscriptionId, platform.backboneResourceGroup, 'Microsoft.Network/virtualNetworks', platform.vnet)
var logAnalyticsId = resourceId(subscription().subscriptionId, platform.backboneResourceGroup, 'Microsoft.OperationalInsights/workspaces', platform.logAnalytics)
var dnsZoneId = mapValues(zones, z => resourceId(subscription().subscriptionId, platform.backboneResourceGroup, 'Microsoft.Network/privateDnsZones', z))
var hasStorage = !empty(capabilities.storageContainers)

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: squad.resourceGroup
  location: location
  tags: tags
}

// ---------------------------------------------------------------------------------------------
// Compute: always present. The function app's identity is what every capability grants to.
// ---------------------------------------------------------------------------------------------

// Endpoints are computed from names rather than read from module outputs: the function app
// needs them as settings, and the Key Vault (etc.) needs the function's identity for RBAC, so
// reading outputs both ways would be a cycle.
var settings = union(
  capabilities.keyVault ? { KEY_VAULT_URI: 'https://${squad.keyVault}${environment().suffixes.keyvaultDns}/' } : {},
  capabilities.appConfig ? { APP_CONFIG_ENDPOINT: 'https://${squad.appConfig}.azconfig.io' } : {},
  hasStorage ? { DATA_BLOB_ENDPOINT: 'https://${squad.dataStorage}.blob.${environment().suffixes.storage}/' } : {},
  empty(capabilities.topics) && empty(capabilities.subscribe)
    ? {}
    : { SERVICE_BUS__fullyQualifiedNamespace: '${platform.serviceBusNamespace}.servicebus.windows.net' },
  { SQUAD: squadName }
)

module functionApp '../modules/function-app/main.bicep' = {
  name: 'function-app'
  scope: rg
  params: {
    name: squad.functionApp
    location: location
    tags: tags
    logAnalyticsWorkspaceId: logAnalyticsId
    peSubnetId: '${vnetId}/subnets/${platform.privateEndpointSubnet}'
    blobPrivateDnsZoneId: dnsZoneId.blob
    integrationSubnetId: '${vnetId}/subnets/${platform.functionIntegrationSubnet}'
    apimSubnetId: '${vnetId}/subnets/${platform.apimSubnet}'
    appSettings: settings
  }
}

// ---------------------------------------------------------------------------------------------
// Capabilities, each only if the manifest asked for it
// ---------------------------------------------------------------------------------------------

module keyVault '../modules/key-vault/main.bicep' = if (capabilities.keyVault) {
  name: 'key-vault'
  scope: rg
  params: {
    name: squad.keyVault
    location: location
    tags: tags
    peSubnetId: '${vnetId}/subnets/${platform.privateEndpointSubnet}'
    privateDnsZoneId: dnsZoneId.vault
    logAnalyticsWorkspaceId: logAnalyticsId
    secretReaderPrincipalIds: [functionApp.outputs.principalId]
  }
}

module appConfig '../modules/app-config/main.bicep' = if (capabilities.appConfig) {
  name: 'app-config'
  scope: rg
  params: {
    name: squad.appConfig
    location: location
    tags: tags
    peSubnetId: '${vnetId}/subnets/${platform.privateEndpointSubnet}'
    privateDnsZoneId: dnsZoneId.appConfig
    logAnalyticsWorkspaceId: logAnalyticsId
    dataReaderPrincipalIds: [functionApp.outputs.principalId]
  }
}

module storage '../modules/storage/main.bicep' = if (hasStorage) {
  name: 'storage'
  scope: rg
  params: {
    name: squad.dataStorage
    location: location
    tags: tags
    peSubnetId: '${vnetId}/subnets/${platform.privateEndpointSubnet}'
    blobPrivateDnsZoneId: dnsZoneId.blob
    logAnalyticsWorkspaceId: logAnalyticsId
    containerNames: capabilities.storageContainers
    dataContributorPrincipalIds: [functionApp.outputs.principalId]
  }
}

// Topics and subscriptions live in the shared namespace, in the backbone RG. The squad never
// gets any role on that RG; only its function identity gets data roles on its own topics.
module topics '../modules/servicebus-topic/main.bicep' = [
  for topic in capabilities.topics: {
    name: 'topic-${topic}'
    scope: backboneRg
    params: {
      namespaceName: platform.serviceBusNamespace
      topicName: '${squadName}.${topic}'
      senderPrincipalIds: [functionApp.outputs.principalId]
    }
  }
]

module subscriptions '../modules/servicebus-subscription/main.bicep' = [
  for topic in capabilities.subscribe: {
    name: 'subscription-${topic}'
    scope: backboneRg
    params: {
      namespaceName: platform.serviceBusNamespace
      topicName: topic
      subscriberSquad: squadName
      receiverPrincipalIds: [functionApp.outputs.principalId]
    }
  }
]

module route '../modules/api-route/main.bicep' = if (capabilities.expose.public) {
  name: 'api-route'
  scope: backboneRg
  params: {
    apimName: platform.apim
    frontDoorProfileName: platform.frontDoor
    frontDoorEndpointName: platform.frontDoorEndpoint
    frontDoorOriginGroupName: platform.frontDoorApimOriginGroup
    squadName: squadName
    path: substring(capabilities.expose.path, 1)
    functionHostName: functionApp.outputs.defaultHostName
  }
}

// ---------------------------------------------------------------------------------------------
// The squad's own deploy identity: Squad Workload Contributor on this RG, nothing else
// ---------------------------------------------------------------------------------------------

module squadDeployer '../identity/squad-deployer.bicep' = {
  name: 'squad-deployer'
  scope: resourceGroup(platform.identityResourceGroup)
  params: {
    name: squad.deployerIdentity
    location: location
    tags: tags
    repository: repository
  }
}

module squadDeployerRole 'squad-deployer-role.bicep' = {
  name: 'squad-deployer-role'
  scope: rg
  params: {
    principalId: squadDeployer.outputs.principalId
  }
}

// What the squad needs to wire up its own repo. None of these are secrets.
output resourceGroup string = rg.name
output functionAppName string = squad.functionApp
output squadDeployerClientId string = squadDeployer.outputs.clientId
output publicUrl string = capabilities.expose.public ? route!.outputs.url : ''
