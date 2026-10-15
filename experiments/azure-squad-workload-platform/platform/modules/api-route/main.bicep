metadata name = 'api-route'
metadata description = 'Publishes a squad function at <frontdoor-endpoint>/<path> via APIM. Deployed into the backbone resource group, since APIM and Front Door live there.'
metadata version = '1.0.0'

param apimName string
param frontDoorProfileName string
param frontDoorEndpointName string

@description('The backbone origin group that points at the APIM gateway.')
param frontDoorOriginGroupName string

param squadName string

@description('Without the leading slash, e.g. orders.')
param path string

param functionHostName string

resource apim 'Microsoft.ApiManagement/service@2023-05-01-preview' existing = {
  name: apimName
}

resource api 'Microsoft.ApiManagement/service/apis@2023-05-01-preview' = {
  parent: apim
  name: squadName
  properties: {
    displayName: squadName
    path: path
    protocols: ['https']
    serviceUrl: 'https://${functionHostName}/api'
    // PoC: no APIM subscription key, so the demo is a plain curl. A real rollout would
    // require one (or validate-jwt) here.
    subscriptionRequired: false
  }

  // Wildcard operations, so the squad can add routes in code without a platform change.
  resource operations 'operations' = [
    for method in ['GET', 'POST', 'PUT', 'DELETE']: {
      name: toLower(method)
      properties: {
        displayName: method
        method: method
        urlTemplate: '/*'
      }
    }
  ]
}

resource frontDoor 'Microsoft.Cdn/profiles@2024-02-01' existing = {
  name: frontDoorProfileName

  resource endpoint 'afdEndpoints' existing = {
    name: frontDoorEndpointName
  }

  resource apimOrigins 'originGroups' existing = {
    name: frontDoorOriginGroupName
  }
}

resource route 'Microsoft.Cdn/profiles/afdEndpoints/routes@2024-02-01' = {
  parent: frontDoor::endpoint
  name: squadName
  properties: {
    originGroup: { id: frontDoor::apimOrigins.id }
    patternsToMatch: ['/${path}', '/${path}/*']
    supportedProtocols: ['Https']
    forwardingProtocol: 'HttpsOnly'
    httpsRedirect: 'Disabled'
    linkToDefaultDomain: 'Enabled'
  }
  dependsOn: [api]
}

output url string = 'https://${frontDoor::endpoint.properties.hostName}/${path}'
