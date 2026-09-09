// Shared contract vocabulary, imported by every foundation and workload module.
//
// This file IS the governance surface for parameter shapes: change a type here and every
// module that imports it is affected on its next build. That is deliberate — it is the
// mechanism that lets a platform team evolve the contract centrally instead of chasing
// copy-pasted parameter blocks across dozens of squad repos.

@export()
@sealed()
@description('''
Standard tag contract every workload resource must carry.

Sealed on purpose: squads cannot add arbitrary extra keys to this object. Cost and compliance
dashboards join on this fixed key set — an uncontrolled key set breaks that the moment someone
adds a typo'd `Owner` next to `owner`. Anything squad-specific that doesn't belong in the
mandatory set goes in a module's own `additionalTags` parameter instead (see
`foundation/storage-account/main.bicep`), which stays a plain, unsealed object.
''')
type mandatoryTags = {
  @description('Cost centre code, validated against the finance CMDB export at deploy time by policy, not by this type.')
  costCentre: string

  @description('Squad that owns this resource, e.g. "payments-squad". Used for on-call routing and cost attribution.')
  owner: string

  @allowed(['dev', 'test', 'staging', 'production'])
  environment: string

  @allowed(['public', 'internal', 'confidential', 'restricted'])
  @description('Data classification. Drives default backup/retention/encryption posture in foundation modules.')
  dataClassification: string
}

@export()
@description('''
Network posture contract, deliberately NOT sealed.

AVM resource modules add new optional networking properties on almost every release
(private DNS zone group options, new ACL bypass flags, etc.). If this type were sealed,
every such AVM upgrade would force an edit here before a workload module could pass the new
property through — the opposite of "squads can extend without waiting on the platform team".
Sealing is a tool for closing a surface off; leave it off wherever the surface is meant to grow.
''')
type networkPosture = {
  @description('REQUIRED — no default anywhere this type is used as a parameter. Forces an explicit reachability decision at every call site instead of inheriting whatever the underlying resource type happens to default to.')
  publicNetworkAccess: 'Enabled' | 'Disabled'

  @description('Optional extension point: subnet resource ID for a private endpoint / delegation. Foundation modules must accept this when present.')
  subnetResourceId: string?

  @description('Optional extension point: extra IP allow-list entries layered on top of the platform default deny-all.')
  allowedIpRules: string[]?
}

@export()
@allowed([
  'Standard_LRS'
  'Standard_ZRS'
  'Standard_GRS'
])
@description('''
Approved storage SKU allow-list.

This is the whole point of typing a SKU as a closed union instead of `string`: the
Bicep compiler rejects `Premium_LRS` or `Standard_RAGRS` at build time, in the squad's own
PR, before it ever reaches a `what-if`. No lint rule, wiki page, or reviewer memory required —
the type itself is the policy. Widening the allow-list is a one-line, reviewed change to this
file, which is exactly the kind of change platform teams want to stay in control of.
''')
type approvedStorageSku = string
