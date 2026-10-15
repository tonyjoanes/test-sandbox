# Squad Workload Platform: Manifest to Live Endpoint

A PoC for a self-service internal platform. A squad declares what it needs in a `workload.yaml`
and opens a PR. A platform pipeline turns that into a private, wired-up resource group behind a
public Front Door route. The squad ships only code, and policy stops anything that would break
the guardrails.

> **Status: design only.** This folder holds the [ADR](docs/adr-0001-manifest-driven-squad-workloads.md)
> and the [issue plan](docs/issue-plan.md). Nothing is built yet.

## How This Relates to the Other Experiments

| Experiment | What it covers | How this PoC uses it |
|---|---|---|
| `azure-landing-zone-squad-vending` | Where a squad deploys (a vended subscription per squad) | Replaced by one RG per squad here, to fit one personal subscription. The seam is kept so it can move back |
| `azure-bicep-module-contracts-governance` | What a squad may deploy (typed module contracts) | Same contract style: private endpoints, diagnostics and RBAC aren't optional |
| `azure-squad-cicd-identity` | How deployments authenticate (OIDC, one identity per tier) | Same pattern, with a platform identity and a squad identity |
| `azure-policy-examples` | The policy safety net | Three of those patterns are assigned for real here |

The new piece is the **manifest**: the squad-facing API that ties the other four together.

## Read First

1. [ADR-0001](docs/adr-0001-manifest-driven-squad-workloads.md), in particular the
   *Consequences* section. It lists where the original plan doesn't hold up against Azure SKU
   limits.
2. [Issue plan](docs/issue-plan.md): six PRs, with spend starting only at Part 3.
