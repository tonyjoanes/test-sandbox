targetScope = 'subscription'

// ---------------------------------------------------------------------------
// 05 — auditIfNotExists: audits the resource matched by "if" based on
// whether a RELATED resource (usually a child, like an extension or
// diagnostic setting) exists and matches a condition.
//
// This is the read-only sibling of deployIfNotExists (06) — same "does the
// related thing exist and look right?" check, but it only flags
// non-compliance instead of fixing it for you.
// ---------------------------------------------------------------------------

resource auditMissingAntimalwarePolicy 'Microsoft.Authorization/policyDefinitions@2021-06-01' = {
  name: 'audit-vm-missing-antimalware-extension'
  properties: {
    displayName: 'Audit virtual machines without the antimalware extension installed'
    description: 'Flags VMs that do not have an IaaSAntimalware extension resource as a child.'
    policyType: 'Custom'
    mode: 'Indexed'
    metadata: {
      category: 'Compute'
      version: '1.0.0'
    }
    policyRule: {
      if: {
        field: 'type'
        equals: 'Microsoft.Compute/virtualMachines'
      }
      then: {
        effect: 'auditIfNotExists'
        details: {
          // `type` here is the CHILD resource type Policy looks for
          // underneath the resource matched by "if" (a virtual machine) —
          // not the type of the VM itself.
          type: 'Microsoft.Compute/virtualMachines/extensions'
          existenceCondition: {
            field: 'Microsoft.Compute/virtualMachines/extensions/type'
            equals: 'IaaSAntimalware'
          }
        }
      }
    }
  }
}
