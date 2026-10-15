import { platformNames, privateDnsZones } from '../naming.bicep'

param location string
param tags object
param publisherEmail string

@description('The platform deployer identity. Gets AcrPush on the module registry, so module release tags can publish.')
param modulePublisherPrincipalId string

var names = platformNames(subscription().subscriptionId)
var zones = privateDnsZones()

// ---------------------------------------------------------------------------------------------
// Observability
// ---------------------------------------------------------------------------------------------

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: names.logAnalytics
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
    // A cap rather than an alert: a chatty squad can't run up the bill.
    workspaceCapping: { dailyQuotaGb: 1 }
  }
}

// ---------------------------------------------------------------------------------------------
// Network: three subnets, each with one job
// ---------------------------------------------------------------------------------------------

resource apimNsg 'Microsoft.Network/networkSecurityGroups@2023-11-01' = {
  name: 'nsg-${names.apimSubnet}'
  location: location
  tags: tags
  properties: {
    securityRules: [
      // Gateway traffic: only from Front Door. Everything else inbound from the internet falls
      // through to the default DenyAllInBound rule. The X-Azure-FDID check in the APIM policy
      // below narrows this further, to *our* Front Door profile rather than any Front Door.
      {
        name: 'allow-frontdoor-https'
        properties: {
          priority: 100
          direction: 'Inbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourceAddressPrefix: 'AzureFrontDoor.Backend'
          sourcePortRange: '*'
          destinationAddressPrefix: 'VirtualNetwork'
          destinationPortRange: '443'
        }
      }
      // APIM management plane and load balancer health probes, both required for VNet mode.
      {
        name: 'allow-apim-management'
        properties: {
          priority: 110
          direction: 'Inbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourceAddressPrefix: 'ApiManagement'
          sourcePortRange: '*'
          destinationAddressPrefix: 'VirtualNetwork'
          destinationPortRange: '3443'
        }
      }
      {
        name: 'allow-load-balancer'
        properties: {
          priority: 120
          direction: 'Inbound'
          access: 'Allow'
          protocol: 'Tcp'
          sourceAddressPrefix: 'AzureLoadBalancer'
          sourcePortRange: '*'
          destinationAddressPrefix: 'VirtualNetwork'
          destinationPortRange: '6390'
        }
      }
    ]
  }
}

resource vnet 'Microsoft.Network/virtualNetworks@2023-11-01' = {
  name: names.vnet
  location: location
  tags: tags
  properties: {
    addressSpace: { addressPrefixes: ['10.40.0.0/16'] }
    subnets: [
      {
        name: names.apimSubnet
        properties: {
          addressPrefix: '10.40.0.0/27'
          networkSecurityGroup: { id: apimNsg.id }
          // Lets function apps allow "traffic from this subnet" in their access restrictions.
          serviceEndpoints: [{ service: 'Microsoft.Web' }]
        }
      }
      {
        name: names.privateEndpointSubnet
        properties: { addressPrefix: '10.40.1.0/24' }
      }
      {
        name: names.functionIntegrationSubnet
        properties: {
          addressPrefix: '10.40.2.0/24'
          delegations: [{ name: 'flex', properties: { serviceName: 'Microsoft.App/environments' } }]
        }
      }
    ]
  }
}

resource dnsZones 'Microsoft.Network/privateDnsZones@2020-06-01' = [
  for zone in items(zones): {
    name: zone.value
    location: 'global'
    tags: tags
  }
]

resource dnsLinks 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2020-06-01' = [
  for (zone, i) in items(zones): {
    parent: dnsZones[i]
    name: names.vnet
    location: 'global'
    tags: tags
    properties: {
      virtualNetwork: { id: vnet.id }
      registrationEnabled: false
    }
  }
]

// ---------------------------------------------------------------------------------------------
// Messaging
// ---------------------------------------------------------------------------------------------

// Standard, not Premium: private endpoints on Service Bus are Premium-only (about $680/month).
// Public network access stays on, covered by a policy exemption in policy/main.bicep. What
// keeps it closed instead is Entra-only auth: no SAS keys exist to leak, and every sender or
// receiver needs a role scoped to a specific topic or subscription.
resource serviceBus 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name: names.serviceBusNamespace
  location: location
  tags: tags
  sku: { name: 'Standard', tier: 'Standard' }
  properties: {
    disableLocalAuth: true
    minimumTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
  }
}

resource serviceBusDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'to-log-analytics'
  scope: serviceBus
  properties: {
    workspaceId: logs.id
    logs: [{ categoryGroup: 'allLogs', enabled: true }]
    metrics: [{ category: 'AllMetrics', enabled: true }]
  }
}

