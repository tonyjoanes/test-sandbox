targetScope = 'subscription'

// ---------------------------------------------------------------------------
// 01 — Audit effect: the safest starting point for any new policy.
//
// `audit` never blocks a request. It evaluates every matching resource and
// marks it compliant/non-compliant in Policy's compliance dashboard, with
// zero risk of breaking a deployment. Always prototype a new rule as
// `audit` before switching it to `deny` (02) — audit shows you what WOULD
// have been blocked, across your whole estate, before you commit to
// blocking it.
// ---------------------------------------------------------------------------

resource auditMissingTagPolicy 'Microsoft.Authorization/policyDefinitions@2021-06-01' = {
  name: 'audit-missing-required-tag'
  properties: {
    displayName: 'Audit resources missing a required tag'
    description: 'Flags any resource that does not have the tag named by the "tagName" parameter. Effect is audit-only — nothing is blocked.'
    policyType: 'Custom'
    mode: 'Indexed' // 'Indexed' = only evaluate resource types that support tags/location. 'All' also evaluates types that don't (e.g. role assignments) — needed for non-tag rules, wasteful for tag rules.
    metadata: {
      category: 'Tags'
      version: '1.0.0'
    }
    parameters: {
      tagName: {
        type: 'String'
        metadata: {
          displayName: 'Tag Name'
          description: 'Name of the tag to check for, e.g. "Environment"'
        }
        defaultValue: 'Environment'
      }
    }
    policyRule: {
      if: {
        // field() supports tags['tagName'] lookups directly, and the field
        // path itself can be BUILT from a parameter using concat() — this
        // is how one definition can check for any tag name, chosen at
        // assignment time, rather than one definition per tag.
        field: '[concat(\'tags[\', parameters(\'tagName\'), \']\')]'
        // exists is a *string* 'false' here, not the JSON boolean false —
        // policy language functions always operate on/return strings.
        exists: 'false'
      }
      then: {
        effect: 'audit'
      }
    }
  }
}
