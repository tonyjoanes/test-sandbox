# Squad Workload Platform: from Manifest to Live Endpoint

A small internal platform. A squad describes what it needs in a `workload.yaml` and opens a PR.
A platform pipeline turns that into a private, wired-up resource group behind a public
Front Door route. After that the squad ships code only. Policy, not a reviewer, stops anything
that breaks the guardrails.

> **What runs here and what doesn't.** Manifest validation, cross-squad checks, rendering
> parameters and deploy ordering are real and tested (`npm test`, 13 tests). Every `.bicep`
> file compiles and lints clean in CI. The Azure jobs (what-if, deploy, module release,
> teardown) are wired up but only run once the repo variables in
> [Running This for Real](#running-this-for-real) are set. Nothing here has been deployed to a
> subscription yet. See [Verify on First Deploy](#verify-on-first-deploy).

---

## The Problem This Answers

The other experiments in this repo each cover one layer:

| Experiment | Answers |
|---|---|
| `azure-landing-zone-squad-vending` | **Where** does a squad deploy? |
| `azure-bicep-module-contracts-governance` | **What** may it deploy? |
| `azure-squad-cicd-identity` | **How** does it deploy, with no stored secrets? |
| `azure-policy-examples` | What catches everything else? |

None of them answers the question a squad actually asks: *"I need a Key Vault, some config, a
blob container, a topic and a public URL for my function. How do I get those without filing a
ticket?"*

The usual answers fail in one of two ways:

- **A form, and a platform engineer working through it.** That's a ticket queue, and the
  platform team becomes the bottleneck for every new topic.
- **Contributor on a subscription, and good luck.** Every squad hand-rolls its own private
  endpoints and diagnostics, and half of them forget one.

This experiment takes a third route. The request is a **manifest** reviewed by the squad that
owns it. The platform's work is encoded once in **modules**, and **policy** guards the gap
between the two.

---

## Mental Model First

| Piece | Owned by | Job |
|---|---|---|
| `platform/squads/<squad>/workload.yaml` | The squad (CODEOWNERS) | Says *what* the squad needs |
| `platform/schema/workload.schema.json` | Platform | The squad-facing API: anything not in here can't be requested |
| `platform/modules/*` | Platform | Says *how* each capability is built. Private endpoint, diagnostics and RBAC are built in, with no switch to turn them off |
| `platform/squad/main.bicep` | Platform | Turns one manifest into one resource group, using the modules |
| `platform/policy/main.bicep` | Platform | Catches anything that skips the modules, mostly in squads' own `infra/` |
| `squad-template/` | The squad | Their code, plus an optional `infra/` escape hatch |

The flow, end to end:

```
squad PR: squads/orders/workload.yaml
   │  validate.mjs (schema + cross-squad checks)  ── no Azure needed
   │  bicep build                                  ── no Azure needed
   │  what-if, read-only planner identity
   ▼  CODEOWNERS approval from the orders squad → auto-merge
main
   │  deployer identity: rg-squad-orders, function app, KV, App Config, storage, topic,
   ▼  APIM API + Front Door route, role assignments, a squad deploy identity
squad repo push
   │  squad identity (Squad Workload Contributor on rg-squad-orders only)
   ▼  code → function app;   infra/ → their own extras, inside policy
https://<frontdoor>/orders/health
```

---

## The Contract

```yaml
apiVersion: platform/v1
squad: orders
repository: tonyjoanes/orders-service
capabilities:
  keyVault: true
  appConfig: true
  storage: [invoices]
  topics: [order-placed]
  expose: { public: true, path: /orders }
```

Compared with the original idea, the manifest adds three things:

- **`apiVersion`**, so the contract can change without breaking squads already using it.
- **`repository`**: the squad's deploy identity trusts OIDC tokens from this repo's
  `production` environment and nothing else. The manifest is the only place that link is
  declared.
- **`subscribe`**: consuming another squad's topic (see `squads/payments/`). Topics are
  created as `<squad>.<topic>` in the shared namespace, so two squads can both have an
  `order-placed` without colliding.

`validate.mjs` checks what one schema can't, because these checks need every manifest or the
file's location:

- `squad` must match its folder. CODEOWNERS works per folder, so a mismatch would let one squad
  edit another's resources.
- `expose.path` must be unique across squads.
- Every `subscribe` must point at a topic some squad actually publishes.

`render-params.mjs --list` returns the squads in deploy order, publishers before subscribers.

---

## Where the Original Plan Doesn't Hold

These were found while building it. Each one changed what got built.

| Original plan | Problem | What's built instead |
|---|---|---|
| Front Door Standard with private endpoints | **Private Link origins are a Front Door Premium feature** (about $330/month base vs about $35) | Front Door Standard → APIM *public* gateway in **external VNet mode**. APIM accepts only Front Door traffic (NSG allows the `AzureFrontDoor.Backend` tag, and a global policy checks `X-Azure-FDID` against *our* profile). APIM → function goes over the VNet |
| Service Bus with private endpoints | **Private endpoints on Service Bus need Premium** (about $680/month) | Standard namespace with `disableLocalAuth` (Entra only, no SAS keys) and RBAC scoped to each topic and subscription. One policy exemption, scoped to that single resource, expiring in 6 months |
| Function app with a private endpoint | Classic Consumption supports neither inbound PE nor VNet integration. On Flex Consumption, disabling public access also makes the **deploy (SCM) endpoint** private, which GitHub-hosted runners can't reach | Flex Consumption with VNet integration for outbound. Inbound is closed by an **access restriction allowing only the APIM subnet**; SCM stays reachable for deploys, with Entra auth only (basic auth is off) |
| Squads "can't create Key Vaults" *and* "can add custom infra" | These conflict. If RBAC blocks squads from creating resources, demo 2 fails with `AuthorizationFailed`, which proves nothing about policy | Custom role **Squad Workload Contributor**: Contributor minus networking, role assignments, platform-owned types (KV, App Config, function plan and site config) and federated credentials. It *can* create storage accounts, so policy is what stops the public one |
| "Require diagnostics" policy | A diagnostic setting is created *after* its parent, so a Deny fires before the setting could exist | `DeployIfNotExists`, with a remediation identity |
| Budget alert as the cost guard | Budgets notify; they never stop anything | A budget alert **plus** a `teardown-squads` workflow that deletes and purges squad resource groups |

---

## Three Identities

| Identity | Trusts (OIDC subject) | Can |
|---|---|---|
| `id-platform-planner` | `repo:<platform>:pull_request` | Read and what-if only. PR code is untrusted, so it never gets the deployer |
| `id-platform-deployer` | `repo:<platform>:ref:refs/heads/main` | Contributor, plus **RBAC Administrator with an ABAC condition** limiting it to the 8 roles the modules hand out. It can't assign Owner, Contributor or User Access Administrator |
| `id-squad-<squad>-deployer` | `repo:<squad repo>:environment:production` | Squad Workload Contributor on `rg-squad-<squad>` only |

All three are user-assigned managed identities with federated credentials: no app
registrations and no secrets. Squad deployer identities live in the platform's identity
resource group, **not** the squad's. Otherwise a squad could add its own federated credential
and let any repo deploy as them.

The function app's system-assigned identity gets **data-plane roles at the narrowest scope**:

- Key Vault Secrets User on the vault
- App Configuration Data Reader on the store
- Blob Data Contributor per **container**
- Service Bus Data Sender per **topic**, Data Receiver per **subscription**

---

## The Guardrails

One initiative, assigned at subscription scope, so it applies to a squad's resource group from
the moment the group exists:

| Policy | Effect | Note |
|---|---|---|
| Data services must disable public network access | Deny | Storage, Key Vault, App Config, Service Bus. `notEquals 'Disabled'`, so leaving the property out also counts as public |
| Diagnostics to the platform workspace | DeployIfNotExists | Mostly catches what squads deploy from `infra/`. The modules already set diagnostics |
| Resource groups need a `squad` tag | Deny | Only the pipeline creates resource groups, and it always tags them |
| Inherit `squad` tag from the resource group | Modify | So squads' `infra/` resources get tagged whether or not they remembered |

---

## The Escape Hatch

`squad-template/infra/main.bicep` is deployed by the squad's own pipeline with the squad's
own identity: no platform review, no ticket. The example is an alert on the function's 5xx
responses, the kind of thing that shouldn't need anyone's permission.

RBAC and policy bound it, not review: no networking, no role assignments, nothing public,
diagnostics enforced. A second Key Vault isn't an `infra/` change; it's a manifest change,
which arrives with its private endpoint already wired.

---

## App Configuration Cost

Each squad with `appConfig: true` gets its own Standard store, about $36/month at list price
(Standard is the cheapest tier with private endpoints).

A shared store with key prefixes and labels would cost one store, but App Config RBAC is
**store-wide**: any squad with Data Reader can read every other squad's keys. That's tolerable
for non-secret config and never for anything sensitive. So this is a deliberate trade-off, and
`appConfig` defaults to `false` (`payments` doesn't use it).

The manifest keeps the choice reversible. `appConfig: true` is an abstraction, so the platform
could move to a shared store later without any squad changing a line.

---

## Module Versioning

Each module carries `metadata version`. The ops workflow's `release-module` action checks that
the version was bumped and isn't already published, then publishes to the registry as
`br/platform:<module>:<version>` and tags the commit `module/<module>/v<version>`.

It runs from `main` rather than on tag push, so it can reuse the deployer's single federated
credential. A tag-triggered run would present a different OIDC subject.

`squad/main.bicep` references modules by relative path, because there's no registry to pull
from in this sandbox. Against a real registry, those lines become
`br/platform:key-vault:1.0.0`, and that's what makes a module change an explicit, per-version
opt-in.

---

## Files

```
platform/
  schema/workload.schema.json        the contract
  squads/{orders,payments}/          two example squads
  CODEOWNERS.example                 what makes the PR self-service
  scripts/                           validate.mjs, render-params.mjs, tests
  naming.bicep                       every shared name, derived from the subscription ID
  modules/                           key-vault, app-config, storage, servicebus-topic,
                                     servicebus-subscription, function-app, api-route,
                                     private-endpoint
  identity/                          bootstrap 1: platform identities + custom roles
  backbone/                          bootstrap 2: Log Analytics, VNet + DNS, Service Bus,
                                     APIM, Front Door, module registry
  policy/                            bootstrap 3: the initiative + the one exemption
  squad/main.bicep                   one manifest → one resource group
squad-template/
  app/                               the function app (Node, Flex Consumption)
  infra/main.bicep                   the escape hatch
  infra/demo/public-storage.bicep    demo 2: what policy refuses
  .github-example/workflows/         the squad repo's whole pipeline
../../.github/workflows/
  squad-workload-platform.yml        validate, build, what-if, deploy
  squad-workload-platform-ops.yml    release-module, teardown-squads
```

---

## Running This for Real

Use a personal subscription, not a work one.

1. **Budget.** Create a budget with alerts at 50/80/100%. It only notifies; teardown is what
   stops spend.
2. **Bootstrap.** These three steps run once, as a subscription Owner:
   ```bash
   cd platform
   az deployment sub create -l uksouth -f identity/main.bicep -p platformRepository=<owner>/<repo>
   az deployment sub create -l uksouth -f backbone/main.bicep \
     -p publisherEmail=<you> platformDeployerPrincipalId=<deployerPrincipalId output>
   az deployment sub create -l uksouth -f policy/main.bicep
   ```
   APIM takes 30–60 minutes. Leave the backbone up between sessions and tear down squads
   instead.
3. **Repo variables** on the platform repo: `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`,
   `PLATFORM_DEPLOYER_CLIENT_ID`, `PLATFORM_PLANNER_CLIENT_ID`, and
   `PLATFORM_REGISTRY_LOGIN_SERVER`.
4. **Branch protection** on `main`: require the `Validate manifests` and `Build and lint
   Bicep` checks plus code owner review, and allow auto-merge. Copy `CODEOWNERS.example` to
   `.github/CODEOWNERS`.

### The demo

1. **Empty repo to live endpoint.** Create the squad repo from `squad-template/`. Open a PR
   adding `squads/orders/workload.yaml`, approve it as the squad, and let it merge. Copy the
   deployment outputs into the squad repo's variables and push. Then
   `curl https://<frontdoor>/orders/health` returns
   `{"squad":"orders","checks":{"keyVault":"ok","appConfig":"ok","storage":"ok"}}`.
2. **Policy blocks a shortcut.** As the squad identity, run
   `az deployment group create -g rg-squad-orders -f infra/demo/public-storage.bicep`. It fails
   with **`RequestDisallowedByPolicy`** and the assignment's message pointing at the manifest.
   It does not fail with `AuthorizationFailed`.
3. **Not a ticket queue.** The squad adds `order-cancelled` to `topics` in a PR. It merges on
   their own approval, and the topic exists with Data Sender granted, with no platform human
   involved.

### Rough monthly cost at list price

Verify these in the pricing calculator before deploying.

| Item | ~ per month |
|---|---|
| APIM Developer | $50 |
| Front Door Standard (base) | $35 |
| App Configuration Standard (orders only) | $36 |
| Private endpoints (6 across both squads) | $45 |
| Service Bus Standard (base) | $10 |
| Log Analytics (1 GB/day cap), Flex Consumption, ACR Basic, storage | < $20 |
| **Everything up 24/7** | **about $200** |

---

## Verify on First Deploy

These compile but rest on behaviour that only a real deploy proves:

- **Flex Consumption deploy with the SCM site public and the host storage private.** The
  platform pulls the package from a private blob container through VNet integration. If that
  fails, the fallback is to give the deployment container's account a private endpoint *and*
  a firewall exception for trusted Azure services.
- **Deploying code with `sites/config/write` blocked** in the squad role. `functions-action`
  for Flex shouldn't need it. If it does, the fix is a narrower NotAction for the access
  restriction settings, not handing back all of config.
- **The planner identity's what-if** with Reader plus the custom "Deployment What-If Runner"
  role. What-if may also want `deployments/write` at subscription scope on some resource types.
- **Sharing one Flex integration subnet** between squads' function apps. If that isn't
  allowed, each squad needs its own `/27`, which `squad/main.bicep` would carve out of the
  backbone VNet.

## Known Gaps

- **One resource group per squad in one subscription**, not subscription vending. That's a
  deliberate downgrade so the experiment fits a personal subscription. `naming.bicep` and
  `squad/main.bicep` are the seams to swap for `azure-landing-zone-squad-vending`.
- **One environment.** No dev, staging or prod split.
- **The manifest lives in the platform repo**, not the squad repo. Putting it in the squad repo
  would mean the platform identity trusting a pipeline the squad can edit. Doing that safely
  needs federated credentials bound to a reusable-workflow ref, which is worth a follow-up but
  not this experiment.
- **APIM has no subscription key or JWT validation** on squad APIs, so the demo is a plain
  `curl`.
- **Removing a capability from a manifest doesn't delete the resource.** Deployments are
  incremental. A "prune" step would compare the manifest with what's tagged in the resource
  group.