// ---------------------------------------------------------------------------------------------
// Edge: Front Door -> APIM -> function apps
// ---------------------------------------------------------------------------------------------

resource frontDoor 'Microsoft.Cdn/profiles@2024-02-01' = {
  name: names.frontDoor
  location: 'global'
  tags: tags
  sku: { name: 'Standard_AzureFrontDoor' }
}

resource frontDoorEndpoint 'Microsoft.Cdn/profiles/afdEndpoints@2024-02-01' = {
  parent: frontDoor
  name: names.frontDoorEndpoint
  location: 'global'
  tags: tags
  properties: { enabledState: 'Enabled' }
}

resource apimPublicIp 'Microsoft.Network/publicIPAddresses@2023-11-01' = {
  name: 'pip-${names.apim}'
  location: location
  tags: tags
  sku: { name: 'Standard' }
  properties: {
    publicIPAllocationMethod: 'Static'
    dnsSettings: { domainNameLabel: names.apim }
  }
}

// External VNet mode: the gateway has a public IP (Front Door Standard can only reach public
// origins; Private Link origins are Premium) but lives in the VNet, so it can reach function
// apps that only accept traffic from snet-apim.
resource apim 'Microsoft.ApiManagement/service@2023-05-01-preview' = {
  name: names.apim
  location: location
  tags: tags
  sku: { name: 'Developer', capacity: 1 }
  properties: {
    publisherEmail: publisherEmail
    publisherName: 'Platform team'
    virtualNetworkType: 'External'
    virtualNetworkConfiguration: { subnetResourceId: '${vnet.id}/subnets/${names.apimSubnet}' }
    publicIpAddressId: apimPublicIp.id
  }
}

// Global policy: reject anything that didn't come through *our* Front Door profile. The NSG
// only proves "came from some Front Door"; the FDID header proves which one.
resource apimGlobalPolicy 'Microsoft.ApiManagement/service/policies@2023-05-01-preview' = {
  parent: apim
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: '<policies><inbound><check-header name="X-Azure-FDID" failed-check-httpcode="403" failed-check-error-message="Not via Front Door" ignore-case="true"><value>${frontDoor.properties.frontDoorId}</value></check-header></inbound><backend><forward-request /></backend><outbound /><on-error /></policies>'
  }
}

resource apimDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'to-log-analytics'
  scope: apim
  properties: {
    workspaceId: logs.id
    logs: [{ categoryGroup: 'allLogs', enabled: true }]
    metrics: [{ category: 'AllMetrics', enabled: true }]
  }
}

resource apimOrigins 'Microsoft.Cdn/profiles/originGroups@2024-02-01' = {
  parent: frontDoor
  name: names.frontDoorApimOriginGroup
  properties: {
    loadBalancingSettings: { sampleSize: 4, successfulSamplesRequired: 3, additionalLatencyInMilliseconds: 50 }
    healthProbeSettings: {
      probePath: '/status-0123456789abcdef'
      probeProtocol: 'Https'
      probeRequestType: 'GET'
      probeIntervalInSeconds: 100
    }
  }

  resource gateway 'origins' = {
    name: 'apim-gateway'
    properties: {
      hostName: replace(apim.properties.gatewayUrl, 'https://', '')
      originHostHeader: replace(apim.properties.gatewayUrl, 'https://', '')
      httpsPort: 443
      enforceCertificateNameCheck: true
    }
  }
}

resource frontDoorDiagnostics 'Microsoft.Insights/diagnosticSettings@2021-05-01-preview' = {
  name: 'to-log-analytics'
  scope: frontDoor
  properties: {
    workspaceId: logs.id
    logs: [{ categoryGroup: 'allLogs', enabled: true }]
    metrics: [{ category: 'AllMetrics', enabled: true }]
  }
}

// ---------------------------------------------------------------------------------------------
// Module registry: where release tags publish modules/<name> as br/platform:<name>:<version>
// ---------------------------------------------------------------------------------------------

resource registry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: names.containerRegistry
  location: location
  tags: tags
  sku: { name: 'Basic' }
  properties: { adminUserEnabled: false }
}

var acrPush = '8311e382-0749-4cb8-b61a-304f252e45ec'

resource modulePublisher 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, modulePublisherPrincipalId, acrPush)
  scope: registry
  properties: {
    principalId: modulePublisherPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPush)
  }
}

output frontDoorHostName string = frontDoorEndpoint.properties.hostName
output containerRegistryLoginServer string = registry.properties.loginServer
