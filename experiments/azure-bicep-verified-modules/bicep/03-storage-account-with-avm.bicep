// =============================================================================
// 03 — The SAME Secure Storage Account, WITH an Azure Verified Module
// =============================================================================
// Same outcome as 02-storage-account-without-avm.bicep — HTTPS-only, TLS 1.2
// minimum, no public network access, a private endpoint with DNS wired up,
// diagnostics to Log Analytics, one RBAC role grant — expressed as
// PARAMETERS to a single module call instead of five hand-authored resource
// blocks. The module is maintained by Microsoft + the community, published
// to the public Bicep registry, and versioned — you consume it the same way
// you'd consume any published library.
//
// 'avm/res/storage/storage-account' is a real, verified module path (checked
// against github.com/Azure/bicep-registry-modules while building this
// experiment). The version tag below and the exact nested property names
// inside privateEndpoints/diagnosticSettings/roleAssignments can shift
// between module releases — VS Code's Bicep extension gives full
// IntelliSense once you type the module path, and the module's own README
// (linked from https://aka.ms/AVM) is the source of truth for the CURRENT
// version and exact parameter shape. Treat the version pin here as
// illustrative and re-check it before using this for real — see
// 06-versioning-and-telemetry.bicep for why that matters.
// =============================================================================

@description('Name of the storage account (globally unique, 3-24 lowercase alphanumeric characters).')
@minLength(3)
@maxLength(24)
param storageAccountName string

@description('Azure region for all resources.')
param location string = resourceGroup().location

@description('Resource ID of the subnet the private endpoint attaches to.')
param privateEndpointSubnetId string

@description('Resource ID of the private DNS zone for blob storage (privatelink.blob.core.windows.net).')
param blobPrivateDnsZoneId string

@description('Resource ID of the Log Analytics workspace diagnostic logs are sent to.')
param logAnalyticsWorkspaceId string

@description('Principal (object) ID to grant read access to blob data.')
param readerPrincipalId string

module storageAccount 'br/public:avm/res/storage/storage-account:0.14.3' = {
  name: 'storageAccountDeployment'
  params: {
    name: storageAccountName
    location: location
    skuName: 'Standard_LRS'
    kind: 'StorageV2'

    // Same security posture as 02 — but as named booleans/strings instead
    // of knowing the exact 'properties.*' JSON shape.
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    publicNetworkAccess: 'Disabled'
    networkAcls: {
      defaultAction: 'Deny'
      bypass: 'AzureServices'
    }

    // Replaces 02's separate privateEndpoint + privateDnsZoneGroup
    // resources — the module wires the DNS registration up internally.
    // (Nested property names here are illustrative — confirm the exact
    // shape for your pinned version via IntelliSense or the module README.)
    privateEndpoints: [
      {
        service: 'blob'
        subnetResourceId: privateEndpointSubnetId
        privateDnsZoneResourceIds: [
          blobPrivateDnsZoneId
        ]
      }
    ]

    // Replaces 02's separate diagnosticSettings resource — no need to know
    // the exact metric/log category names the module already knows them.
    diagnosticSettings: [
      {
        workspaceResourceId: logAnalyticsWorkspaceId
      }
    ]

    // Replaces 02's separate roleAssignment resource AND the hand-sourced
    // role definition GUID — 'roleDefinitionIdOrName' accepts the
    // human-readable built-in role name directly.
    roleAssignments: [
      {
        principalId: readerPrincipalId
        roleDefinitionIdOrName: 'Storage Blob Data Reader'
        principalType: 'ServicePrincipal'
      }
    ]

    tags: {
      environment: 'demo'
    }
  }
}

output storageAccountId string = storageAccount.outputs.resourceId
output primaryBlobEndpoint string = storageAccount.outputs.primaryBlobEndpoint

// -----------------------------------------------------------------------------
// Tally: 1 module call, 0 API versions to track (the module owns that), 0
// role GUIDs to source, 0 prior knowledge of how private endpoints and DNS
// zone groups fit together required to get it right. The module's own test
// suite (run in CI before every publish, by the module maintainers) is what
// verifies that wiring works — you inherit that verification instead of
// re-deriving it.
// -----------------------------------------------------------------------------
