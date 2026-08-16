targetScope = 'subscription'

// ---------------------------------------------------------------------------
// 03 — Append effect: silently adds a field to the request, without
// blocking it and without the caller needing to know the policy exists.
//
// Contrast with a template default value: a default only fills a gap when
// the CALLER'S OWN template omits the parameter. `append` runs inside the
// Policy engine on every matching request, regardless of what tool created
// it (Portal, CLI, Terraform, another team's pipeline) — so it's how you
// guarantee a value across an entire organisation, not just your own IaC.
//
// Caveat: append only ADDS a field that's missing. If the field is already
// present with a different value, append leaves it alone — it does not
// overwrite. Use `modify` (04) when you need to force-correct an existing
// value, including on resources that already exist.
// ---------------------------------------------------------------------------

resource appendCostCenterTagPolicy 'Microsoft.Authorization/policyDefinitions@2021-06-01' = {
  name: 'append-cost-center-tag'
  properties: {
    displayName: 'Append a CostCenter tag to resource groups that are missing one'
    description: 'Adds tags[CostCenter] with a fixed value to any resource group created without it. The caller never has to know this rule exists.'
    policyType: 'Custom'
    mode: 'Indexed'
    metadata: {
      category: 'Tags'
      version: '1.0.0'
    }
    parameters: {
      costCenterValue: {
        type: 'String'
        metadata: {
          displayName: 'Cost Center'
          description: 'Value to stamp onto tags[CostCenter] when the tag is absent'
        }
        defaultValue: 'unassigned'
      }
    }
    policyRule: {
      if: {
        allOf: [
          {
            field: 'type'
            equals: 'Microsoft.Resources/subscriptions/resourceGroups'
          }
          {
            field: 'tags[\'CostCenter\']'
            exists: 'false'
          }
        ]
      }
      then: {
        effect: 'append'
        // details is an ARRAY for append — you can add several fields in
        // one policy (e.g. two tags at once), unlike modify's `operations`.
        details: [
          {
            field: 'tags[\'CostCenter\']'
            value: '[parameters(\'costCenterValue\')]'
          }
        ]
      }
    }
  }
}
