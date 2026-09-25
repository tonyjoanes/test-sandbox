// Only called for tier=landingZone (see ../main.bicep) — a sandbox never gets this module
// deployed at all. That's deliberate: "sandboxes can't reach the corp network" is true because
// there's no VNet peering to break, not because a policy says not to. Absence, not denial.
//
// This creates only the SPOKE side of the peering. The HUB side (platform-connectivity peering
// back to this spoke) is created separately, by platform automation reacting to a new landing
// zone landing under Corp/Online — a squad has no rights into platform-connectivity to create
// it themselves, and shouldn't need to.

targetScope = 'subscription'

@description('REQUIRED. Address space for this squad\'s spoke VNet, e.g. 10.42.8.0/22. Platform allocates disjoint blocks per squad from a reserved supernet — squads don\'t pick their own, so two spokes peered to the same hub can never collide.')
param addressPrefix string

@description('REQUIRED. Azure region. Must be one of the regions the hub itself exists in — a spoke can\'t peer to a hub in a region it isn\'t deployed to.')
param location string

@description('REQUIRED. Resource ID of the hub VNet in Platform - Connectivity. Peering to it is what gives this landing zone centralized egress through the hub firewall, corp connectivity, and shared DNS.')
param hubVnetResourceId string

resource networkRg 'Microsoft.Resources/resourceGroups@2023-07-01' = {
  name: 'rg-network'
  location: location
}

resource spokeVnet 'Microsoft.Network/virtualNetworks@2023-09-01' = {
  name: 'vnet-spoke'
  location: location
  scope: networkRg
  properties: {
    addressSpace: {
      addressPrefixes: [
        addressPrefix
      ]
    }
    subnets: [
      {
        name: 'snet-workload'
        properties: {
          addressPrefix: cidrSubnet(addressPrefix, 24, 0)
        }
      }
    ]
  }
}

resource spokeToHub 'Microsoft.Network/virtualNetworks/virtualNetworkPeerings@2023-09-01' = {
  parent: spokeVnet
  name: 'spoke-to-hub'
  properties: {
    remoteVirtualNetwork: {
      id: hubVnetResourceId
    }
    allowForwardedTraffic: true
    allowGatewayTransit: false
    useRemoteGateways: true
  }
}

@description('Resource ID of this squad\'s spoke VNet — platform automation reads this output to create the matching hub-side peering.')
output spokeVnetResourceId string = spokeVnet.id
