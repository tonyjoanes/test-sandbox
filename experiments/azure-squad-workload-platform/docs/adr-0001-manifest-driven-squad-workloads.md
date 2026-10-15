# ADR-0001: Manifest-Driven Squad Workloads on a Shared Backbone

- **Status:** Proposed
- **Date:** 2026-10-15
- **Scope:** PoC only. A personal Azure subscription with a budget alert, not the work tenant.

---

## Context

The other experiments in this repo each cover one layer of a self-service platform:

| Experiment | Layer it covers |
|---|---|
| `azure-landing-zone-squad-vending` | **Where** a squad deploys: a vended subscription per squad, with guardrails inherited from the management group |
| `azure-bicep-module-contracts-governance` | **What** a squad may deploy: typed module contracts on top of AVM |
| `azure-squad-cicd-identity` | **How** it gets deployed: OIDC identities, one per tier, with no stored secrets |
| `azure-policy-examples` | The policy safety net underneath all three |

None of them answers the question a squad actually asks: *"I need a Key Vault, some config, a
blob container, a topic, and a public route to my function. How do I get those without filing a
ticket?"* This PoC answers that question. A squad declares its needs in a small manifest. A
platform pipeline turns the manifest into a wired-up resource group. The squad then ships only
code.

The demo has to show two things:

1. A squad goes from an empty repo to a live public endpoint through PRs only.
2. A squad that tries to create a public storage account on its own gets blocked by **policy**.
   RBAC must not be what stops it.

---

## Decision

### 1. One resource group per squad, inside one shared subscription

This is a deliberate step down from the subscription-per-squad model in
`azure-landing-zone-squad-vending`. The PoC has to run in a single personal subscription, and
the thing being tested here is the manifest-to-infrastructure loop, not tenant isolation. The
design keeps the seam explicit: the platform pipeline takes a target **scope**, so swapping
"RG in shared subscription" for "vended subscription" later only changes the scope, not the
modules.

### 2. A shared backbone resource group, owned by the platform

| Resource | SKU | Why |
|---|---|---|
| Log Analytics workspace | Pay-as-you-go | Diagnostics target for every module |
| VNet + private DNS zones | n/a | Private endpoints need somewhere to land and resolve |
| Service Bus namespace | **Standard** (see consequences) | One namespace. Each squad gets topics and subscriptions in it |
| API Management | Developer (classic) | VNet injection (external mode) to reach private Function Apps. No SLA, which is fine for a PoC |
| Front Door | **Standard** (see consequences) | Public edge, one route per squad that has `expose.public: true` |

### 3. The contract is `workload.yaml`, validated against a JSON Schema

```yaml
squad: orders
capabilities:
  keyVault: true
  appConfig: true
  storage: [invoices]
  topics: [order-placed]
  expose: { public: true, path: /orders }
```

The schema is the API. It is versioned (`apiVersion: platform/v1`) so the platform can evolve
it without breaking squads already on it. Every field is validated in CI before anything
touches Azure. This follows the same pattern as `flags/schema.json` in the feature flag registry.

### 4. Self-service means a PR into the platform repo, merged by CI and CODEOWNERS

**Where the manifest lives** is the most important call in this ADR. There are two options:

| Option | Pros | Cons |
|---|---|---|
| **A. Manifest lives in the squad repo.** The squad repo's pipeline triggers the platform pipeline | Manifest sits next to the code it serves | The platform identity would have to trust a pipeline the squad can edit. Any squad repo could then deploy anything the platform identity can deploy. Fixing that needs OIDC subject claims bound to a reusable workflow ref, which is real but fiddly |
| **B. Manifest lives in the platform repo**, at `squads/<squad>/workload.yaml`. The squad owns that path through CODEOWNERS | The platform identity only ever trusts the platform repo's `main`. Squad approval on their own path, plus green validation, merges without a platform human | Two repos to touch to onboard |

**Chosen: B.** It satisfies "self-service through a PR, not a form" without widening the
platform identity's trust boundary. The "not a ticket queue" rule is enforced mechanically:
CODEOWNERS on `squads/<squad>/` names only that squad. Platform reviewers are required only
for changes outside `squads/`. Auto-merge goes on when validation, `what-if` and the squad's
approval all pass.

