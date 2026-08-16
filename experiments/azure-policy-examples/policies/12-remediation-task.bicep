targetScope = 'subscription'

// ---------------------------------------------------------------------------
// 12 — Remediation: deployIfNotExists (06) and modify (04) only act on
// resources at the moment they're created or updated. Anything that
// already existed before the assignment went live stays non-compliant
// forever unless something retroactively fixes it — that "something" is a
// remediation task.
//
// A remediation task re-runs one policyDefinitionReferenceId from a given
// assignment against its already-evaluated compliance results, and applies
// the fix (deploy the template / apply the modify operations) to every
// resource currently flagged non-compliant.
// ---------------------------------------------------------------------------

@description('Resource group to remediate.')
param targetResourceGroupName string = 'example-rg'

@description('Resource ID of the policy assignment to remediate. Paste the `assignmentId` output from 10-policy-assignment.bicep.')
param policyAssignmentId string

resource remediation 'Microsoft.PolicyInsights/remediations@2021-10-01' = {
  name: 'fix-existing-environment-tags'
  scope: resourceGroup(targetResourceGroupName)
  properties: {
    policyAssignmentId: policyAssignmentId
    // Which policy INSIDE the initiative to remediate — matches the
    // policyDefinitionReferenceId set in 09, not the underlying
    // definition's own name.
    policyDefinitionReferenceId: 'modifyEnvironmentTag'
    // 'ExistingNonCompliant' = fix resources already known to be
    // non-compliant from the last compliance scan. 'ReEvaluateCompliance'
    // forces a fresh scan first, in case the assignment or definition
    // changed since the last one ran.
    resourceDiscoveryMode: 'ExistingNonCompliant'
  }
}
