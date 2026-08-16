targetScope = 'subscription'

// ---------------------------------------------------------------------------
// 11 — Exemption: a scoped, time-boxed "sit this one out" for a resource
// (or resource group) that would otherwise be evaluated by an assignment.
//
// Unlike notScopes (set once on the assignment in 10, excluding a whole
// scope permanently), an exemption is its OWN resource — created
// separately, targeted at a specific scope, optionally limited to just
// SOME of the policies inside an initiative, and normally given an
// expiresOn so it can't be silently forgotten.
// ---------------------------------------------------------------------------

@description('Resource group the exemption applies to.')
param targetResourceGroupName string = 'example-rg'

@description('Resource ID of the policy assignment being exempted from. Paste the `assignmentId` output from 10-policy-assignment.bicep.')
param policyAssignmentId string

resource exemption 'Microsoft.Authorization/policyExemptions@2022-07-01-preview' = {
  name: 'example-rg-allowed-locations-waiver'
  scope: resourceGroup(targetResourceGroupName)
  properties: {
    policyAssignmentId: policyAssignmentId
    // Exempt only the allowedLocations rule from the initiative (09) —
    // every other policy in the baseline still applies to this scope.
    // Omit this property entirely to exempt from the WHOLE assignment.
    policyDefinitionReferenceIds: [
      'allowedLocations'
    ]
    exemptionCategory: 'Waiver' // 'Waiver' = accepted risk / agreed business reason. 'Mitigated' = the risk is addressed by a control Policy itself can't see.
    displayName: 'DR failover resources temporarily outside allowed regions'
    description: 'This resource group hosts a disaster-recovery drill that intentionally deploys to a non-standard region. Revisit before expiry.'
    expiresOn: '2026-12-31T00:00:00Z'
  }
}
