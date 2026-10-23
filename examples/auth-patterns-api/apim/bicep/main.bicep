// APIM API, operations, named values and policies for the auth-patterns example.
// Illustrative only - not deployed. Assumes an existing APIM instance with a system-assigned identity
// that has "Key Vault Secrets User" on the vault.
param apimName string
param backendUrl string
param tenantId string
param apiAudience string
param backendAppIdUri string
param keyVaultUri string // e.g. https://kv-name.vault.azure.net/

resource apim 'Microsoft.ApiManagement/service@2023-05-01-preview' existing = {
  name: apimName
}

var plainNamedValues = {
  'tenant-id': tenantId
  'api-audience': apiAudience
  'backend-app-id-uri': backendAppIdUri
}

resource nv 'Microsoft.ApiManagement/service/namedValues@2023-05-01-preview' = [for item in items(plainNamedValues): {
  parent: apim
  name: item.key
  properties: {
    displayName: item.key
    value: item.value
  }
}]

resource kvSecrets 'Microsoft.ApiManagement/service/namedValues@2023-05-01-preview' = [for slot in [ 'primary', 'secondary' ]: {
  parent: apim
  name: 'webhook-secret-${slot}'
  properties: {
    displayName: 'webhook-secret-${slot}'
    secret: true
    keyVault: {
      secretIdentifier: '${keyVaultUri}secrets/webhook-secret-${slot}'
    }
  }
}]

resource fragment 'Microsoft.ApiManagement/service/policyFragments@2023-05-01-preview' = {
  parent: apim
  name: 'backend-trust'
  properties: {
    format: 'rawxml'
    value: loadTextContent('../policies/backend-trust.fragment.xml')
  }
  dependsOn: [ nv ]
}

resource api 'Microsoft.ApiManagement/service/apis@2023-05-01-preview' = {
  parent: apim
  name: 'auth-patterns'
  properties: {
    displayName: 'Auth patterns example'
    path: 'auth-patterns'
    protocols: [ 'https' ]
    serviceUrl: backendUrl
    subscriptionRequired: false
  }
}

resource getOrders 'Microsoft.ApiManagement/service/apis/operations@2023-05-01-preview' = {
  parent: api
  name: 'get-orders'
  properties: {
    displayName: 'Get orders'
    method: 'GET'
    urlTemplate: '/orders'
  }
}

resource getOrdersPolicy 'Microsoft.ApiManagement/service/apis/operations/policies@2023-05-01-preview' = {
  parent: getOrders
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: loadTextContent('../policies/validate-jwt.xml')
  }
  dependsOn: [ fragment ]
}

resource webhook 'Microsoft.ApiManagement/service/apis/operations@2023-05-01-preview' = {
  parent: api
  name: 'post-webhook-orders'
  properties: {
    displayName: 'Order webhook'
    method: 'POST'
    urlTemplate: '/webhooks/orders'
  }
}

resource webhookPolicy 'Microsoft.ApiManagement/service/apis/operations/policies@2023-05-01-preview' = {
  parent: webhook
  name: 'policy'
  properties: {
    format: 'rawxml'
    value: loadTextContent('../policies/verify-webhook-hmac.xml')
  }
  dependsOn: [ fragment, kvSecrets ]
}
