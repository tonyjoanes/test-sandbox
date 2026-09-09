# Bicep Module Contracts & Governance — with AVM

A reference set of Bicep files showing how to open up module authoring to multiple squads
without losing control of what gets deployed: a **contract** (what a squad must set, what it
may extend, and what it cannot touch) enforced by the Bicep type system itself, composed on
top of **Azure Verified Modules (AVM)** for the actual resource definitions, backed by a
**two-tier ownership model** and a CI gate.

> Like `azure-yaml-pipelines-examples`, this is reference material, not an executable demo —
> the sandbox this repo runs in has no `bicep`/`az` CLI and no Azure subscription. See
> [Using These Files](#using-these-files) to try it against a real subscription.

---

## The Problem This Answers

"Can squads write their own Bicep modules?" — yes, but unrestricted authoring means:

- No two squads name, tag, or scope a storage account the same way.
- A squad accidentally leaves `publicNetworkAccess` at its resource-provider default (often
  `Enabled`) because nothing forced an explicit choice.
- A squad picks an unapproved SKU because nothing told them it wasn't approved until a policy
  scan fails the deployment — after the PR already merged.
- Platform team's only lever is a `deny` policy assignment that fails deployments after the
  fact, which teaches squads to route around governance rather than build with it.

The fix isn't "stop squads from writing Bicep." It's making the **contract** — required
inputs, allowed extensions, forbidden territory — something the compiler enforces at the
squad's own PR, before it ever reaches policy or a human reviewer.

---

## Mental Model First

Three separate mechanisms do three separate jobs. Confusing them is the #1 way this kind of
setup ends up either too rigid (squads route around it) or too loose (it isn't governance):

| Mechanism | Answers | Enforced by | Example in this repo |
|---|---|---|---|
| **Required vs optional parameters** | "What must every deployment decide explicitly?" | Bicep — no default = required | `network` in `foundation/storage-account/main.bicep` has no default |
| **Typed extension points** | "What may a squad change, and within what limits?" | Bicep's type system — `@allowed`, unions, `@sealed()` | `sku approvedStorageSku = 'Standard_LRS'` |
| **Composition (AVM)** | "What does the resource actually look like on Azure?" | AVM module, pinned version | `module storageAccount 'br/public:avm/res/storage/storage-account:0.14.3'` |

A fourth layer — **policy-as-code / PSRule / Azure Policy** — exists to catch what the type
system structurally can't (cross-resource rules, "syntactically valid but still banned in
prod"). It's the safety net, not the primary control; see [Governance Layers](#governance-layers-in-order).

---

## The Contract, In One Picture

```
param name string                          ← REQUIRED: no default at all
param location string                       ← REQUIRED
param tags mandatoryTags                     ← REQUIRED, and shaped by a sealed type
param network networkPosture                 ← REQUIRED, unsealed on purpose (see below)

param sku approvedStorageSku = 'Standard_LRS'        ← OPTIONAL, but closed to an allow-list
param enableHierarchicalNamespace bool = false        ← OPTIONAL, plain bool is fine here
param advanced advancedOverrides = {}                  ← OPTIONAL, sealed escape hatch
param additionalTags object = {}                        ← OPTIONAL, deliberately open
```

Reading a module's parameter list top to bottom should tell a squad everything about the
contract without opening any other file:

- **No default → required.** There is no separate `required` keyword in Bicep; the absence
  of a default *is* the mechanism. Use it for anything platform wants decided explicitly at
  every call site (see `network.publicNetworkAccess` — no default, because "secure by
  accident" isn't a posture worth offering).
- **Default + closed type → "extend, but only within limits."** `approvedStorageSku` is a
  `@allowed([...])` string union. A squad can override the default SKU, but the compiler
  rejects anything not on the list — at `bicep build`, in the squad's own PR, not three stages
  later in a policy scan.
- **Default + `@sealed()` object type → a named, bounded escape hatch.** `advancedOverrides`
  exposes exactly two extra AVM properties. `@sealed()` means the compiler rejects any key
  that isn't one of those two — growing the escape hatch is itself a reviewed one-line change
  to the foundation module, not something a squad can do by just adding a property.
- **Default + plain, unsealed `object`/type → genuinely open.** `additionalTags` and
  `networkPosture` are intentionally not sealed — see the comments in `shared/types.bicep` for
  why sealing the wrong thing is just as much a mistake as sealing nothing.

`@sealed()` is the one decorator worth understanding well: it's the difference between "this
object type accepts additional properties beyond the ones declared" (default) and "it does
not" (`@sealed()`). Seal the types that back dashboards, cost queries, or compliance joins,
where an uncontrolled extra key silently breaks something downstream. Leave types unsealed
where the whole point is letting the surface grow without a contract edit — typically anything
that mirrors an upstream AVM module's own evolving optional properties.

---

## Why Wrap AVM Instead Of Writing `resource` Blocks Directly

[Azure Verified Modules](https://aka.ms/avm) are Microsoft- and community-maintained,
tested, kept current with resource-provider API versions, and cover the vast majority of
Azure resource types. Re-deriving all of that per squad, per resource type, is wasted effort
and a correctness liability (stale API versions, missed provider quirks).

What AVM does **not** give you is organisational control — an AVM module for a storage
account happily accepts `Premium_LRS`, public network access, and dozens of other properties
your organisation may not want every squad deciding for themselves. That's exactly the gap a
foundation module fills: **AVM owns correctness of the resource shape; the foundation module
owns which parts of that shape squads are allowed to touch, and how.**

```
              ┌─────────────────────────────┐
squad writes  │  workload/team-blob-store    │  ← composes, adds squad-only resources
              └──────────────┬──────────────┘
                              │ module call — required contract only
              ┌──────────────▼──────────────┐
platform owns │ foundation/storage-account   │  ← narrows AVM's surface, enforces the contract
              └──────────────┬──────────────┘
                              │ module call — full AVM surface, pinned version
              ┌──────────────▼──────────────┐
Microsoft/AVM │  avm/res/storage/storage-    │  ← the actual `Microsoft.Storage/...` resource,
    owns      │  account:0.14.3              │     kept current with the provider
              └─────────────────────────────┘
```

Never let a `workload/` module call an AVM module directly for a resource type that has a
foundation module — that's the one rule which, if broken, defeats everything else here. There
is no way to structurally prevent it in Bicep alone; it's enforced by `ci/ps-rule/ps-rule.yaml`
and code review, not by the compiler.

---

## Governance Layers, In Order

Each layer catches what the one before it structurally can't. Push every control as early
(left) as you can — a squad member who never merges an invalid module (layer 1) is entirely
different from one who deployed a policy-denied resource on a live subscription (layer 4):

| # | Layer | Catches | Files |
|---|---|---|---|
| 1 | **Bicep type system** | Missing required inputs, out-of-allow-list values, extra keys on sealed types — all at `bicep build`, on the squad's own machine, before a PR even opens | `shared/types.bicep`, `foundation/*/main.bicep` |
| 2 | **Linter (`bicepconfig.json`)** | Style/hygiene issues the type system doesn't express: unused params, hardcoded URLs, secrets in outputs | `bicepconfig.json` |
| 3 | **CODEOWNERS + required review** | "Did a human from the platform team actually look at this contract change?" — required only on `shared/` and `foundation/`, not `workload/` | `governance/CODEOWNERS.example` |
| 4 | **CI: build + lint + PSRule + what-if** | Anything layers 1–3 miss, plus a preview of the actual resource diff before it merges | `ci/azure-pipelines.yml`, `ci/ps-rule/ps-rule.yaml` |
| 5 | **Azure Policy (deny/audit)** | The absolute last line of defense — catches drift, out-of-band changes (portal, CLI, another tool entirely) that never went through this pipeline at all | *(not modelled here — lives in your Azure environment, not this repo)* |

Layer 5 is not optional even with layers 1–4 in place: it's the only layer that still applies
to a change that bypasses this repo entirely. But if layer 5 is doing most of the catching in
practice, that's a signal the contract in layers 1–3 is too loose, not that layer 5 is working
as intended.

---

## Two-Tier Ownership

| | `foundation/` | `workload/` |
|---|---|---|
| Owned by | Platform team | Individual squads |
| Wraps | AVM directly, pinned version | A foundation module (never AVM directly) |
| Parameter surface | Deliberately narrow — only what the org wants squads deciding | As wide as the squad needs for its own resources |
| Review gate | CODEOWNERS-mandated platform review | Normal squad review |
| Can loosen the contract? | Yes — that's the point; changes here are reviewed precisely because they can | No — structurally can't without a foundation-module change first |

This is the actual answer to "how do we control this when squads are writing the modules":
squads get genuine authoring freedom in `workload/`, but every `workload/` module is built
*out of* `foundation/` building blocks whose contract they cannot loosen from the outside. The
platform team's job shrinks to maintaining a small number of well-typed foundation modules
instead of reviewing every squad PR.

---

## Files

| File | Role |
|---|---|
| [`shared/types.bicep`](shared/types.bicep) | Exported contract vocabulary: `mandatoryTags` (sealed), `networkPosture` (unsealed), `approvedStorageSku` (allow-list) |
| [`foundation/storage-account/main.bicep`](foundation/storage-account/main.bicep) | Platform-owned module: wraps AVM, narrows the surface, defines the sealed `advancedOverrides` escape hatch |
| [`workload/team-blob-store/main.bicep`](workload/team-blob-store/main.bicep) | Squad-owned module: composes the foundation module, adds squad-only container resources |
| [`bicepconfig.json`](bicepconfig.json) | Registry aliases (public AVM + private platform registry) and linter rule levels |
| [`ci/azure-pipelines.yml`](ci/azure-pipelines.yml) | Mandatory gate: build, lint, PSRule, `what-if` — modelled on the `extends:` pattern in `azure-yaml-pipelines-examples/pipelines/12-extends-and-loops.yml` |
| [`ci/ps-rule/ps-rule.yaml`](ci/ps-rule/ps-rule.yaml) | Policy-as-code baseline (layer 4) |
| [`governance/CODEOWNERS.example`](governance/CODEOWNERS.example) | Which paths require platform sign-off vs normal squad review |
| [`governance/module-contract-checklist.md`](governance/module-contract-checklist.md) | PR checklist for anyone changing `foundation/` or `shared/` |

---

## Reading Order

1. **`shared/types.bicep`** — the contract vocabulary. Read every comment; the *why* behind
   sealed vs unsealed is the main lesson in this repo.
2. **`foundation/storage-account/main.bicep`** — see required/optional/sealed applied to a
   real AVM composition.
3. **`workload/team-blob-store/main.bicep`** — see a squad consume that contract and extend
   around it without touching it.
4. **`bicepconfig.json`** then **`ci/azure-pipelines.yml`** then **`ci/ps-rule/ps-rule.yaml`**
   — the layers that catch what the type system in steps 1–3 can't.
5. **`governance/CODEOWNERS.example`** and **`governance/module-contract-checklist.md`** — the
   human-process layer wrapping all of the above.

---

## Key Concepts Glossary

- **Required vs optional parameter** — in Bicep, a parameter with no default value is
  required; one with a default is optional. There is no separate `required` keyword.
- **User-defined type (UDT)** — a named type declared with `type name = {...}`, usable as a
  parameter or output type. Lets a contract be expressed once and imported everywhere instead
  of repeated inline object shapes.
- **`@export()`** — marks a type (or variable/function) in one Bicep file as importable by
  other files via `import { name } from 'path.bicep'`. This is how `shared/types.bicep`
  becomes the single source of truth for contract shapes.
- **`@sealed()`** — on an object type, forbids any property not explicitly declared on the
  type. Use it to close a surface (tags, anything downstream tooling depends on having a fixed
  key set); leave it off to intentionally leave a surface open to grow.
- **`@allowed([...])`** — restricts a parameter (or a type built from `string`) to a fixed set
  of literal values, checked at compile time. The mechanism behind `approvedStorageSku`.
- **Azure Verified Modules (AVM)** — Microsoft/community-maintained, tested Bicep (and
  Terraform) modules for individual resources (`avm/res/...`) and multi-resource patterns
  (`avm/ptn/...`), published to the public Bicep registry (`br/public:...`) and kept current
  with resource-provider API versions.
- **Foundation vs workload module** — the two-tier ownership split used in this repo:
  foundation modules (platform-owned, wrap AVM, narrow the contract) vs workload modules
  (squad-owned, compose foundation modules, add squad-specific resources).
- **Private module registry (ACR)** — an Azure Container Registry used as a Bicep module
  registry (`br/platform:...` in `bicepconfig.json`) for publishing versioned, organisation-
  internal modules, as distinct from the public AVM registry.
- **PSRule for Azure** — a policy-as-code tool that lints compiled ARM/Bicep output against
  Azure Well-Architected and custom organisational rules; runs after `bicep build` succeeds,
  catching what the type system can't express structurally.
- **`what-if`** — `az deployment group what-if` previews the actual resource-level diff a
  deployment would make, without deploying. Run in CI as the last check before merge so a
  reviewer (and the pipeline) sees intent, not just "it compiled."

---

## Using These Files

These are reference examples, not wired into a live Azure subscription from this sandbox
(there is no `bicep`/`az` CLI here — see the note at the top). To try them for real:

1. Install the [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) with the
   Bicep extension (`az bicep install`), or the standalone `bicep` CLI.
2. From this folder, validate compilation: `az bicep build --file foundation/storage-account/main.bicep`
   and `az bicep build --file workload/team-blob-store/main.bicep`.
3. To see the contract actually reject something, try changing `sku: 'Standard_ZRS'` in
   `workload/team-blob-store/main.bicep` to `sku: 'Premium_LRS'` and re-run `bicep build` — it
   fails at compile time because `Premium_LRS` isn't in `approvedStorageSku`'s allow-list.
4. For the private registry references in `bicepconfig.json` to resolve, replace
   `contoso.azurecr.io` with a real ACR instance you've enabled as a Bicep registry
   (`az bicep publish --file foundation/storage-account/main.bicep --target br:<yourRegistry>.azurecr.io/modules/storage-account:1.0.0`).
5. `ci/azure-pipelines.yml` assumes an Azure DevOps project with a `bicep-validation`
   Environment (Pipelines → Environments) and a service connection with rights to
   `rg-bicep-validation`; the `ps-rule-assert@2` task needs the
   [PSRule extension](https://marketplace.visualstudio.com/items?itemName=bewhite.ps-rule-vscode-adjacent-pipelines-task)
   installed on the organisation.
6. `governance/CODEOWNERS.example` needs renaming to `CODEOWNERS` (or
   `.github/CODEOWNERS`) at the repo root, with `@contoso/...` replaced by real team handles,
   before the platform/squad review split actually takes effect.
