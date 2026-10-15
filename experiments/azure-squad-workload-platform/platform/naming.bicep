// One place that decides every shared resource name. The backbone, identity and squad
// deployments each import this rather than passing names to each other as outputs, so a squad
// deployment needs nothing from the backbone deployment except that it has run.
//
// The suffix comes from the subscription ID, which makes globally-unique names (APIM, Front
// Door, Key Vault, storage) deterministic per subscription without anyone choosing them.

@export()
func platformNames(subscriptionId string) object => {
  backboneResourceGroup: 'rg-platform-backbone'
  identityResourceGroup: 'rg-platform-identity'
  logAnalytics: 'log-platform'
  vnet: 'vnet-platform'
  apimSubnet: 'snet-apim'
  privateEndpointSubnet: 'snet-private-endpoints'
  functionIntegrationSubnet: 'snet-function-integration'
  serviceBusNamespace: 'sb-platform-${uniqueString(subscriptionId)}'
  apim: 'apim-platform-${uniqueString(subscriptionId)}'
  frontDoor: 'afd-platform'
  frontDoorEndpoint: 'squads-${uniqueString(subscriptionId)}'
  frontDoorApimOriginGroup: 'apim'
  containerRegistry: 'crplatform${uniqueString(subscriptionId)}'
}

@export()
func squadNames(subscriptionId string, squad string) object => {
  resourceGroup: 'rg-squad-${squad}'
  functionApp: 'func-${squad}-${take(uniqueString(subscriptionId, squad), 6)}'
  keyVault: 'kv-${squad}-${take(uniqueString(subscriptionId, squad), 6)}'
  appConfig: 'appcs-${squad}-${take(uniqueString(subscriptionId, squad), 6)}'
  dataStorage: 'st${squad}${take(uniqueString(subscriptionId, squad), 8)}'
  deployerIdentity: 'id-squad-${squad}-deployer'
}

@export()
@description('The privatelink zones every private endpoint resolves through. Created once in the backbone.')
func privateDnsZones() object => {
  vault: 'privatelink.vaultcore.azure.net'
  appConfig: 'privatelink.azconfig.io'
  blob: 'privatelink.blob.${environment().suffixes.storage}'
}
