metadata name = 'servicebus-subscription'
metadata description = 'A subscription named after the consuming squad, on any squad\'s topic. Data Receiver is scoped to this subscription only, so a consumer cannot read other squads\' subscriptions on the same topic.'
metadata version = '1.0.0'

param namespaceName string

@description('Fully qualified topic: <publishing squad>.<topic>. validate.mjs has already checked that some squad publishes it.')
param topicName string

@description('The consuming squad. Becomes the subscription name.')
param subscriberSquad string

param receiverPrincipalIds string[]

var serviceBusDataReceiver = '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0'

resource namespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' existing = {
  name: namespaceName

  resource topic 'topics' existing = {
    name: topicName
  }
}

resource subscription 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2022-10-01-preview' = {
  parent: namespace::topic
  name: subscriberSquad
  properties: {
    maxDeliveryCount: 10
    deadLetteringOnMessageExpiration: true
  }
}

resource receivers 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for principalId in receiverPrincipalIds: {
    name: guid(subscription.id, principalId, serviceBusDataReceiver)
    scope: subscription
    properties: {
      principalId: principalId
      principalType: 'ServicePrincipal'
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', serviceBusDataReceiver)
    }
  }
]

output id string = subscription.id
