metadata name = 'servicebus-topic'
metadata description = 'A squad-owned topic in the shared namespace, named <squad>.<topic>. Deployed into the backbone resource group; RBAC is scoped to the topic, not the namespace.'
metadata version = '1.0.0'

// No private endpoint or diagnostics parameter here, on purpose: both belong to the namespace,
// which the backbone owns (backbone/main.bicep). Private endpoints on Service Bus need the
// Premium tier; see README "Where the original plan doesn't hold".

param namespaceName string

@description('Fully qualified: <squad>.<topic>.')
param topicName string

@description('Principals that get Azure Service Bus Data Sender on this topic only.')
param senderPrincipalIds string[]

var serviceBusDataSender = '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39'

resource namespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' existing = {
  name: namespaceName
}

resource topic 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' = {
  parent: namespace
  name: topicName
  properties: {
    defaultMessageTimeToLive: 'P7D'
    maxSizeInMegabytes: 1024
  }
}

resource senders 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for principalId in senderPrincipalIds: {
    name: guid(topic.id, principalId, serviceBusDataSender)
    scope: topic
    properties: {
      principalId: principalId
      principalType: 'ServicePrincipal'
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', serviceBusDataSender)
    }
  }
]

output id string = topic.id
