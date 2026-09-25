targetScope = 'subscription'

// ---------------------------------------------------------------------------
// 02 — Deny effect: blocks the create/update request outright.
//
// `deny` runs at admission time — before the resource is written to Azure.
// A failed evaluation returns an error to whoever (or whatever pipeline)
// issued the request; nothing gets created or updated. This is the effect
// people mean when they say "policy stopped my deployment." Because the
// blast radius is "nobody can create this resource shape anywhere in
// scope," always validate with `audit` (01) first before flipping to
// `deny`.
// ---------------------------------------------------------------------------

resource denyInsecureStoragePolicy 'Microsoft.Authorization/policyDefinitions@2021-06-01' = {
  name: 'deny-storage-without-https-only'
  properties: {
    displayName: 'Deny storage accounts that do not enforce HTTPS-only traffic'
    description: 'Blocks creation or update of a storage account unless supportsHttpsTrafficOnly is true. Demonstrates allOf (AND) and a resource-provider alias.'
    policyType: 'Custom'
    mode: 'Indexed'
    metadata: {
      category: 'Storage'
      version: '1.0.0'
    }
    policyRule: {
      if: {
        // allOf = logical AND: every nested condition must be true for the
        // "if" to match as a whole. (See 08 for anyOf/not and nesting.)
        allOf: [
          {
            field: 'type'
            equals: 'Microsoft.Storage/storageAccounts'
          }
          {
            // This is a *resource provider alias* — a path into the
            // resource's properties that Policy knows how to read, even
            // though it isn't a generic field like "type" or "location".
            // List aliases for a resource type with:
            //   az provider show --namespace Microsoft.Storage --expand "resourceTypes/aliases" --query "resourceTypes[?resourceType=='storageAccounts'].aliases[].name"
            field: 'Microsoft.Storage/storageAccounts/supportsHttpsTrafficOnly'
            notEquals: 'true'
          }
        ]
      }
      then: {
        effect: 'deny'
      }
    }
  }
}
