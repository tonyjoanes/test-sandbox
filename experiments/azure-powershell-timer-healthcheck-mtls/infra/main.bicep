// Reference infrastructure for the timer-triggered mTLS health-check
// Function App. Illustrative, not turnkey — review names, SKUs, and
// network settings for your environment before applying.
//
// Deliberate choices and why:
//  - Plan: Elastic Premium (EP1), not Consumption (Y1). Private
//    certificates (WEBSITE_LOAD_CERTIFICATES) and custom Startup Commands
//    are unreliable on Linux Consumption — see README "Known limitation".
//    Premium also avoids cold-start jitter on a 5-minute timer and allows
//    VNet integration to reach an internal endpoint.
//  - Managed identity + Key Vault: the client certificate's private key
//    never appears in an app setting or in source control. It is imported
//    into the Function App as a "Private Key Certificate" sourced from
//    Key Vault; only WEBSITE_LOAD_CERTIFICATES (a thumbprint, not a
//    secret) is stored as an app setting.

@description('Short, unique name used as a prefix for all resources.')
param namePrefix string

@description('Azure region for all resources.')
param location string = resourceGroup().location

@description('Key Vault resource ID that already contains the client certificate (import it there first, e.g. via az keyvault certificate import).')
param keyVaultResourceId string

@description('Name of the certificate object in Key Vault (holds the mTLS client cert + private key).')
param clientCertKeyVaultCertName string

@description('Internal health-check endpoint, e.g. https://internal-app.contoso.internal/health')
param healthCheckTargetUrl string

@description('Comma-separated thumbprints of the internal root/issuing CA certs, for startup.sh to trust. Leave empty if the internal endpoint already chains to a public CA.')
param internalCaThumbprints string = ''

var storageAccountName = toLower('${namePrefix}sa')
var planName = '${namePrefix}-plan'
var functionAppName = '${namePrefix}-func'
var appInsightsName = '${namePrefix}-ai'
var logAnalyticsName = '${namePrefix}-law'

resource storage 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: storageAccountName
  location: location
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
  }
}

resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2022-10-01' = {
  name: logAnalyticsName
  location: location
  properties: {
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
  }
}

resource plan 'Microsoft.Web/serverfarms@2023-01-01' = {
  name: planName
  location: location
  kind: 'elastic'
  sku: {
    name: 'EP1'
    tier: 'ElasticPremium'
  }
  properties: {
    reserved: true // required for Linux
  }
}

resource functionApp 'Microsoft.Web/sites@2023-01-01' = {
  name: functionAppName
  location: location
  kind: 'functionapp,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: plan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'PowerShell|7.4'
      // Points at /home, the persistent share mounted into every instance —
      // NOT a path inside the function code package (that content isn't
      // guaranteed to survive every container recycle the same way /home
      // does). deploy.sh uploads infra/startup.sh there via the Kudu VFS API
      // before the app is expected to serve traffic.
      appCommandLine: '/home/startup.sh'
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      appSettings: [
        { name: 'AzureWebJobsStorage', value: 'DefaultEndpointsProtocol=https;AccountName=${storage.name};AccountKey=${storage.listKeys().keys[0].value};EndpointSuffix=${environment().suffixes.storage}' }
        { name: 'FUNCTIONS_WORKER_RUNTIME', value: 'powershell' }
        { name: 'FUNCTIONS_EXTENSION_VERSION', value: '~4' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
        { name: 'HEALTHCHECK_TARGET_URL', value: healthCheckTargetUrl }
        { name: 'HEALTHCHECK_TIMEOUT_SECONDS', value: '10' }
        { name: 'HEALTHCHECK_MAX_ATTEMPTS', value: '2' }
        { name: 'CLIENT_CERT_THUMBPRINT', value: clientCert.properties.thumbprint }
        // thumbprint list, not the cert itself — safe to store as a plain app setting
        { name: 'WEBSITE_LOAD_CERTIFICATES', value: clientCert.properties.thumbprint }
        { name: 'INTERNAL_CA_THUMBPRINTS', value: internalCaThumbprints }
      ]
    }
  }
}

// Imports the client certificate from Key Vault as a Private Key Certificate
// on the Function App. Requires the Function App's managed identity (or the
// deployment principal) to already have "get" access on secrets/certificates
// in the target Key Vault — grant that before this deploys.
resource clientCert 'Microsoft.Web/certificates@2023-01-01' = {
  name: '${namePrefix}-client-cert'
  location: location
  properties: {
    serverFarmId: plan.id
    keyVaultId: keyVaultResourceId
    keyVaultSecretName: clientCertKeyVaultCertName
  }
}

output functionAppName string = functionApp.name
output functionAppPrincipalId string = functionApp.identity.principalId
output appliedClientCertThumbprint string = clientCert.properties.thumbprint
