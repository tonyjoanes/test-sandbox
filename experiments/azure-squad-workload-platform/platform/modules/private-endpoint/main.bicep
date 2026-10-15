metadata name = 'private-endpoint'
metadata description = 'One private endpoint plus its DNS zone group. Used by every other module; not called by squads directly.'
metadata version = '1.0.0'

param name string
param location string
param tags object
param subnetId string
param targetResourceId string

@description('The private link sub-resource, e.g. vault, configurationStores, blob, sites.')
param groupId string

@description('The privatelink.* DNS zone for this groupId, created once in the backbone.')
param privateDnsZoneId string

resource pe 'Microsoft.Network/privateEndpoints@2023-11-01' = {
  name: name
  location: location
  tags: tags
  properties: {
    subnet: { id: subnetId }
    privateLinkServiceConnections: [
      {
        name: name
        properties: {
          privateLinkServiceId: targetResourceId
          groupIds: [groupId]
        }
      }
    ]
  }

  // Without the zone group the endpoint exists but nothing resolves to it, and clients quietly
  // keep using the public hostname — which policy has made unreachable. This is the line most
  // hand-written private endpoints forget.
  resource dns 'privateDnsZoneGroups' = {
    name: 'default'
    properties: {
      privateDnsZoneConfigs: [
        { name: groupId, properties: { privateDnsZoneId: privateDnsZoneId } }
      ]
    }
  }
}

output id string = pe.id
