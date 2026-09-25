targetScope = 'subscription'

// ---------------------------------------------------------------------------
// 06 — deployIfNotExists (DINE): the "self-healing" effect. If the related
// resource described in "details" doesn't exist, or exists but doesn't
// match existenceCondition, Policy deploys the ARM template in
// `deployment` to create/fix it.
//
// Requires:
//   - roleDefinitionIds: like Modify, the assignment's managed identity
//     needs a role that can perform the deployment (here: Monitoring
//     Contributor, to write a diagnostic setting).
//   - deployment.properties.template: a normal ARM JSON template, not
//     Bicep — Policy evaluates raw ARM at runtime, so this is one place
//     Bicep can't avoid nested JSON. Its `[parameters('x')]` calls resolve
//     against THIS INNER template's own parameters block, not the outer
//     policy's parameters — the two are easy to conflate because they look
//     identical.
// ---------------------------------------------------------------------------

resource deployDiagnosticSettingsPolicy 'Microsoft.Authorization/policyDefinitions@2021-06-01' = {
  name: 'deploy-storage-diagnostic-settings'
  properties: {
    displayName: 'Deploy diagnostic settings for storage accounts to a Log Analytics workspace'
    description: 'If a storage account has no diagnostic setting sending logs to the target workspace, deploys one automatically.'
    policyType: 'Custom'
    mode: 'Indexed'
    metadata: {
      category: 'Monitoring'
      version: '1.0.0'
    }
    parameters: {
      logAnalyticsWorkspaceId: {
        type: 'String'
        metadata: {
          displayName: 'Log Analytics Workspace'
          description: 'Resource ID of the workspace diagnostic logs should be sent to'
          strongType: 'Microsoft.OperationalInsights/workspaces' // tells the Portal to render a workspace picker instead of a free-text box
        }
      }
    }
    policyRule: {
      if: {
        field: 'type'
        equals: 'Microsoft.Storage/storageAccounts'
      }
      then: {
        effect: 'deployIfNotExists'
        details: {
          type: 'Microsoft.Insights/diagnosticSettings'
          existenceCondition: {
            field: 'Microsoft.Insights/diagnosticSettings/workspaceId'
            equals: '[parameters(\'logAnalyticsWorkspaceId\')]' // outer (policyRule) parameter — this one IS shared with the definition's parameters block above
          }
          roleDefinitionIds: [
            subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '749f88d5-cbae-40b8-bcfc-e573ddc772fa') // Monitoring Contributor
          ]
          deployment: {
            properties: {
              mode: 'incremental'
              // --- everything below is a self-contained ARM template ---
              template: {
                '$schema': 'https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#'
                contentVersion: '1.0.0.0'
                parameters: {
                  resourceName: {
                    type: 'string'
                  }
                  logAnalyticsWorkspaceId: {
                    type: 'string'
                  }
                }
                resources: [
                  {
                    type: 'Microsoft.Storage/storageAccounts/providers/diagnosticSettings'
                    apiVersion: '2021-05-01-preview'
                    name: '[concat(parameters(\'resourceName\'), \'/Microsoft.Insights/send-to-log-analytics\')]' // inner template's own parameter, NOT the policy's
                    properties: {
                      workspaceId: '[parameters(\'logAnalyticsWorkspaceId\')]' // inner template's own parameter
                      metrics: [
                        {
                          category: 'Transaction'
                          enabled: true
                        }
                      ]
                    }
                  }
                ]
              }
              // This is where the OUTER (policy-level) values get handed
              // into the INNER template's parameters — the bridge between
              // the two parameter scopes above.
              parameters: {
                resourceName: {
                  value: '[field(\'name\')]' // field() reads a value off the resource that triggered evaluation — here, the storage account's own name
                }
                logAnalyticsWorkspaceId: {
                  value: '[parameters(\'logAnalyticsWorkspaceId\')]'
                }
              }
            }
          }
        }
      }
    }
  }
}
