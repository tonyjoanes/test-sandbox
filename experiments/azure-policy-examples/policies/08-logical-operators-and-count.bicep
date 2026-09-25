targetScope = 'subscription'

// ---------------------------------------------------------------------------
// 08 — Combining allOf / anyOf / not, and the count() field function for
// evaluating ARRAY properties (like an NSG's list of security rules).
//
// count() with a `where` sub-condition answers "how many items in this
// array match X?" — here: how many inbound Allow rules open RDP (3389)
// from any source? If that count is 1 or more, the NSG is flagged. This is
// the pattern behind most built-in "audit open management ports" policies.
// ---------------------------------------------------------------------------

resource auditOpenRdpPolicy 'Microsoft.Authorization/policyDefinitions@2021-06-01' = {
  name: 'audit-nsg-open-rdp-from-internet'
  properties: {
    displayName: 'Audit network security groups that allow RDP from the internet'
    description: 'Flags any NSG with an inbound Allow rule exposing port 3389 to a wildcard/Internet source.'
    policyType: 'Custom'
    mode: 'Indexed'
    metadata: {
      category: 'Network'
      version: '1.0.0'
    }
    policyRule: {
      if: {
        // allOf = AND: both the resource-type check and the count()
        // condition must hold.
        allOf: [
          {
            field: 'type'
            equals: 'Microsoft.Network/networkSecurityGroups'
          }
          {
            count: {
              // [*] means "every item in this array property" — count()
              // iterates the NSG's securityRules and counts how many
              // satisfy the nested `where`.
              field: 'Microsoft.Network/networkSecurityGroups/securityRules[*]'
              where: {
                allOf: [
                  {
                    field: 'Microsoft.Network/networkSecurityGroups/securityRules[*].direction'
                    equals: 'Inbound'
                  }
                  {
                    field: 'Microsoft.Network/networkSecurityGroups/securityRules[*].access'
                    equals: 'Allow'
                  }
                  {
                    // anyOf = OR: either port literal counts as "opens RDP".
                    anyOf: [
                      {
                        field: 'Microsoft.Network/networkSecurityGroups/securityRules[*].destinationPortRange'
                        equals: '3389'
                      }
                      {
                        field: 'Microsoft.Network/networkSecurityGroups/securityRules[*].destinationPortRange'
                        equals: '*'
                      }
                    ]
                  }
                  {
                    anyOf: [
                      {
                        field: 'Microsoft.Network/networkSecurityGroups/securityRules[*].sourceAddressPrefix'
                        equals: '*'
                      }
                      {
                        field: 'Microsoft.Network/networkSecurityGroups/securityRules[*].sourceAddressPrefix'
                        equals: 'Internet'
                      }
                    ]
                  }
                ]
              }
            }
            greaterOrEquals: 1
          }
        ]
      }
      then: {
        effect: 'audit'
      }
    }
  }
}
