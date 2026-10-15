// Bootstrap step 1, run once by a subscription Owner:
//
//   az deployment sub create -l uksouth -f identity/main.bicep -p platformRepository=<owner>/<repo>
//
// Creates the platform's own identities and the custom role squads get. Everything after this
// (squad resource groups, squad identities, role assignments to function apps) is done by the
// platform pipeline using the identities created here.

targetScope = 'subscription'

import { platformNames } from '../naming.bicep'

param location string = deployment().location

@description('owner/name of the repo holding platform/squads/*. The only repo whose main branch can deploy squads.')
param platformRepository string

var names = platformNames(subscription().subscriptionId)
var tags = { squad: 'platform' }

var roles = {
  contributor: 'b24988ac-6180-42a0-ab88-20f7382dd24c'
  reader: 'acdd72a7-3385-48ef-bd42-f606fba81ae7'
  rbacAdministrator: 'f58310d9-a9f6-439a-9e8d-f62e7b41a168'
  keyVaultSecretsUser: '4633458b-17de-408a-b874-0445c86b69e6'
  appConfigurationDataReader: '516239f1-63e1-4d78-a4de-a74fb236a071'
  storageBlobDataContributor: 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
  storageBlobDataOwner: 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b'
  serviceBusDataSender: '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39'
  serviceBusDataReceiver: '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0'
  monitoringMetricsPublisher: '3913510d-42f4-4e42-8a64-420c390055eb'
}

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: names.identityResourceGroup
  location: location
  tags: tags
}

module identities 'platform-identities.bicep' = {
  name: 'platform-identities'
  scope: rg
  params: {
    location: location
    tags: tags
    platformRepository: platformRepository
  }
}

// ---------------------------------------------------------------------------------------------
// Squad Workload Contributor: what a squad deployer gets on its own resource group, nothing more.
//
// Contributor-shaped, because the escape hatch (squad-template/infra/) means squads *can*
// create resources in their RG, and the second demo depends on that: a squad must be able to
// *attempt* a public storage account so that policy, not RBAC, is what refuses it. If RBAC
// blocked it first the demo would show AuthorizationFailed and prove nothing about policy.
//
// What it can't do is anything that would let a squad widen its own blast radius or replace
// a platform-owned resource with its own version.
// ---------------------------------------------------------------------------------------------

resource squadWorkloadContributor 'Microsoft.Authorization/roleDefinitions@2022-04-01' = {
  name: guid(subscription().id, 'squad-workload-contributor')
  properties: {
    roleName: 'Squad Workload Contributor'
    description: 'Deploy code and custom resources into one squad resource group. No networking, no role assignments, no platform-owned resource types.'
    type: 'CustomRole'
    assignableScopes: [subscription().id]
    permissions: [
      {
        actions: ['*']
        notActions: [
          // Same exclusions as built-in Contributor: no granting access, no policy exemptions.
          'Microsoft.Authorization/*/Delete'
          'Microsoft.Authorization/*/Write'
          'Microsoft.Authorization/elevateAccess/Action'
          'Microsoft.Blueprint/blueprintAssignments/write'
          'Microsoft.Blueprint/blueprintAssignments/delete'
          'Microsoft.Compute/galleries/share/action'
          // No networking: no VNets, peerings, public IPs, private endpoints or NSG changes.
          // (Joining an existing subnet is a separate action and isn't needed: the platform
          // already wired the function app's integration subnet.)
          'Microsoft.Network/*/write'
          'Microsoft.Network/*/delete'
          // Platform-owned resource types: a second Key Vault or App Config store is a manifest
          // change, so it arrives with private endpoint, diagnostics and RBAC already wired.
          'Microsoft.KeyVault/vaults/write'
          'Microsoft.KeyVault/vaults/delete'
          'Microsoft.AppConfiguration/configurationStores/write'
          'Microsoft.AppConfiguration/configurationStores/delete'
          // The function app's plan, identity and network settings are platform-owned; the
          // squad deploys code (sites/publish) only. sites/config/write is blocked too, because
          // that's where the access restrictions live: without this a squad could open its
          // function to the internet and skip APIM. Squad settings go in App Config instead,
          // which is what the appConfig capability is for.
          'Microsoft.Web/serverfarms/write'
          'Microsoft.Web/serverfarms/delete'
          'Microsoft.Web/sites/write'
          'Microsoft.Web/sites/delete'
          'Microsoft.Web/sites/config/write'
          'Microsoft.Web/sites/networkConfig/write'
          // No minting tokens for other repos via the squad's own identity.
          'Microsoft.ManagedIdentity/userAssignedIdentities/federatedIdentityCredentials/write'
        ]
      }
    ]
  }
}

