// Example: the same squad's payments workload graduating from the sandbox above into a real
// landing zone — production-track policy, corp network connectivity, a spend cap sized for a
// real workload rather than a prototype.

using '../main.bicep'

param subscriptionDisplayName = 'sq-payments-prod'
param billingScopeId = '/providers/Microsoft.Billing/billingAccounts/<account-id>/billingProfiles/<profile-id>/invoiceSections/<section-id>'
param tier = 'landingZone'
param managementGroupId = '/providers/Microsoft.Management/managementGroups/contoso-landing-zones-online'
param ownerGroupObjectId = '<entra-object-id-of-payments-squad-owners-group>'
param monthlyBudgetAmount = 15000
param notificationEmails = [
  'payments-squad@contoso.com'
  'platform-cost-management@contoso.com'
]
param spokeAddressPrefix = '10.42.8.0/22'
param hubVnetResourceId = '/subscriptions/<platform-connectivity-subscription-id>/resourceGroups/rg-hub-network/providers/Microsoft.Network/virtualNetworks/vnet-hub'
param location = 'uksouth'
