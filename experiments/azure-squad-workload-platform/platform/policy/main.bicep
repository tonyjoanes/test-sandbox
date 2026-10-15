// Bootstrap step 3, run once by a subscription Owner after the backbone exists:
//
//   az deployment sub create -l uksouth -f policy/main.bicep
//
// Three guardrails, assigned at subscription scope so they apply to every squad resource group
// the day it's created, including anything a squad deploys from its own infra/ folder. These
// are custom definitions rather than built-ins so the rule is readable right here; the
// built-in equivalents exist and would do the same job.

targetScope = 'subscription'

import { platformNames } from '../naming.bicep'

param location string = deployment().location

var names = platformNames(subscription().subscriptionId)

// ---------------------------------------------------------------------------------------------
// 1. Deny public network access on data services
// ---------------------------------------------------------------------------------------------
// `notEquals 'Disabled'` rather than `equals 'Enabled'`: an omitted publicNetworkAccess means
// the resource provider's default, which is Enabled for storage. Silence is not a choice.
//
// Function apps aren't in this list: their inbound path is closed by access restrictions to the
// APIM subnet instead (see modules/function-app/main.bicep for why), and the squad role can't
// change those.

var publicAccessTypes = [
  'Microsoft.Storage/storageAccounts'
  'Microsoft.KeyVault/vaults'
  'Microsoft.AppConfiguration/configurationStores'
  'Microsoft.ServiceBus/namespaces'
]

resource denyPublicAccess 'Microsoft.Authorization/policyDefinitions@2023-04-01' = {
  name: 'deny-public-network-access'
  properties: {
    displayName: 'Data services must disable public network access'
    policyType: 'Custom'
    mode: 'Indexed'
    policyRule: {
      if: {
        anyOf: [
          for type in publicAccessTypes: {
            allOf: [
              { field: 'type', equals: type }
              { field: '${type}/publicNetworkAccess', notEquals: 'Disabled' }
            ]
          }
        ]
      }
      then: { effect: 'deny' }
    }
  }
}

// ---------------------------------------------------------------------------------------------
// 2. Diagnostics must exist: DeployIfNotExists, not Deny
// ---------------------------------------------------------------------------------------------
// A diagnostic setting is an extension resource created *after* its parent, so a Deny on the
// parent would fire before the setting could possibly exist and block every deployment. DINE
// waits for the parent, checks, and deploys the setting if it's missing. The platform modules
// already create these settings, so in practice this catches what squads deploy themselves from
// infra/. Storage isn't in the list: its logs live on the blobServices child resource, which
// would need a definition of its own.

var diagnosticTypes = [
  'Microsoft.KeyVault/vaults'
  'Microsoft.AppConfiguration/configurationStores'
  'Microsoft.Web/sites'
  'Microsoft.ServiceBus/namespaces'
]

var logAnalyticsContributor = '92aaf0da-9dab-42b6-94a3-d43ce8d16293'
var monitoringContributor = '749f88d5-cbae-40b8-bcfc-e573ddc772fa'

resource requireDiagnostics 'Microsoft.Authorization/policyDefinitions@2023-04-01' = {
  name: 'require-diagnostics-to-platform-workspace'
  properties: {
    displayName: 'Send diagnostics to the platform Log Analytics workspace'
    policyType: 'Custom'
    mode: 'Indexed'
    parameters: {
      workspaceId: { type: 'String' }
    }
    policyRule: {
      if: { field: 'type', in: diagnosticTypes }
      then: {
        effect: 'deployIfNotExists'
        details: {
          type: 'Microsoft.Insights/diagnosticSettings'
          existenceCondition: {
            field: 'Microsoft.Insights/diagnosticSettings/workspaceId'
            equals: '[parameters(\'workspaceId\')]'
          }
          roleDefinitionIds: [
            '/providers/Microsoft.Authorization/roleDefinitions/${logAnalyticsContributor}'
            '/providers/Microsoft.Authorization/roleDefinitions/${monitoringContributor}'
          ]
          deployment: {
            properties: {
              mode: 'incremental'
              parameters: {
                resourceType: { value: '[field(\'type\')]' }
                resourceName: { value: '[field(\'name\')]' }
                location: { value: '[field(\'location\')]' }
                workspaceId: { value: '[parameters(\'workspaceId\')]' }
              }
              template: {
                '$schema': 'https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#'
                contentVersion: '1.0.0.0'
                parameters: {
                  resourceType: { type: 'string' }
                  resourceName: { type: 'string' }
                  location: { type: 'string' }
                  workspaceId: { type: 'string' }
                }
                resources: [
                  {
                    type: '[concat(parameters(\'resourceType\'), \'/providers/diagnosticSettings\')]'
                    apiVersion: '2021-05-01-preview'
                    name: '[concat(parameters(\'resourceName\'), \'/Microsoft.Insights/set-by-policy\')]'
                    location: '[parameters(\'location\')]'
                    properties: {
                      workspaceId: '[parameters(\'workspaceId\')]'
                      logs: [{ categoryGroup: 'allLogs', enabled: true }]
                      metrics: [{ category: 'AllMetrics', enabled: true }]
                    }
                  }
                ]
              }
            }
          }
        }
      }
    }
  }
}

