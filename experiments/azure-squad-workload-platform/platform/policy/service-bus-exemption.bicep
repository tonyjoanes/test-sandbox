param namespaceName string
param assignmentId string

@description('Exemptions should expire. Six months from the first deployment is long enough for a PoC to finish or move to Premium.')
param expiresOn string = dateTimeAdd(utcNow(), 'P6M')

resource namespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' existing = {
  name: namespaceName
}

resource exemption 'Microsoft.Authorization/policyExemptions@2022-07-01-preview' = {
  name: 'service-bus-standard-no-private-endpoint'
  scope: namespace
  properties: {
    policyAssignmentId: assignmentId
    policyDefinitionReferenceIds: ['deny-public-network-access']
    exemptionCategory: 'Mitigated'
    displayName: 'Service Bus Standard cannot use private endpoints'
    description: 'Private endpoints need Premium (~$680/month). Mitigated by disableLocalAuth (Entra only) and topic/subscription-scoped RBAC. Remove when moving to Premium.'
    expiresOn: expiresOn
  }
}
