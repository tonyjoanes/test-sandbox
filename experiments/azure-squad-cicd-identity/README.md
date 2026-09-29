# CI/CD Identity & Deployment Pipeline — Closing the Loop

The third piece of a self-service platform, after the other two experiments in this repo:
`azure-bicep-module-contracts-governance` defines **what** a squad is allowed to deploy;
`azure-landing-zone-squad-vending` gives them **where** to deploy it. This one is **how** —
a pipeline that deploys a squad's contract-governed Bicep modules into their vended landing
zone, authenticating with no stored secret at all, and gated by the same sandbox-vs-landing-
zone distinction the other two experiments already established.

> Like the rest of this repo, this is reference material — no `az`/`bicep` CLI, Azure DevOps
> project, or Azure tenant in this sandbox. See [Using These Files](#using-these-files).

---

## The Problem This Answers

A squad has a landing zone and a governed Bicep module. How does either become a running
resource, without:

- **A stored secret.** A client secret in a pipeline variable group is a secret someone has to
  rotate, that can leak in a log line, and that grants whatever it's scoped to for as long as
  it's valid — regardless of whether the pipeline that's using it right now is the one it was
  issued for.
- **One identity for everything.** A single service principal with Contributor across every
  squad's every subscription is a single compromise away from every squad's blast radius at
  once — the opposite of the isolation `azure-landing-zone-squad-vending` was built to provide.
- **A gate that lives only in policy.** "Landing-zone deploys need approval" has to be a fact
  about the pipeline, not a note in a wiki a squad could route around by editing their own YAML.

Workload identity federation (no secret, ever) plus one identity per tier (not one identity
overall) plus Azure DevOps Environments (approval gates the pipeline author can't bypass from
inside the YAML) answers all three at once — see below for how they fit together.

---

## Mental Model First

| Mechanism | Answers | Where |
|---|---|---|
| **Federated credential** | "How does a pipeline authenticate with no stored secret?" | `identity/ci-identity.bicep` |
| **One identity per tier** | "How does a sandbox pipeline stay unable to touch the landing-zone subscription?" | `identity/ci-identity.bicep` + `identity/ci-identity-rbac.bicep`, deployed twice |
| **Environment approval gates** | "How does 'landing-zone deploys need sign-off' become unavoidable, not just documented?" | `pipelines/templates/deploy-stage.yml`'s `environment:` |

---

## Why Two Identities, Not One With Two Credentials

The tempting shortcut is one app registration per squad, with two federated credentials (one
per tier) hanging off it. Don't — federated credentials control **who can exchange a token for
an identity**; they don't scope **what that identity can then do**. If both credentials sit on
the same app, and `ci-identity-rbac.bicep` has granted that app Contributor on both the sandbox
and landing-zone subscriptions (which it will, the first time someone runs it twice against
the same app to "finish the setup"), then a token minted via the sandbox credential is a token
for an identity that also has Contributor on the landing-zone subscription. The tier isolation
`azure-landing-zone-squad-vending` spent an entire experiment establishing evaporates at the
identity layer.

Two separate app registrations, each with exactly one federated credential and RBAC on exactly
one subscription, costs one extra Graph deployment per squad and means there is no shortcut
that quietly merges the two tiers back together. See `identity/ci-identity.bicep`'s header
comment and `identity/params/` for the two deployments this actually means in practice.

---

## Environments (Azure DevOps Portal, Not YAML)

Two Environments per squad, created in the Azure DevOps portal (Pipelines → Environments), not
declared anywhere in this repo's YAML — same reasoning as `azure-yaml-pipelines-examples`'
glossary entry on this: whoever can edit a pipeline file shouldn't also be able to grant
themselves deploy approval by editing the same file.

| Environment | Approvers | What happens without one |
|---|---|---|
| `sq-<squad>-sandbox` | None | Deploys immediately on every push to `main` |
| `sq-<squad>-landing-zone` | Squad lead (or platform, per your rollout — see `azure-bicep-module-contracts-governance/ADOPTION.md`'s rollout sequencing for the same "don't over-gate on day one" reasoning) | Deployment job pauses at `environment: sq-<squad>-landing-zone` in `pipelines/templates/deploy-stage.yml` until approved |

The service connection each Environment's deployments use (`payments-sandbox` /
`payments-landingZone` in the example pipeline) must be created with **Workload Identity
federation** as its authentication method, pointed at the matching app registration from
`identity/ci-identity.bicep` — that's the step that actually wires the federated credential to
a name your YAML can reference.

---

## Files

| File | Role |
|---|---|
| [`identity/ci-identity.bicep`](identity/ci-identity.bicep) | Tenant-scope: one app registration + service principal + federated credential, for one squad, one tier |
| [`identity/ci-identity-rbac.bicep`](identity/ci-identity-rbac.bicep) | Subscription-scope: grants that service principal Contributor on exactly one subscription |
| [`identity/params/sandbox.bicepparam`](identity/params/sandbox.bicepparam), [`identity/params/landing-zone.bicepparam`](identity/params/landing-zone.bicepparam) | The two deployments of `ci-identity.bicep` a squad actually needs |
| [`pipelines/templates/deploy-stage.yml`](pipelines/templates/deploy-stage.yml) | Reusable stage: validate, what-if, deploy, gated by an Environment |
| [`pipelines/deploy-workload.yml`](pipelines/deploy-workload.yml) | A squad's actual pipeline — two calls to the template above, deploying the workload module from `azure-bicep-module-contracts-governance` |

---

## Reading Order

1. **`identity/ci-identity.bicep`** — read the header comment fully before the code; the
   "why two identities" reasoning is the load-bearing idea in this experiment.
2. **`identity/ci-identity-rbac.bicep`** — Contributor, never Owner, and why that split matters
   once a pipeline can already authenticate without a human in the loop.
3. **`identity/params/`** — the two deployments this actually means for one squad.
4. **`pipelines/templates/deploy-stage.yml`** — the Environment-gated deploy stage, and the
   note contrasting Azure DevOps' WIF model with GitHub Actions' for anyone more familiar with
   the latter.
5. **`pipelines/deploy-workload.yml`** — how thin a squad's own pipeline ends up being once the
   template above exists, and where it points back at the earlier two experiments' output.

---

## Key Concepts Glossary

- **Workload identity federation (WIF)** — exchanging a short-lived OIDC token from a CI
  platform for an Azure AD access token, via a federated credential, with no client secret
  stored anywhere at any point.
- **Federated credential** — an Entra app registration's declaration of which external OIDC
  issuer + subject claim it trusts to exchange for a token, in place of a client secret.
- **Subject claim** — the specific string a federated credential matches against (here, an
  Azure DevOps service connection's identity: `sc://<org>/<project>/<service-connection>`).
  Get it wrong and the token exchange fails — no partial match.
- **Service connection (Azure DevOps)** — a named, project-scoped authentication configuration
  a pipeline references by name; the thing that's actually configured with "Workload Identity
  federation" as its auth method, in the portal.
- **Azure DevOps Environment** — a named deployment target with its own approval/check
  configuration, set in the portal rather than YAML, referenced from a `deployment:` job via
  `environment: <name>`.
- **`az deployment group what-if`** — previews the resource-level diff a deployment would make
  without applying it; run before every deploy in `deploy-stage.yml` so a reviewer (and an
  approver on the landing-zone Environment) sees intent, not just "the pipeline went green."

---

## Using These Files

Reference examples — no Azure DevOps project or Azure tenant in this sandbox. To try them:

1. Deploy `identity/ci-identity.bicep` twice, once per `identity/params/*.bicepparam` file —
   this needs Application Administrator (or Global Administrator) in Entra, since it creates
   app registrations via the Microsoft Graph Bicep extension.
2. Deploy `identity/ci-identity-rbac.bicep` twice — once against the sandbox subscription with
   the sandbox identity's `servicePrincipalObjectId` output, once against the landing-zone
   subscription with the landing-zone identity's.
3. In Azure DevOps: Project Settings → Service connections → New → Azure Resource Manager →
   Workload Identity federation, once per tier, named to match `pipelines/deploy-workload.yml`
   (`payments-sandbox`, `payments-landingZone`), each pointed at the matching app registration's
   client ID, tenant ID, and target subscription ID from step 1's outputs.
4. Pipelines → Environments → New Environment, once per tier (`sq-payments-sandbox`,
   `sq-payments-landing-zone`), adding an approval check on the landing-zone one only.
5. `pipelines/deploy-workload.yml`'s `workloadTemplate` variable points at
   `../azure-bicep-module-contracts-governance/workload/team-blob-store/main.bicep` — a real
   file elsewhere in this repo, not a placeholder; `params/sandbox.bicepparam` and
   `params/landing-zone.bicepparam` (referenced but not included here — see that experiment's
   own parameter files for the shape) would need to exist alongside it in a real pipeline run.