// ---------------------------------------------------------------------------------------------
// 3. The squad tag: Deny on resource groups, inherit onto resources
// ---------------------------------------------------------------------------------------------
// Denying every untagged *resource* is noisy and squads route around noise. Deny at the
// resource group (which only the platform pipeline creates, always tagged), then copy the tag
// down with Modify so a squad's infra/ resources are tagged whether or not they remembered.

resource requireSquadTagOnRg 'Microsoft.Authorization/policyDefinitions@2023-04-01' = {
  name: 'require-squad-tag-on-resource-groups'
  properties: {
    displayName: 'Resource groups must have a squad tag'
    policyType: 'Custom'
    mode: 'All'
    policyRule: {
      if: {
        allOf: [
          { field: 'type', equals: 'Microsoft.Resources/subscriptions/resourceGroups' }
          { field: 'tags[\'squad\']', exists: 'false' }
        ]
      }
      then: { effect: 'deny' }
    }
  }
}

var contributor = 'b24988ac-6180-42a0-ab88-20f7382dd24c'

resource inheritSquadTag 'Microsoft.Authorization/policyDefinitions@2023-04-01' = {
  name: 'inherit-squad-tag-from-resource-group'
  properties: {
    displayName: 'Inherit the squad tag from the resource group'
    policyType: 'Custom'
    mode: 'Indexed'
    policyRule: {
      if: {
        allOf: [
          { field: 'tags[\'squad\']', notEquals: '[resourceGroup().tags[\'squad\']]' }
          { value: '[resourceGroup().tags[\'squad\']]', notEquals: '' }
        ]
      }
      then: {
        effect: 'modify'
        details: {
          roleDefinitionIds: ['/providers/Microsoft.Authorization/roleDefinitions/${contributor}']
          operations: [
            { operation: 'addOrReplace', field: 'tags[\'squad\']', value: '[resourceGroup().tags[\'squad\']]' }
          ]
        }
      }
    }
  }
}

// ---------------------------------------------------------------------------------------------
// One initiative, one assignment
// ---------------------------------------------------------------------------------------------

resource guardrails 'Microsoft.Authorization/policySetDefinitions@2023-04-01' = {
  name: 'squad-platform-guardrails'
  properties: {
    displayName: 'Squad platform guardrails'
    policyType: 'Custom'
    parameters: {
      workspaceId: { type: 'String' }
    }
    policyDefinitions: [
      { policyDefinitionReferenceId: 'deny-public-network-access', policyDefinitionId: denyPublicAccess.id }
      {
        policyDefinitionReferenceId: 'require-diagnostics'
        policyDefinitionId: requireDiagnostics.id
        parameters: { workspaceId: { value: '[parameters(\'workspaceId\')]' } }
      }
      { policyDefinitionReferenceId: 'require-squad-tag-on-rg', policyDefinitionId: requireSquadTagOnRg.id }
      { policyDefinitionReferenceId: 'inherit-squad-tag', policyDefinitionId: inheritSquadTag.id }
    ]
  }
}

resource assignment 'Microsoft.Authorization/policyAssignments@2024-04-01' = {
  name: 'squad-guardrails'
  location: location
  // DINE and Modify need an identity to make their changes with.
  identity: { type: 'SystemAssigned' }
  properties: {
    displayName: 'Squad platform guardrails'
    policyDefinitionId: guardrails.id
    parameters: {
      workspaceId: {
        value: resourceId(subscription().subscriptionId, names.backboneResourceGroup, 'Microsoft.OperationalInsights/workspaces', names.logAnalytics)
      }
    }
    nonComplianceMessages: [
      {
        policyDefinitionReferenceId: 'deny-public-network-access'
        message: 'Public network access must be Disabled. Use a private endpoint, or ask for the capability in your workload.yaml and the platform wires one up for you.'
      }
      {
        policyDefinitionReferenceId: 'require-squad-tag-on-rg'
        message: 'Resource groups are created by the platform pipeline from squads/<squad>/workload.yaml, not by hand.'
      }
    ]
  }
}

// The remediation identity needs its roles at the assignment's scope.
resource remediationRoles 'Microsoft.Authorization/roleAssignments@2022-04-01' = [
  for role in [logAnalyticsContributor, monitoringContributor, contributor]: {
    name: guid(assignment.id, role)
    properties: {
      principalId: assignment.identity.principalId
      principalType: 'ServicePrincipal'
      roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', role)
    }
  }
]

// ---------------------------------------------------------------------------------------------
// The one exemption: the Service Bus namespace (Standard tier can't take a private endpoint).
// Scoped to that single resource, with an expiry, so it has to be looked at again.
// ---------------------------------------------------------------------------------------------

module serviceBusExemption 'service-bus-exemption.bicep' = {
  name: 'service-bus-exemption'
  scope: resourceGroup(names.backboneResourceGroup)
  params: {
    namespaceName: names.serviceBusNamespace
    assignmentId: assignment.id
  }
}
