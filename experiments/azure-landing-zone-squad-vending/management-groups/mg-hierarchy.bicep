// Management group hierarchy — deployed once, by platform, at tenant scope. Everything else in
// this experiment (policy guardrails, subscription vending) targets a management group this
// file creates. Standard Azure Landing Zone shape, trimmed to what a squad-vending platform
// actually needs — see README.md for why each group exists and what inherits from where.
//
// Deploy with: az deployment tenant create --location <region> --template-file mg-hierarchy.bicep

targetScope = 'tenant'

@description('The tenant root group ID (your Entra tenant ID). Every management group below is parented, directly or indirectly, off this.')
param tenantRootGroupId string

resource platform 'Microsoft.Management/managementGroups@2023-04-01' = {
  name: 'contoso-platform'
  properties: {
    displayName: 'Platform'
    details: {
      parent: {
        id: tenantResourceId('Microsoft.Management/managementGroups', tenantRootGroupId)
      }
    }
  }
}

// Platform sub-groups: identity, management (logging/monitoring), connectivity (hub network).
// Squad subscriptions never land here — these hold the shared infrastructure squads consume
// (the hub VNet, centralized logging) but never own or deploy into directly.
resource platformIdentity 'Microsoft.Management/managementGroups@2023-04-01' = {
  name: 'contoso-platform-identity'
  properties: {
    displayName: 'Platform - Identity'
    details: {
      parent: {
        id: platform.id
      }
    }
  }
}

resource platformManagement 'Microsoft.Management/managementGroups@2023-04-01' = {
  name: 'contoso-platform-management'
  properties: {
    displayName: 'Platform - Management'
    details: {
      parent: {
        id: platform.id
      }
    }
  }
}

resource platformConnectivity 'Microsoft.Management/managementGroups@2023-04-01' = {
  name: 'contoso-platform-connectivity'
  properties: {
    displayName: 'Platform - Connectivity'
    details: {
      parent: {
        id: platform.id
      }
    }
  }
}

// Landing Zones: where a squad's subscription lands once a workload is past pure prototyping
// and needs to be treated like it's staying — full guardrail policy set (see
// ../policy/landing-zone-guardrails.bicep), peered into the hub for corp connectivity.
resource landingZones 'Microsoft.Management/managementGroups@2023-04-01' = {
  name: 'contoso-landing-zones'
  properties: {
    displayName: 'Landing Zones'
    details: {
      parent: {
        id: tenantResourceId('Microsoft.Management/managementGroups', tenantRootGroupId)
      }
    }
  }
}

resource landingZonesCorp 'Microsoft.Management/managementGroups@2023-04-01' = {
  name: 'contoso-landing-zones-corp'
  properties: {
    displayName: 'Landing Zones - Corp'
    details: {
      parent: {
        id: landingZones.id
      }
    }
  }
}

resource landingZonesOnline 'Microsoft.Management/managementGroups@2023-04-01' = {
  name: 'contoso-landing-zones-online'
  properties: {
    displayName: 'Landing Zones - Online'
    details: {
      parent: {
        id: landingZones.id
      }
    }
  }
}

// Sandboxes: where a squad's subscription lands for pure innovation. See
// ../policy/sandbox-guardrails.bicep — deliberately looser resource policy (squads can try
// things a production landing zone would block), but every subscription here is spend-capped
// and has no route to the corp network at all. "Safe to innovate in" means safe to fail in,
// not safe to reach production data from.
resource sandboxes 'Microsoft.Management/managementGroups@2023-04-01' = {
  name: 'contoso-sandboxes'
  properties: {
    displayName: 'Sandboxes'
    details: {
      parent: {
        id: tenantResourceId('Microsoft.Management/managementGroups', tenantRootGroupId)
      }
    }
  }
}

// Decommissioned: subscriptions in their 30-day teardown window before cancellation. Existing
// here strips every custom RBAC assignment and applies a deny-everything policy — nobody
// deploys anything new to a subscription on its way out, including the squad that owned it.
resource decommissioned 'Microsoft.Management/managementGroups@2023-04-01' = {
  name: 'contoso-decommissioned'
  properties: {
    displayName: 'Decommissioned'
    details: {
      parent: {
        id: tenantResourceId('Microsoft.Management/managementGroups', tenantRootGroupId)
      }
    }
  }
}

@description('Management group IDs, for wiring policy assignments (../policy/) and vending target selection (../vending/) without re-deriving names.')
output managementGroupIds object = {
  platform: platform.id
  platformIdentity: platformIdentity.id
  platformManagement: platformManagement.id
  platformConnectivity: platformConnectivity.id
  landingZones: landingZones.id
  landingZonesCorp: landingZonesCorp.id
  landingZonesOnline: landingZonesOnline.id
  sandboxes: sandboxes.id
  decommissioned: decommissioned.id
}
