// Guardrail policy for the "Sandboxes" management group. Deliberately the LOOSE tier — this
// is what makes a sandbox a place squads will actually use to try things, rather than a slower
// version of the same restrictions they'd hit in a real landing zone.
//
// "Safe to innovate in" is delivered by three things working together, only one of which is
// policy: this initiative (resource-level guardrails), the hard spend cap wired in
// ../vending/modules/budget.bicep (financial blast radius), and the absence of any peering to
// the hub network in ../vending/modules/network-spoke.bicep (a sandbox literally cannot reach
// anything on the corp network — there's no route, not just a policy saying not to). Losing
// any one of the three turns "sandbox" into either "unsafe" or "not actually usable."
//
// Deploy with: az deployment mg create --management-group-id contoso-sandboxes
//   --location <region> --template-file sandbox-guardrails.bicep

targetScope = 'managementGroup'

resource sandboxInitiative 'Microsoft.Authorization/policySetDefinitions@2023-04-01' = {
  name: 'contoso-sandbox-guardrails'
  properties: {
    displayName: 'Contoso Sandbox Guardrails'
    description: 'Minimum guardrails for a pure-innovation subscription. Intentionally much shorter than landing-zone-guardrails.bicep — see README.md for what a sandbox trades away and what it keeps.'
    policyType: 'Custom'
    policyDefinitions: [
      {
        // Built-in: "Not allowed resource types" — a much shorter list than the landing zone
        // tier. The goal here is "can't create a standing liability the platform team inherits
        // after the sandbox is torn down" (ExpressRoute circuits, VPN gateways, anything with
        // a multi-year commit), not "can't experiment with unusual resource types."
        policyDefinitionId: '/providers/Microsoft.Authorization/policyDefinitions/6c112d4e-5bc7-47ae-a041-ea2d9dccd749'
        policyDefinitionReferenceId: 'blocked-resource-types'
        parameters: {
          notAllowedResourceTypes: {
            value: [
              'Microsoft.Network/expressRouteCircuits'
              'Microsoft.Network/vpnGateways'
              'Microsoft.Network/virtualNetworkGateways'
            ]
          }
        }
      }
      {
        // Built-in: "Require a tag on resource groups" — kept even in the sandbox tier,
        // because the one thing that must never be optional is "who do we email when the
        // spend alert fires." Everything else about tagging discipline is relaxed.
        policyDefinitionId: '/providers/Microsoft.Authorization/policyDefinitions/96670d01-0a4d-4649-9c89-2d3abc0a5025'
        policyDefinitionReferenceId: 'require-owner-tag'
        parameters: {
          tagName: {
            value: 'owner'
          }
        }
      }
      {
        // Built-in: "Audit" (not deny) allowed locations. Sandboxes can deploy anywhere —
        // trying a resource type or SKU only available in another region is exactly the kind
        // of thing a sandbox should let a squad discover quickly — but the audit trail still
        // flags it so a pattern of "everyone's sandboxing outside our two home regions" is
        // visible to platform, instead of only being enforced (or not) after the fact.
        policyDefinitionId: '/providers/Microsoft.Authorization/policyDefinitions/e56962a6-4747-49cd-b67b-bf8b01975c4c'
        policyDefinitionReferenceId: 'audit-locations'
        parameters: {
          effect: {
            value: 'Audit'
          }
        }
      }
    ]
  }
}

resource assignment 'Microsoft.Authorization/policyAssignments@2023-04-01' = {
  name: 'contoso-sandbox-guardrails'
  properties: {
    displayName: 'Contoso Sandbox Guardrails'
    policyDefinitionId: sandboxInitiative.id
    enforcementMode: 'Default'
  }
  identity: {
    type: 'SystemAssigned'
  }
}