// ---------------------------------------------------------------------------------------------
// Platform deployer: deploys squad resource groups from the platform repo's main branch.
// ---------------------------------------------------------------------------------------------

resource deployerContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(subscription().id, 'platform-deployer', roles.contributor)
  properties: {
    principalId: identities.outputs.deployerPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.contributor)
  }
}

// It has to create role assignments (function identity -> its Key Vault, squad deployer -> its
// RG), but "can create role assignments" is normally one step from Owner. The ABAC condition
// limits it to exactly the roles the modules hand out. It can't assign Owner, Contributor,
// User Access Administrator, or this role itself.
var assignableRoleIds = join(
  [
    roles.keyVaultSecretsUser
    roles.appConfigurationDataReader
    roles.storageBlobDataContributor
    roles.storageBlobDataOwner
    roles.serviceBusDataSender
    roles.serviceBusDataReceiver
    roles.monitoringMetricsPublisher
    squadWorkloadContributor.name
  ],
  ', '
)

resource deployerRbacAdmin 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(subscription().id, 'platform-deployer', roles.rbacAdministrator)
  properties: {
    principalId: identities.outputs.deployerPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.rbacAdministrator)
    conditionVersion: '2.0'
    condition: '((!(ActionMatches{\'Microsoft.Authorization/roleAssignments/write\'})) OR (@Request[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {${assignableRoleIds}})) AND ((!(ActionMatches{\'Microsoft.Authorization/roleAssignments/delete\'})) OR (@Resource[Microsoft.Authorization/roleAssignments:RoleDefinitionId] ForAnyOfAnyValues:GuidEquals {${assignableRoleIds}}))'
  }
}

// ---------------------------------------------------------------------------------------------
// Platform planner: runs validation and what-if on pull requests. PR code is untrusted (anyone
// who can open a PR controls it), so it gets a read-only identity, never the deployer.
// ---------------------------------------------------------------------------------------------

resource plannerReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(subscription().id, 'platform-planner', roles.reader)
  properties: {
    principalId: identities.outputs.plannerPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roles.reader)
  }
}

// What-if is an action on deployments, which Reader doesn't include. This role adds only that.
resource whatIfRole 'Microsoft.Authorization/roleDefinitions@2022-04-01' = {
  name: guid(subscription().id, 'deployment-what-if')
  properties: {
    roleName: 'Deployment What-If Runner'
    description: 'Validate and what-if deployments. Cannot create or change anything.'
    type: 'CustomRole'
    assignableScopes: [subscription().id]
    permissions: [
      {
        actions: [
          'Microsoft.Resources/deployments/validate/action'
          'Microsoft.Resources/deployments/whatIf/action'
        ]
        notActions: []
      }
    ]
  }
}

resource plannerWhatIf 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(subscription().id, 'platform-planner', 'what-if')
  properties: {
    principalId: identities.outputs.plannerPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: whatIfRole.id
  }
}

output deployerClientId string = identities.outputs.deployerClientId
output deployerPrincipalId string = identities.outputs.deployerPrincipalId
output plannerClientId string = identities.outputs.plannerClientId
output squadWorkloadContributorRoleId string = squadWorkloadContributor.name
