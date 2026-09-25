# Azure Landing Zones — Self-Service Squad Vending

A reference set of Bicep files showing how to give squads a safe, self-service place to deploy
and innovate: subscription-per-squad isolation, a management group hierarchy that applies
guardrails automatically on arrival, and two deliberately different tiers — a loose,
spend-capped **sandbox** for pure innovation, and a stricter, network-connected **landing
zone** for workloads that are staying.

> Like the other experiments in this repo, this is reference material — no `bicep`/`az` CLI or
> Azure subscription in this sandbox. See [Using These Files](#using-these-files) to try it
> against a real tenant.

> **How this relates to `azure-bicep-module-contracts-governance/`:** that experiment answers
> "once a squad has somewhere to deploy, how do we keep their Bicep modules honest." This one
> answers "how do they get the somewhere in the first place." A vended subscription from here
> is the environment; the contract-governed modules from that experiment are what a squad
> actually deploys inside it.

---

## The Problem This Answers

"Give squads a safe place to deploy and innovate" sounds like one request but is actually two,
and conflating them is the usual failure mode:

- **Too safe, not usable.** One shared subscription, heavily locked down, every deployment
  reviewed by platform. Squads can't move fast enough to actually innovate in it, so they
  either stop trying or find a way around it (a personal Azure subscription on a credit card
  is a real, common outcome of this).
- **Usable, not safe.** Give squads broad rights in a shared environment so they can move fast.
  One squad's runaway spend, noisy-neighbor resource contention, or overly permissive NSG rule
  now affects everyone else sharing that subscription.

The fix is two tiers with genuinely different trade-offs, not one compromise tier that's both
too slow and not safe enough: a **sandbox** squads can self-serve into within minutes, isolated
enough that a mistake there can't reach anything that matters, and a **landing zone** for
workloads that have earned real platform investment (network connectivity, production support,
a bigger spend cap) by actually needing it.

---

## Mental Model First

Three mechanisms, each doing a different job — mixing them up is how "landing zone" setups end
up either too rigid or accidentally leaky:

| Mechanism | Answers | Where |
|---|---|---|
| **Management group hierarchy** | "What policy does this subscription inherit, automatically, forever?" | `management-groups/mg-hierarchy.bicep` |
| **Tiered guardrail policy** | "What can a squad actually do inside their subscription?" | `policy/landing-zone-guardrails.bicep`, `policy/sandbox-guardrails.bicep` |
| **Subscription vending** | "How does a squad actually get one of these, without filing a ticket every time?" | `vending/main.bicep` |

A subscription's tier isn't a tag or a setting a squad can change later — it's **which
management group the subscription lives under**, decided once at vending time. Everything else
(which policy initiative applies, whether a spoke network gets deployed) follows from that one
decision. This is the same "structure encodes the rule" idea as `@sealed()` in the Bicep
contracts experiment, one level up the stack: here it's management group placement instead of a
type system, but the goal is identical — make the safe thing the thing that's actually true,
not a policy statement someone has to remember to keep enforcing.

---

## The Hierarchy, In One Picture

```
Tenant Root
├── Platform                         ← platform-owned, squads never deploy here
│   ├── Identity
│   ├── Management                   (central Log Analytics, central automation)
│   └── Connectivity                 (the hub VNet, firewall, DNS)
│
├── Landing Zones                    ← STRICT guardrails, network-connected
│   ├── Corp                         (needs to reach the corp network)
│   └── Online                       (internet-facing, no corp connectivity)
│
├── Sandboxes                        ← LOOSE guardrails, spend-capped, no network
│
└── Decommissioned                   ← deny-everything, subscriptions on the way out
```

A squad's subscription only ever lives under **Landing Zones** or **Sandboxes** — never
directly under the tenant root, and never under **Platform**. See
`management-groups/mg-hierarchy.bicep`.

---

## Sandbox vs Landing Zone

| | Sandbox | Landing zone (Corp / Online) |
|---|---|---|
| Who approves | Squad lead — self-service | Platform (network + spend commitment) |
| Turnaround | Minutes | Days |
| Resource policy | Loose — audits rather than blocks most things (`policy/sandbox-guardrails.bicep`) | Strict — blocks public IPs, requires diagnostics, enforces tagging (`policy/landing-zone-guardrails.bicep`) |
| Network | None — no VNet is deployed at all (`vending/modules/network-spoke.bicep` is never called) | Spoke VNet peered to the platform hub |
| Spend cap | Small, default-able | Sized per workload, explicit |
| Lifetime | Expires — teardown date set at request time | Indefinite |
| Subscription workload type | `DevTest` | `Production` |

A sandbox trades network reach and policy strictness for speed; a landing zone trades speed for
the platform guarantees (corp connectivity, production support) that come with a stricter
contract. Neither is the "correct" default — see `intake/request-template.md` for how a squad
picks, and how a sandbox graduates into a landing zone once a workload is actually staying.

---

## Why Group RBAC, Never Named Users

Every RBAC assignment in this experiment (`vending/modules/rbac.bicep`) targets an Entra
**group** object ID, never an individual user. Two reasons, both load-bearing:

- **Turnover.** A subscription owned by a named person becomes an access-review fire drill the
  day they leave the squad. Owned by a group, membership changes and the subscription's access
  model doesn't.
- **Auditability at scale.** With dozens of squad subscriptions, "who can deploy to
  `sq-payments-prod`" needs to be answerable by looking at one group's membership, not by
  auditing role assignments subscription-by-subscription.

If a request template ever asks for an individual's object ID instead of a group's, that's a
sign the group doesn't exist yet — the fix is creating the group first, not assigning the role
to a person as a stopgap.

---

## Files

| File | Role |
|---|---|
| [`management-groups/mg-hierarchy.bicep`](management-groups/mg-hierarchy.bicep) | Tenant-scope: creates the Platform/Landing Zones/Sandboxes/Decommissioned structure |
| [`policy/landing-zone-guardrails.bicep`](policy/landing-zone-guardrails.bicep) | Strict guardrail initiative, assigned at the Landing Zones MG |
| [`policy/sandbox-guardrails.bicep`](policy/sandbox-guardrails.bicep) | Loose guardrail initiative, assigned at the Sandboxes MG |
| [`vending/main.bicep`](vending/main.bicep) | Orchestrator: creates the subscription, RBAC, budget, and (landing-zone tier only) network, in one deployment |
| [`vending/modules/subscription.bicep`](vending/modules/subscription.bicep) | Creates the subscription and places it under the target MG at creation time |
| [`vending/modules/rbac.bicep`](vending/modules/rbac.bicep) | Grants the squad's Entra group a role at subscription scope |
| [`vending/modules/budget.bicep`](vending/modules/budget.bicep) | Spend cap + threshold notifications — the financial half of "safe to innovate in" |
| [`vending/modules/network-spoke.bicep`](vending/modules/network-spoke.bicep) | Spoke VNet peered to the hub — only ever deployed for landing-zone tier |
| [`vending/params/sandbox.bicepparam`](vending/params/sandbox.bicepparam) | Example: a squad's pure-innovation sandbox request |
| [`vending/params/landing-zone.bicepparam`](vending/params/landing-zone.bicepparam) | Example: the same workload graduating to a landing zone |
| [`intake/request-template.md`](intake/request-template.md) | The squad-facing "how do I get one" form, with the sandbox-vs-landing-zone decision built in |

---

## Reading Order

1. **`management-groups/mg-hierarchy.bicep`** — the structure everything else hangs off.
2. **`policy/landing-zone-guardrails.bicep`** then **`policy/sandbox-guardrails.bicep`** — read
   them side by side; the contrast is the point.
3. **`vending/modules/subscription.bicep`** — how a subscription is created already inside the
   right management group, not moved there afterward.
4. **`vending/modules/rbac.bicep`**, **`vending/modules/budget.bicep`**,
   **`vending/modules/network-spoke.bicep`** — the three things every vended subscription gets
   (network conditionally), each self-contained.
5. **`vending/main.bicep`** — how the pieces above compose into one deployment, and how
   `tier` drives every decision downstream of it.
6. **`vending/params/sandbox.bicepparam`** then **`vending/params/landing-zone.bicepparam`** —
   the same squad's workload at two points in its life.
7. **`intake/request-template.md`** — the human process in front of all of the above.

---

## Key Concepts Glossary

- **Management group** — a container above subscriptions in Azure's resource hierarchy, used
  to apply policy and RBAC to many subscriptions at once by inheritance.
- **Policy inheritance** — a policy assignment at a management group applies to every
  subscription under it, present and future, with no per-subscription action required. The
  actual mechanism behind "a vended subscription is governed the moment it's created."
- **Subscription vending** — provisioning a new Azure subscription programmatically (via
  `Microsoft.Subscription/aliases`) as a repeatable, self-service operation, rather than a
  manual request to a billing admin each time.
- **Hub-and-spoke network** — a central "hub" VNet (firewall, DNS, gateway) that spoke VNets
  peer into for shared connectivity and centralized egress, instead of each subscription
  managing its own internet/on-prem connectivity independently.
- **Sandbox tier** — a subscription tier optimized for speed and blast-radius containment over
  capability: fast to get, loosely policed, financially capped, and deliberately disconnected
  from the corp network.
- **Landing zone tier** — a subscription tier optimized for platform guarantees over speed:
  slower to get, strictly policed, network-connected, intended for workloads that are staying.
- **Group-based RBAC** — granting roles to an Entra security group rather than named
  individuals, so access survives team membership changes without a manual reassignment step.
- **Spend cap / budget threshold** — `Microsoft.Consumption/budgets`, notifying (or, in a fuller
  rollout, triggering automation) as actual or forecasted spend crosses a percentage of a fixed
  monthly amount.

---

## Using These Files

Reference examples, not wired into a live tenant from this sandbox — no `bicep`/`az` CLI here.
To try them for real:

1. `management-groups/mg-hierarchy.bicep` needs Owner (or Management Group Contributor) at the
   tenant root — deploy it once, as platform, before anything else in this folder makes sense:
   `az deployment tenant create --location uksouth --template-file management-groups/mg-hierarchy.bicep --parameters tenantRootGroupId=<your-tenant-id>`.
2. Deploy the two guardrail initiatives at their respective management groups:
   `az deployment mg create --management-group-id contoso-landing-zones --location uksouth --template-file policy/landing-zone-guardrails.bicep`
   and the same pattern for `contoso-sandboxes` with `policy/sandbox-guardrails.bicep`.
3. `vending/main.bicep` needs `Microsoft.Subscription/aliases` write rights and a valid
   `billingScopeId` for your EA or MCA account — replace the placeholder values in
   `vending/params/*.bicepparam` before deploying:
   `az deployment tenant create --location uksouth --template-file vending/main.bicep --parameters vending/params/sandbox.bicepparam`.
4. `policy/landing-zone-guardrails.bicep`'s `require-diagnostics` entry uses a placeholder
   policy definition ID — the real "require diagnostic settings" built-in varies by resource
   type; pick the ones relevant to your landing zone workloads before using this for real.
5. The hub VNet referenced by `vending/params/landing-zone.bicepparam` (`hubVnetResourceId`)
   isn't built by this experiment — it's assumed to already exist in Platform - Connectivity,
   deployed and owned by platform separately.
