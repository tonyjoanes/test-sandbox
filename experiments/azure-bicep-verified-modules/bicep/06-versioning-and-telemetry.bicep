// =============================================================================
// 06 — Versioning, Pinning & Telemetry
// =============================================================================
// Two operational details that matter once you're actually depending on AVM
// modules rather than just trying them out: how versions work, and what
// that quiet extra deployment resource you'll see in every resource group
// is.
// =============================================================================

@description('Azure region for all resources.')
param location string = resourceGroup().location

// -----------------------------------------------------------------------------
// VERSIONING
// -----------------------------------------------------------------------------
// AVM modules are published to the Bicep public registry (backed by MCR)
// under exact semantic version tags — there is deliberately no ':latest'
// floating tag published for you to accidentally track. Every reference
// pins to a specific version by design:

module storageAccountPinned 'br/public:avm/res/storage/storage-account:0.14.3' = {
  name: 'storageAccountPinnedDeployment'
  params: {
    name: 'stpinned${uniqueString(resourceGroup().id)}'
    location: location
  }
}

// This is a FEATURE, not friction: it means every deployment is
// reproducible from source control alone — re-running this file next year
// deploys the exact same module logic it did today, unaffected by any
// changes AVM publishes in the meantime. Bumping the version is then a
// deliberate, reviewable change (a one-line diff in a PR) rather than
// something that happens silently on your next deployment.
//
// `bicep restore` downloads and locally caches every module a file
// references (including transitively, for pattern modules that call other
// modules) — run it after changing a version pin, or let your CI pipeline's
// deployment step do it implicitly.

// --- Maximum reproducibility: pinning by digest instead of tag -------------
// A version tag (":0.14.3") can, in principle, be re-pushed to point at
// different content — vanishingly unlikely for a governed registry like
// AVM, but for a supply-chain-sensitive deployment you can pin to the
// immutable content digest instead, the same way container image
// references support '@sha256:...' pinning. This uses the fully-qualified
// registry form rather than the 'br/public:' shorthand alias:
//
//   module storageAccountByDigest 'br:mcr.microsoft.com/bicep/avm/res/storage/storage-account@sha256:<digest>' = {
//     name: 'storageAccountByDigestDeployment'
//     params: { ... }
//   }
//
// Fetch the digest for a given tag with the Bicep/OCI tooling your
// registry client provides, or from the module's page on MCR. Most teams
// don't need this level of pinning — semantic version pinning above is
// the normal, recommended default.

// -----------------------------------------------------------------------------
// TELEMETRY
// -----------------------------------------------------------------------------
// Every AVM module deploys one extra, harmless resource alongside your
// actual infrastructure: a zero-cost `Microsoft.Resources/deployments`
// no-op whose NAME (not its content) encodes which module and version was
// used. Microsoft aggregates these names across all AVM deployments
// tenant-wide to understand module adoption — no resource configuration,
// parameter values, or other customer data is included, only "this module,
// this version, was deployed."
//
// Every module exposes 'enableTelemetry' to opt out, defaulting to true:

module storageAccountNoTelemetry 'br/public:avm/res/storage/storage-account:0.14.3' = {
  name: 'storageAccountNoTelemetryDeployment'
  params: {
    name: 'stnotel${uniqueString(resourceGroup().id)}'
    location: location
    enableTelemetry: false
  }
}

// Reasons to actually flip this: a data-residency or air-gapped-cloud
// policy that prohibits ANY telemetry resource regardless of content, or
// an internal policy that blanket-disallows resources your team doesn't
// explicitly own the definition of. Otherwise, leaving it enabled costs
// nothing and is what funds the case for AVM's continued investment
// internally at Microsoft — visible adoption numbers are part of how the
// initiative justifies maintaining and expanding the module catalog.
