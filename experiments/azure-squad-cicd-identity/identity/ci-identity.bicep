// Creates ONE CI identity for ONE tier of ONE squad: an Entra app registration + service
// principal, trusted via a federated credential instead of a client secret. No secret ever
// exists to leak, rotate, or land in a YAML file by accident — the trust relationship is "a
// token from this exact Azure DevOps org/project/service-connection", not "whoever holds this
// string."
//
// Deploy this TWICE per squad — once with tier=sandbox, once with tier=landingZone (see
// params/) — producing two separate app registrations, not one app with two federated
// credentials. That's deliberate, not an oversight: see "Why two identities, not one" in
// ../README.md. The short version — a federated credential controls who can EXCHANGE a token
// for this identity; it does not scope what that identity can then DO. If sandbox and
// landing-zone pipelines shared one app, ci-identity-rbac.bicep granting that app Contributor
// on the landing-zone subscription would hand landing-zone rights to the sandbox pipeline too,
// the moment both credentials existed on the same principal.
//
// Uses the Microsoft Graph Bicep extension (`Microsoft.Graph/applications`) to manage the app
// registration as code. Illustrative: the extension has moved fast since GA — check the
// resource API version and property names against your installed extension version.

targetScope = 'tenant'

extension microsoftGraph

@allowed([
  'sandbox'
  'landingZone'
])
@description('REQUIRED. Which tier this CI identity is for. Drives the app\'s display name and the federated credential\'s subject claim — never both tiers on one identity.')
param tier string

@description('REQUIRED. Squad name, e.g. "payments".')
param squadName string

@description('REQUIRED. Azure DevOps organization name.')
param devOpsOrganization string

@description('REQUIRED. Azure DevOps project name.')
param devOpsProject string

var appDisplayName = 'ci-${squadName}-${tier}'

resource ciApp 'Microsoft.Graph/applications@v1.0' = {
  displayName: appDisplayName
  signInAudience: 'AzureADMyOrg'
}

resource ciServicePrincipal 'Microsoft.Graph/servicePrincipals@v1.0' = {
  appId: ciApp.appId
}

// The subject claim names an Azure DevOps SERVICE CONNECTION, not a branch or repo. A pipeline
// only gets a token for this identity by running through a service connection literally named
// "<squad>-<tier>" — see ../pipelines/templates/deploy-stage.yml, where `serviceConnection`
// is passed in per tier and must match this exactly.
resource federatedCredential 'Microsoft.Graph/applications/federatedIdentityCredentials@v1.0' = {
  parent: ciApp
  name: '${appDisplayName}-fic'
  audiences: [
    'api://AzureADTokenExchange'
  ]
  issuer: 'https://vstoken.dev.azure.com/${devOpsOrganization}'
  subject: 'sc://${devOpsOrganization}/${devOpsProject}/${squadName}-${tier}'
}

@description('The CI service principal\'s object ID. Grant this — never a client secret — access at subscription scope; see ci-identity-rbac.bicep.')
output servicePrincipalObjectId string = ciServicePrincipal.id

@description('The application (client) ID, needed alongside the tenant and target subscription ID when creating the matching Workload Identity Federation service connection in Azure DevOps. Not a secret by itself.')
output applicationClientId string = ciApp.appId
