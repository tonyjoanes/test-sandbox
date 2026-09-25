// Example: a squad requesting a pure-innovation sandbox. Fast to approve — no network, no
// production support expectation, small spend cap. See ../../intake/request-template.md for
// the request this would come from.

using '../main.bicep'

param subscriptionDisplayName = 'sq-payments-sandbox'
param billingScopeId = '/providers/Microsoft.Billing/billingAccounts/<account-id>/billingProfiles/<profile-id>/invoiceSections/<section-id>'
param tier = 'sandbox'
param managementGroupId = '/providers/Microsoft.Management/managementGroups/contoso-sandboxes'
param ownerGroupObjectId = '<entra-object-id-of-payments-squad-owners-group>'
param monthlyBudgetAmount = 500
param notificationEmails = [
  'payments-squad@contoso.com'
]

// spokeAddressPrefix and hubVnetResourceId are intentionally left at their defaults ('') —
// tier=sandbox never deploys ../modules/network-spoke.bicep, so there's nothing to fill in.
