targetScope = 'subscription'

// ---------------------------------------------------------------------------
// 09 — Initiative (policySetDefinition): a named, assignable bundle of
// individual policy definitions, evaluated and assigned together.
//
// Why bundle at all? Compliance dashboards, and scope/assignment, work at
// the initiative level too — "our governance baseline" becomes ONE thing
// to assign and ONE thing to see compliance for, rather than four separate
// assignments to keep in sync across every subscription.
//
// This example assumes 01 (audit tag), 02 (deny HTTPS), 04 (modify tag),
// and 07 (allowed locations) have already been deployed as policy
// definitions in this subscription — initiatives reference existing
// definitions BY resourceId, they don't inline the policyRule. Each entry
// can also remap the initiative's own parameters onto the underlying
// definition's parameters, so a caller configures the initiative once
// instead of configuring every policy inside it separately.
// ---------------------------------------------------------------------------

resource governanceBaselineInitiative 'Microsoft.Authorization/policySetDefinitions@2021-06-01' = {
  name: 'example-governance-baseline'
  properties: {
    displayName: 'Example governance baseline'
    description: 'Bundles tag auditing, HTTPS enforcement, tag correction, and allowed locations into one assignable initiative.'
    policyType: 'Custom'
    metadata: {
      category: 'General'
      version: '1.0.0'
    }
    parameters: {
      requiredTagName: {
        type: 'String'
        defaultValue: 'Environment'
      }
      environmentValue: {
        type: 'String'
        defaultValue: 'Production'
      }
      allowedLocations: {
        type: 'Array'
        defaultValue: [
          'uksouth'
          'ukwest'
        ]
      }
    }
    policyDefinitions: [
      {
        // policyDefinitionReferenceId is the initiative-LOCAL name for
        // this slot. Assignments (10) and exemptions/remediations
        // (11, 12) target one specific policy WITHIN an initiative using
        // this id, not the underlying definition's own name.
        policyDefinitionReferenceId: 'auditMissingTag'
        policyDefinitionId: subscriptionResourceId('Microsoft.Authorization/policyDefinitions', 'audit-missing-required-tag')
        parameters: {
          tagName: {
            value: '[parameters(\'requiredTagName\')]'
          }
        }
      }
      {
        policyDefinitionReferenceId: 'denyInsecureStorage'
        policyDefinitionId: subscriptionResourceId('Microsoft.Authorization/policyDefinitions', 'deny-storage-without-https-only')
        // No parameters entry needed — this definition doesn't take any.
      }
      {
        policyDefinitionReferenceId: 'modifyEnvironmentTag'
        policyDefinitionId: subscriptionResourceId('Microsoft.Authorization/policyDefinitions', 'modify-environment-tag')
        parameters: {
          environmentValue: {
            value: '[parameters(\'environmentValue\')]'
          }
        }
      }
      {
        policyDefinitionReferenceId: 'allowedLocations'
        policyDefinitionId: subscriptionResourceId('Microsoft.Authorization/policyDefinitions', 'example-allowed-locations')
        parameters: {
          allowedLocations: {
            value: '[parameters(\'allowedLocations\')]'
          }
        }
      }
    ]
  }
}
