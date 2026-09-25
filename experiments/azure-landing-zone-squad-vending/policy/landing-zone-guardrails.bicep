// Guardrail policy for the "Landing Zones" management group tree (Corp + Online). Assigned
// once, at MG scope — every squad subscription placed under Landing Zones inherits this the
// moment ../vending/main.bicep moves it there. No squad opts in or out; that's the point of
// putting control here instead of asking each squad to configure it themselves.
//
// This is deliberately the STRICT tier. Contrast with sandbox-guardrails.bicep: a squad that
// has graduated a workload out of a sandbox into a real landing zone is accepting this policy
// set as the cost of the stronger platform guarantees (corp network connectivity, backup,
// production support) that come with it.
//
// Deploy with: az deployment mg create --management-group-id contoso-landing-zones
//   --location <region> --template-file landing-zone-guardrails.bicep
// The target MG is set by --management-group-id, not a parameter — a Bicep file at
// targetScope 'managementGroup' always deploys into whatever MG the deploy command names.

targetScope = 'managementGroup'

@allowed([
  'uksouth'
  'ukwest'
])
@description('Regions squad workloads may deploy to under this MG. Two, not one: a squad that needs cross-region resilience still has a legal option without an exception request.')
param allowedLocations array = [
  'uksouth'
  'ukwest'
]

// A custom initiative (policy set) rather than individually-assigned built-ins, so the whole
// guardrail bundle shows as one line in the Azure Policy portal and one assignment to update
// when the bundle changes, instead of a dozen scattered assignments.
resource landingZoneInitiative 'Microsoft.Authorization/policySetDefinitions@2023-04-01' = {
  name: 'contoso-landing-zone-guardrails'
  properties: {
    displayName: 'Contoso Landing Zone Guardrails'
    description: 'Baseline every squad landing zone subscription must meet. See experiments/azure-landing-zone-squad-vending/README.md for the reasoning behind each rule.'
    policyType: 'Custom'
    parameters: {
      allowedLocations: {
        type: 'Array'
        metadata: {
          displayName: 'Allowed locations'
        }
      }
    }
    policyDefinitions: [
      {
        // Built-in: "Allowed locations" — https://learn.microsoft.com/azure/governance/policy/samples/built-in-policies#general
        policyDefinitionId: '/providers/Microsoft.Authorization/policyDefinitions/e56962a6-4747-49cd-b67b-bf8b01975c4c'
        policyDefinitionReferenceId: 'allowed-locations'
        parameters: {
          listOfAllowedLocations: {
            value: '[parameters(\'allowedLocations\')]'
          }
        }
      }
      {
        // Built-in: "Require a tag on resource groups" — enforces the mandatoryTags.owner /
        // costCentre contract from experiments/azure-bicep-module-contracts-governance/ at
        // the platform boundary, not just inside modules that choose to import that type.
        policyDefinitionId: '/providers/Microsoft.Authorization/policyDefinitions/96670d01-0a4d-4649-9c89-2d3abc0a5025'
        policyDefinitionReferenceId: 'require-owner-tag'
        parameters: {
          tagName: {
            value: 'owner'
          }
        }
      }
      {
        // Built-in: "Not allowed resource types" — the two resource types that most reliably
        // turn a subscription-scoped mistake into a tenant-scoped incident.
        policyDefinitionId: '/providers/Microsoft.Authorization/policyDefinitions/6c112d4e-5bc7-47ae-a041-ea2d9dccd749'
        policyDefinitionReferenceId: 'blocked-resource-types'
        parameters: {
          notAllowedResourceTypes: {
            value: [
              'Microsoft.Network/publicIPAddresses'
              'Microsoft.Authorization/roleAssignments'
            ]
          }
        }
      }
      {
        // Built-in: "Azure Monitor Log Analytics workspace agent should be installed" family
        // stands in here for "every resource ships diagnostics to the platform Log Analytics
        // workspace in platform-management" — the specific policy ID varies by resource type
        // in a real rollout; this is illustrative of the pattern, not a copy-paste-ready ID.
        policyDefinitionId: '/providers/Microsoft.Authorization/policyDefinitions/PLACEHOLDER-diagnostic-settings'
        policyDefinitionReferenceId: 'require-diagnostics'
        parameters: {}
      }
    ]
  }
}

resource assignment 'Microsoft.Authorization/policyAssignments@2023-04-01' = {
  name: 'contoso-lz-guardrails'
  properties: {
    displayName: 'Contoso Landing Zone Guardrails'
    policyDefinitionId: landingZoneInitiative.id
    parameters: {
      allowedLocations: {
        value: allowedLocations
      }
    }
    // Assignments at MG scope apply to every subscription under it, present and future — a
    // squad subscription vended into Landing Zones tomorrow inherits this without a second
    // deployment. That inheritance is the actual governance mechanism; nothing about a squad's
    // own deployments needs to reference this file at all.
    enforcementMode: 'Default'
  }
  identity: {
    type: 'SystemAssigned'
  }
}