If the PoC goes well, option A with `job_workflow_ref`-bound federated credentials is the
natural follow-up. It's recorded under open questions, not built.

### 5. Three identities, each with the least it needs

| Identity | Scope | Roles | Can't |
|---|---|---|---|
| **Platform deployer** (OIDC from the platform repo's `main`) | Subscription | Contributor, plus **Role Based Access Control Administrator** with an ABAC condition that limits it to the specific data-plane roles in the module catalogue | Assign Owner, User Access Administrator, or any role outside the allow-list |
| **Squad deployer** (OIDC from the squad repo, per environment) | The squad's own RG | Custom role **"Squad Workload Contributor"** (below) | Touch networking, assign roles, or reach any other RG |
| **Function App managed identity** (system-assigned, created by the platform) | Individual resources | Key Vault Secrets User, App Configuration Data Reader, Storage Blob Data Contributor (container scope), Service Bus Data Sender/Receiver (topic/subscription scope) | Anything not declared in the manifest |

**"Squad Workload Contributor"** is Contributor-shaped, with these `NotActions`:

- `Microsoft.Network/*` except `*/join/action` (the networking piece of "can't change networking")
- `Microsoft.Authorization/*/write` and `*/delete` (no role assignments and no policy exemptions)
- `Microsoft.KeyVault/vaults/write`, `Microsoft.AppConfiguration/configurationStores/write`, and
  `Microsoft.Web/serverfarms/write` (they can't create or reconfigure the platform-owned
  instances)

It deliberately **allows** `Microsoft.Storage/storageAccounts/write`. That is the escape hatch.

### 6. The escape hatch is a squad-owned `infra/` folder, governed by policy rather than RBAC

Squads can deploy extra resources into their own RG from `infra/` in their repo, with their own
identity. That's the AeroFlow `infra/` pattern. Only policy bounds what they deploy. This is
also why the second demo works: the squad *can* attempt a storage account, and it is
**policy** that denies `publicNetworkAccess: Enabled`. If RBAC blocked the call first, the demo
would show the wrong control.

### 7. Three policies, assigned at subscription scope, as one initiative

| Policy | Effect | Note |
|---|---|---|
| Deny public network access | `Deny` | One built-in per service (Storage, Key Vault, App Config, Function Apps), grouped into an initiative. No single built-in covers every service |
| Require diagnostics | **`DeployIfNotExists`**, not `Deny` | A diagnostic setting is an extension resource created *after* its parent. A Deny check on the parent fires before the setting could exist. DINE, with a remediation identity, is the pattern that actually works |
| Require the `squad` tag | `Deny` on resource groups, `Modify` (inherit from the RG) on resources | Deny on every resource is noisy. Squads forget, and platform modules already set the tag. Inheriting from the RG is the stable choice |

The backbone RG is **exempt** from the public-access deny for Front Door and APIM. They are the
public edge by design. The exemption is in code, with an expiry and a justification, as in
`azure-policy-examples/policies/11-policy-exemption.bicep`.

### 8. Modules are versioned with releases and consumed by tag

Each module lives in `modules/<name>/` and is released as `<name>/vX.Y.Z` git tags. The
platform pipeline publishes them to a private Bicep registry (ACR, Basic SKU) and references
them as `br:<acr>/platform/<name>:X.Y.Z`. Versioning follows the semver rules already written
up in `azure-bicep-module-contracts-governance/ADOPTION.md`.

Every module has private endpoint, diagnostics and RBAC **built in, not optional**. There is no
`enablePrivateEndpoint` parameter. If a squad can turn something off, it isn't a guardrail.

### 9. App Configuration: one Standard store per squad, for now

There are two choices:

- **Per squad (chosen):** clean RBAC (Data Reader on *their* store only), clean deletion, about
  $36/month per store at list price.
- **Shared store, key prefix and label per squad:** one store's cost. But App Configuration RBAC
  is store-wide, not prefix-scoped, so any squad with Data Reader can read every other squad's
  keys. That's acceptable for non-secret config, never for anything sensitive.

The PoC chooses per-squad and runs at most two squads, so the cost is bounded. The manifest
makes the choice cheap to reverse later: `appConfig: true` is an abstraction, so the platform
can switch the implementation to a shared store without any squad changing a line.

---

## Consequences, Including the Parts of the Original Plan That Don't Hold

These are corrections to the original plan, found while writing this ADR. Each changes what
gets built.

1. **Front Door Standard can't use Private Link origins.** Private Link to an origin is a
   **Premium** feature (about $330/month base vs about $35). On Standard, the route is
   Front Door → APIM's *public* gateway (external VNet mode). APIM accepts only Front Door
   traffic by checking the `X-Azure-FDID` header in policy and restricting its NSG to the
   `AzureFrontDoor.Backend` service tag. APIM → Function App then goes over the VNet to the
   function's private endpoint. The function stays private. APIM is the one deliberately
   public hop, and the policy exemption in decision 7 covers it.

2. **Service Bus private endpoints need Premium.** That's roughly $680/month per messaging unit,
   which is out of budget. The PoC uses a **Standard** namespace with Entra-only auth (local SAS
   auth disabled) and a documented policy exemption for the namespace. The Service Bus module
   still accepts a private endpoint config, so moving to Premium means a SKU change and dropping
   the exemption, not a module rewrite. This is the one place where "private endpoints built
   in" is honestly not true in the PoC, and the README has to say so.

3. **The Function App must be on Flex Consumption** (or Elastic Premium). Classic Consumption
   supports neither VNet integration nor inbound private endpoints. Flex Consumption supports
   both, and it scales to zero.

4. **"Can't create Key Vaults" and "can add custom infra" pull against each other.** The custom
   role in decision 5 resolves that. Squads can create general resources in their RG, but not
   the platform-owned resource types. If the demo needs a squad to add a *second* Key Vault,
   that's a manifest change, not an `infra/` change.

5. **A budget alert is not a spending cap.** Azure budgets notify. They don't stop resources.
   The cost guard has three parts: the budget alert at 50/80/100%, a `make down` / `teardown`
   workflow that deletes squad RGs, and an APIM-free mode (below) for days when the demo isn't
   running.

### Rough monthly cost at list price (verify against the pricing calculator before deploying)

| Item | Approx. per month |
|---|---|
| APIM Developer | ~$50 |
| Front Door Standard (base) | ~$35 |
| App Configuration Standard × 2 squads | ~$72 |
| Private endpoints (~4 per squad × 2, plus backbone) | ~$70 |
| Service Bus Standard (base) | ~$10 |
| Log Analytics, Flex Consumption, ACR Basic, storage | <$20 at PoC volume |
| **Total, everything up 24/7** | **~$250–260** |

APIM is the slowest thing to provision (30–60 minutes), so it's the one to keep up during the
demo window. Squad RGs are cheap to tear down and recreate between sessions, and the PoC
should prove that it can.

---

## Alternatives Considered

- **Terraform instead of Bicep.** Rejected for consistency. Every other experiment in this repo
  is Bicep and AVM, and the module contract work already exists.
- **Azure Deployment Environments / Radius as the manifest engine.** These are worth a later
  comparison, but they hide the mechanism this PoC is meant to make visible.
- **A form and a platform engineer.** This is the ticket queue the plan explicitly rules out.

---

## Open Questions

1. **Where does this live?** Options are a new `experiments/azure-squad-workload-platform/` in
   this repo (the default, matching every other experiment), a set of new repos, or a section of
   AeroFlow. AeroFlow isn't reachable from this sandbox session. Moving it later means copying
   one folder.
2. **Manifest in the squad repo (option A)?** This needs `job_workflow_ref`-scoped federated
   credentials. It's worth a follow-up ADR if option B feels like too much friction in the demo.
3. **Shared App Configuration store** when squad count grows past roughly 5. It needs a decision
   on whether prefix-level leakage of non-secret config is acceptable.
4. **GitHub Actions or Azure DevOps.** `azure-squad-cicd-identity` uses ADO Environments for
   approval gates. This PoC defaults to GitHub Actions to match the runnable feature flag
   registry workflow, using GitHub Environments for the same gate.
