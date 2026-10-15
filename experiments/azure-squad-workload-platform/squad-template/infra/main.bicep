// The escape hatch: infrastructure the manifest doesn't offer, deployed by the squad's own
// pipeline with the squad's own identity, into the squad's own resource group.
//
//   az deployment group create -g rg-squad-orders -f infra/main.bicep \
//     -p squadEmail=orders@example.com functionAppName=<from platform output>
//
// What bounds it is not review, it's the platform:
//   - RBAC (Squad Workload Contributor): this RG only; no networking, no role assignments,
//     no Key Vault / App Config / function plan of its own.
//   - Policy: no public data services, diagnostics enforced, squad tag inherited.
//
// The example is deliberately mundane: an alert on the squad's own function failures. That's
// the kind of thing that shouldn't need a platform ticket.

param squadEmail string

@description('The functionAppName output of the platform\'s squad deployment, also stored as a repo variable.')
param functionAppName string

resource functionApp 'Microsoft.Web/sites@2024-04-01' existing = {
  name: functionAppName
}

resource oncall 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: 'ag-squad-oncall'
  location: 'global'
  properties: {
    groupShortName: 'oncall'
    enabled: true
    emailReceivers: [{ name: 'squad', emailAddress: squadEmail, useCommonAlertSchema: true }]
  }
}

resource httpErrors 'Microsoft.Insights/metricAlerts@2018-03-01' = {
  name: 'alert-http-5xx'
  location: 'global'
  properties: {
    severity: 2
    enabled: true
    scopes: [functionApp.id]
    evaluationFrequency: 'PT5M'
    windowSize: 'PT15M'
    criteria: {
      'odata.type': 'Microsoft.Azure.Monitor.SingleResourceMultipleMetricCriteria'
      allOf: [
        {
          name: 'http5xx'
          criterionType: 'StaticThresholdCriterion'
          metricName: 'Http5xx'
          operator: 'GreaterThan'
          threshold: 5
          timeAggregation: 'Total'
        }
      ]
    }
    actions: [{ actionGroupId: oncall.id }]
  }
}
