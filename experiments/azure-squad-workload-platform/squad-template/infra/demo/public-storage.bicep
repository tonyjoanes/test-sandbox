// Demo 2: a squad trying to go around the platform. Deploy it with the squad identity:
//
//   az deployment group create -g rg-squad-orders -f infra/demo/public-storage.bicep
//
// Expected: RequestDisallowedByPolicy, with the assignment's non-compliance message telling
// the squad to use the manifest instead. NOT AuthorizationFailed: the squad role allows
// creating storage accounts on purpose, so that it's policy doing the refusing here.

param location string = resourceGroup().location

resource sneaky 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: 'stsneaky${uniqueString(resourceGroup().id)}'
  location: location
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    publicNetworkAccess: 'Enabled'
    allowBlobPublicAccess: true
  }
}
