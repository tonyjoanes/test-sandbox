// WORKLOAD MODULE — owned by a squad. This is the shape squad-authored modules take: compose
// the platform's foundation module for the governed resource, then add whatever squad-specific
// resources sit around it. No CODEOWNERS gate on files under workload/ — squads merge these
// through normal team review, because nothing here can loosen the foundation contract.
//
// What a squad CAN change here: which of foundation's optional parameters it sets, and what
// extra squad-owned resources (containers, in this example) it wires up around the result.
// What a squad CANNOT change here: the shape of `tags`/`network` (imported, sealed/typed),
// or the SKU allow-list (enforced by `approvedStorageSku` in the foundation module itself).
// Trying to pass `sku: 'Premium_LRS'` below would fail `bicep build`, not a later review.

targetScope = 'resourceGroup'

import { mandatoryTags, networkPosture } from '../../shared/types.bicep'

param name string
param location string = resourceGroup().location
param tags mandatoryTags
param network networkPosture

@description('Squad-specific extension, not part of the platform contract at all — owned entirely by this module.')
param blobContainers string[] = [
  'inbound'
  'processed'
]

module storage '../../foundation/storage-account/main.bicep' = {
  name: take('${deployment().name}-foundation-storage', 64)
  params: {
    name: name
    location: location
    tags: tags
    network: network
    sku: 'Standard_ZRS' // allowed: within the approved allow-list
    advanced: {
      allowBlobPublicAccess: false
    }
  }
}

// Squad-owned resources layered on top of the governed foundation resource. These don't go
// through the foundation module because they carry no organisation-wide policy concerns —
// container names and count are this squad's call.
resource storageAccountRef 'Microsoft.Storage/storageAccounts@2023-01-01' existing = {
  name: name
  dependsOn: [
    storage
  ]
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-01-01' existing = {
  parent: storageAccountRef
  name: 'default'
}

resource containers 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = [
  for containerName in blobContainers: {
    parent: blobService
    name: containerName
    properties: {
      publicAccess: 'None'
    }
  }
]

output storageAccountId string = storage.outputs.resourceId
