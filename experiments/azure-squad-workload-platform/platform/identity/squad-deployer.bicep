// Called by squad/main.bicep. The squad deployer identity lives in the platform's identity
// resource group, not the squad's: if it lived in the squad RG, a squad could add its own
// federated credentials to it and let any repo deploy as them.

param name string
param location string
param tags object

@description('owner/name of the squad code repo, from the manifest.')
param repository string

resource deployer 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: name
  location: location
  tags: tags

  // Only jobs that target the repo's "production" GitHub environment, so the squad's own
  // environment protection rules (reviewers, branch filters) gate every deploy.
  resource fromProduction 'federatedIdentityCredentials' = {
    name: 'github-production'
    properties: {
      issuer: 'https://token.actions.githubusercontent.com'
      subject: 'repo:${repository}:environment:production'
      audiences: ['api://AzureADTokenExchange']
    }
  }
}

output clientId string = deployer.properties.clientId
output principalId string = deployer.properties.principalId
