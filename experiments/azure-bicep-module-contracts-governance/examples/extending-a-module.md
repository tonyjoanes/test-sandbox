# Four ways a team can extend a shared module

Same underlying mechanism throughout — required/optional/sealed types — but the review weight
is different each time, and picking the wrong one is the usual source of friction. This is
the thing to hand a squad that asks "how do I get the platform module to do X."

| # | What the team needs | Where the change goes | Who reviews it |
|---|---|---|---|
| 1 | A property already offered through the sealed escape hatch | Nowhere — just set it | Nobody, it's already governed |
| 2 | A property the escape hatch doesn't expose yet, but is a small, bounded addition | `foundation/` (or `contributed/`) module's sealed type | Module owner |
| 3 | A value outside an allow-list (SKU, region, tier) | `shared/types.bicep` | Platform, via RFC |
| 4 | Resources that sit around the shared resource but aren't governed at all | The team's own `workload/` module | Normal squad review |
| 5 | A resource type nothing in the catalog covers yet | New `contributed/` module | Platform + peer squads, via RFC |

---

## 1. Use what's already exposed

Nothing to change. `foundation/storage-account/main.bicep` already exposes
`advanced.allowBlobPublicAccess` and `advanced.minimumTlsVersion`. A team that needs
TLS 1.3-only just sets it at the call site:

```bicep
module storage '../../foundation/storage-account/main.bicep' = {
  params: {
    // ...required contract fields...
    advanced: {
      minimumTlsVersion: 'TLS1_2'
    }
  }
}
```

This is the common case in a healthy catalog — most extension requests should land here, which
is the signal that the sealed escape hatches were sized right.

## 2. Widen a module's own sealed escape hatch

A team needs blob soft-delete retention configured per-environment, and
`foundation/storage-account`'s `advancedOverrides` doesn't expose it yet.

**Before**, in `foundation/storage-account/main.bicep`:

```bicep
@sealed()
type advancedOverrides = {
  minimumTlsVersion: ('TLS1_0' | 'TLS1_1' | 'TLS1_2')?
  allowBlobPublicAccess: bool?
}
```

**After** — a PR against the foundation module, reviewed by its owner, adding one key and
wiring it through to the AVM call:

```bicep
@sealed()
type advancedOverrides = {
  minimumTlsVersion: ('TLS1_0' | 'TLS1_1' | 'TLS1_2')?
  allowBlobPublicAccess: bool?
  blobSoftDeleteRetentionDays: int?          // new
}
```

```bicep
module storageAccount 'br/public:avm/res/storage/storage-account:0.14.3' = {
  params: {
    // ...
    blobServices: {
      deleteRetentionPolicy: {
        enabled: advanced.?blobSoftDeleteRetentionDays != null
        days: advanced.?blobSoftDeleteRetentionDays ?? 7
      }
    }
  }
}
```

This is deliberately a *small* PR: one key, additive, `@sealed()` still rejects anything else.
That smallness is what keeps the review fast — the reviewer is checking "is this one property
safe to expose," not re-litigating the whole module.

## 3. Ask for a value outside the allow-list

A team's workload genuinely needs `Standard_GRS` and `approvedStorageSku` only allows
`Standard_LRS`, `Standard_ZRS`, `Standard_GRS`... suppose it wasn't there yet. Widening an
allow-list is a policy decision (cost, replication behaviour, data residency), so it goes
through an RFC (`governance/rfc-template.md`), not a quiet PR:

**Before**, in `shared/types.bicep`:

```bicep
@allowed([
  'Standard_LRS'
  'Standard_ZRS'
])
type approvedStorageSku = string
```

**After** — platform-reviewed, because this widens what *every* module importing this type
will accept, not just one team's module:

```bicep
@allowed([
  'Standard_LRS'
  'Standard_ZRS'
  'Standard_GRS'          // added after RFC-0007: DR requirement for regulated workloads
])
type approvedStorageSku = string
```

The difference between this and pattern 2: pattern 2 changes one module's local escape hatch;
this changes shared vocabulary every module and every team depends on. Same syntax, very
different blast radius — that's why `shared/types.bicep` sits behind the strictest CODEOWNERS
entry in the whole catalog.

## 4. Add squad-specific resources around the shared module

A team needs three extra blob containers with lifecycle rules only their workload uses. None
of that belongs in the shared contract — it's already handled the way
`workload/team-blob-store/main.bicep` does it: compose the foundation module for the governed
resource, then declare squad-owned resources alongside it in the same file. No RFC, no
platform review — nothing here can violate the foundation contract, because the foundation
module's own required/optional/sealed types already constrain the one call into it.

```bicep
module storage '../../foundation/storage-account/main.bicep' = {
  params: { /* required contract fields */ }
}

resource lifecyclePolicy 'Microsoft.Storage/storageAccounts/managementPolicies@2023-01-01' = {
  name: 'default'
  parent: storageAccountRef
  properties: {
    policy: {
      rules: [
        {
          name: 'archive-after-90-days'
          type: 'Lifecycle'
          definition: {
            actions: {
              baseBlob: {
                tierToArchive: { daysAfterModificationGreaterThan: 90 }
              }
            }
            filters: { blobTypes: ['blockBlob'] }
          }
        }
      ]
    }
  }
}
```

## 5. Contribute a whole new module

A team needs Service Bus and nothing in `foundation/` covers it. Rather than reaching for AVM
directly inside their own `workload/` module (which would mean nobody else benefits, and no
contract enforcement applies to their choices), they file an RFC and add
`contributed/service-bus-namespace/main.bicep` — see that file for the full worked example. It
follows the same required/optional/sealed shape as a foundation module, imports the same
shared types for `tags`/`network`, but its SKU allow-list is declared locally (widening it
doesn't need platform sign-off) and the team, not platform, owns it going forward.
