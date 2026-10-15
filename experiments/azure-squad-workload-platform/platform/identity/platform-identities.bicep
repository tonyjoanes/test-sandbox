// User-assigned managed identities with GitHub OIDC federated credentials: no app registration,
// no client secret, nothing to rotate. Each identity trusts exactly one token subject.

param location string
param tags object
param platformRepository string

var githubIssuer = 'https://token.actions.githubusercontent.com'

resource deployer 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-platform-deployer'
  location: location
  tags: tags

  // main branch only. A PR, a feature branch or a fork can't present this subject.
  resource fromMain 'federatedIdentityCredentials' = {
    name: 'github-main'
    properties: {
      issuer: githubIssuer
      subject: 'repo:${platformRepository}:ref:refs/heads/main'
      audiences: ['api://AzureADTokenExchange']
    }
  }
}

resource planner 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: 'id-platform-planner'
  location: location
  tags: tags

  resource fromPullRequests 'federatedIdentityCredentials' = {
    name: 'github-pull-requests'
    properties: {
      issuer: githubIssuer
      subject: 'repo:${platformRepository}:pull_request'
      audiences: ['api://AzureADTokenExchange']
    }
  }
}

output deployerClientId string = deployer.properties.clientId
output deployerPrincipalId string = deployer.properties.principalId
output plannerClientId string = planner.properties.clientId
output plannerPrincipalId string = planner.properties.principalId
