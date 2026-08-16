targetScope = 'subscription'

// ---------------------------------------------------------------------------
// 07 — Parameters and allowedValues: turning a hardcoded rule into a
// reusable, assignment-time-configurable policy definition.
//
// A policy DEFINITION is just a template — the values plugged into its
// parameters are chosen later, per ASSIGNMENT (see 10). This is what lets
// one "allowed locations" definition be assigned with a different location
// list to every subscription, instead of copy-pasting the definition per
// subscription with the list hardcoded.
// ---------------------------------------------------------------------------

resource allowedLocationsPolicy 'Microsoft.Authorization/policyDefinitions@2021-06-01' = {
  name: 'example-allowed-locations'
  properties: {
    displayName: 'Allowed locations (example)'
    description: 'Denies resources created outside the locations listed in the allowedLocations parameter. Mirrors the shape of the built-in "Allowed locations" policy.'
    policyType: 'Custom'
    mode: 'Indexed'
    metadata: {
      category: 'General'
      version: '1.0.0'
    }
    parameters: {
      allowedLocations: {
        type: 'Array'
        metadata: {
          displayName: 'Allowed locations'
          description: 'The list of locations resources are allowed to be created in'
          strongType: 'location' // tells the Portal to render a location multi-picker instead of free-text
        }
        defaultValue: [
          'uksouth'
          'ukwest'
        ]
      }
    }
    policyRule: {
      if: {
        not: {
          // `in` checks membership against an array parameter — the
          // natural partner to an array-typed parameter like this one.
          field: 'location'
          in: '[parameters(\'allowedLocations\')]'
        }
      }
      then: {
        effect: 'deny'
      }
    }
  }
}
