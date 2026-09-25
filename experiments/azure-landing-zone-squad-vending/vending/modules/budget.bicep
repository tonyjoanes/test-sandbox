// The financial half of "safe to innovate in." Policy (../../policy/) constrains what a squad
// CAN deploy; this constrains how much it can cost before someone is paged. Both tiers get a
// budget — the difference between a sandbox and a landing zone is the number that goes in
// `monthlyBudgetAmount` at the call site (see ../params/), not whether this module runs at all.

targetScope = 'subscription'

@description('REQUIRED. Monthly spend cap in the billing currency. No default — every landing zone declares its own ceiling explicitly; there is no organization-wide default that could silently apply to a workload it was never sized for.')
param monthlyBudgetAmount int

@description('REQUIRED. Email address(es) notified as spend approaches and crosses the cap.')
param notificationEmails array

@description('Budget start date, defaulted to the first of the current month. `utcNow()` is only legal in a parameter default in Bicep — every deploy of this template re-evaluates it, which is exactly what "start this month" needs.')
param budgetStartDate string = utcNow('yyyy-MM-01')

resource platformRg 'Microsoft.Resources/resourceGroups@2023-07-01' = {
  name: 'rg-platform-scaffolding'
  location: deployment().location
}

resource spendAlerts 'Microsoft.Insights/actionGroups@2023-01-01' = {
  name: 'ag-spend-alerts'
  location: 'global'
  scope: platformRg
  properties: {
    groupShortName: 'spendalert'
    enabled: true
    emailReceivers: [
      for (email, i) in notificationEmails: {
        name: 'email-${i}'
        emailAddress: email
        useCommonAlertSchema: true
      }
    ]
  }
}

resource budget 'Microsoft.Consumption/budgets@2023-11-01' = {
  name: 'monthly-spend-cap'
  properties: {
    category: 'Cost'
    amount: monthlyBudgetAmount
    timeGrain: 'Monthly'
    timePeriod: {
      startDate: budgetStartDate
    }
    notifications: {
      warning50: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 50
        contactEmails: notificationEmails
        contactGroups: [spendAlerts.id]
        thresholdType: 'Actual'
      }
      warning80: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 80
        contactEmails: notificationEmails
        contactGroups: [spendAlerts.id]
        thresholdType: 'Actual'
      }
      // Forecasted, not Actual: fires when the CURRENT trend projects going over the cap by
      // month end, not only after it already has. The whole point of a spend cap on a sandbox
      // is the warning arriving while there's still time to do something about it.
      forecastExceedsCap: {
        enabled: true
        operator: 'GreaterThanOrEqualTo'
        threshold: 100
        contactEmails: notificationEmails
        contactGroups: [spendAlerts.id]
        thresholdType: 'Forecasted'
      }
    }
  }
}
