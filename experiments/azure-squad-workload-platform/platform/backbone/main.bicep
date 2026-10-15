// The shared backbone: everything squads share, deployed once by the platform team.
//
//   az deployment sub create -l uksouth -f backbone/main.bicep \
//     -p publisherEmail=you@example.com platformDeployerPrincipalId=<from identity/main.bicep>
//
// Run once by a subscription Owner during bootstrap (see README), not by the pipeline.
//
// APIM Developer takes 30-60 minutes to provision. It's the one thing worth leaving up between
// demo sessions; squad resource groups are cheap to delete and recreate.

targetScope = 'subscription'

import { platformNames } from '../naming.bicep'

param location string = deployment().location

@description('APIM requires a publisher contact.')
param publisherEmail string

@description('principalId output of identity/main.bicep.')
param platformDeployerPrincipalId string

var names = platformNames(subscription().subscriptionId)
var tags = { squad: 'platform' }

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: names.backboneResourceGroup
  location: location
  tags: tags
}

module resources 'resources.bicep' = {
  name: 'backbone-resources'
  scope: rg
  params: {
    location: location
    tags: tags
    publisherEmail: publisherEmail
    modulePublisherPrincipalId: platformDeployerPrincipalId
  }
}

output frontDoorHostName string = resources.outputs.frontDoorHostName
output containerRegistryLoginServer string = resources.outputs.containerRegistryLoginServer
