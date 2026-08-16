targetScope = 'subscription'

// ---------------------------------------------------------------------------
// 04 — Modify effect: adds, replaces, or removes fields on a resource —
// and, via remediation (see 12), can correct resources that already exist.
//
// Modify needs two things append/deny don't:
//   1. `roleDefinitionIds` — the policy ASSIGNMENT's managed identity must
//      hold a role that can actually perform the change (here: Tag
//      Contributor), because Modify runs the change as a real, auditable
//      write under that identity. See 10 for where the identity and role
//      assignment actually get created.
//   2. `conflictEffect` — what to do if the request already tries to set a
//      conflicting value for the same field: 'deny' the request outright,
//      or 'audit' it (let it through, but flag as non-compliant).
// ---------------------------------------------------------------------------

resource modifyEnvironmentTagPolicy 'Microsoft.Authorization/policyDefinitions@2021-06-01' = {
  name: 'modify-environment-tag'
  properties: {
    displayName: 'Force-correct the Environment tag to a parameter-supplied value'
    description: 'Adds or overwrites tags[Environment] on matching resources, including retroactively via a remediation task.'
    policyType: 'Custom'
    mode: 'Indexed'
    metadata: {
      category: 'Tags'
      version: '1.0.0'
    }
    parameters: {
      environmentValue: {
        type: 'String'
        allowedValues: [
          'Production'
          'Staging'
          'Development'
        ]
        metadata: {
          displayName: 'Environment'
          description: 'Value to force onto tags[Environment]'
        }
      }
    }
    policyRule: {
      if: {
        field: 'type'
        equals: 'Microsoft.Resources/subscriptions/resourceGroups'
      }
      then: {
        effect: 'modify'
        details: {
          // Built-in "Tag Contributor" role — lets the policy's identity
          // write tags without granting it broader write access. This
          // value is resolved when the DEFINITION is deployed (a plain
          // resourceId), unlike the policyRule's own [parameters(...)]
          // strings, which are resolved later by the Policy engine.
          roleDefinitionIds: [
            subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4a9ae827-6dc8-4573-8ac7-8239d42aa03f')
          ]
          conflictEffect: 'audit' // let a conflicting request through but flag it; use 'deny' to block conflicting requests instead
          operations: [
            {
              operation: 'addOrReplace' // also available: 'add' (only if absent), 'remove'
              field: 'tags[\'Environment\']'
              value: '[parameters(\'environmentValue\')]'
            }
          ]
        }
      }
    }
  }
}
