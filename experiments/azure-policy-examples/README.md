# Azure Policy — Example Suite

A progressive, heavily-commented set of Azure Policy definitions (in Bicep,
so the reasoning can live in real comments) for learning the model from
first principles up to a realistic assign-and-remediate governance
baseline. Every file is self-contained and readable on its own — the
comments explain *why* each construct exists, not just what it does.

> These files are reference material, not a wired-up deployment. To
> actually try one, deploy it into a sandbox subscription — see [Using
> These Files](#using-these-files) below.

---

## Mental Model First

Everything in Azure Policy sits in one of four layers:

```
Policy Definition            "what to check, and what to do about it"
  policyRule: if / then (effect)

Initiative (Policy Set)      "a named bundle of definitions, assigned together"
  policyDefinitions[]        each entry references a definition by resourceId

Assignment                   "turns a definition/initiative ON, at a scope"
  scope: management group | subscription | resource group
  parameters, identity (for modify/deployIfNotExists), enforcementMode

Exemption / Remediation      "carve-outs and after-the-fact fixes for an assignment"
```

A definition on its own does nothing — it's inert until an **assignment**
attaches it to a **scope**. The same definition can be assigned many times,
at different scopes, with different parameter values each time. That's the
whole point of separating "what to check" (definition) from "where, and
with what settings" (assignment).

**Effects**, roughly ordered from least to most intrusive:

| Effect | What it does | Runs when |
|---|---|---|
| `Disabled` | Definition exists but is never evaluated | — |
| `Manual` | Compliance is set by a human/API attestation, not evaluation | — |
| `Audit` | Flags non-compliant resources; never blocks | On evaluation / resource write |
| `AuditIfNotExists` | Audits based on whether a *related* resource exists/matches | On evaluation / resource write |
| `Append` | Silently adds a field to the request if missing | On resource write, before it's saved |
| `Modify` | Adds/replaces/removes fields; can run via remediation on existing resources | On resource write, or on-demand via remediation |
| `Deny` | Blocks the create/update request | On resource write, before it's saved |
| `DeployIfNotExists` | Deploys an ARM template to fix a missing/misconfigured related resource | On resource write, or on-demand via remediation |

Prototype new rules as `Audit` first — it tells you what a stricter effect
*would* have caught, across your whole estate, with zero risk of breaking
anything. Only promote to `Deny`/`Modify`/`DeployIfNotExists` once you've
seen the audit results.

---

## The Examples

| # | File | Concepts Covered |
|---|---|---|
| 01 | [`policies/01-audit-effect-basics.bicep`](policies/01-audit-effect-basics.bicep) | Policy definition shape, `mode`, `policyRule` if/then, `exists`, building a field path from a parameter, the `audit` effect |
| 02 | [`policies/02-deny-effect.bicep`](policies/02-deny-effect.bicep) | The `deny` effect, `allOf`, resource provider aliases, finding aliases via `az provider show` |
| 03 | [`policies/03-append-effect.bicep`](policies/03-append-effect.bicep) | The `append` effect, why it differs from a template default value |
| 04 | [`policies/04-modify-effect.bicep`](policies/04-modify-effect.bicep) | The `modify` effect, `operations`, `roleDefinitionIds`, `conflictEffect`, `allowedValues` |
| 05 | [`policies/05-audit-if-not-exists.bicep`](policies/05-audit-if-not-exists.bicep) | `auditIfNotExists`, `details.type` for a related/child resource, `existenceCondition` |
| 06 | [`policies/06-deploy-if-not-exists.bicep`](policies/06-deploy-if-not-exists.bicep) | `deployIfNotExists`, nested ARM `deployment.properties.template`, `field()`, the two separate parameter scopes (policy vs. inner template) |
| 07 | [`policies/07-parameters-and-allowed-values.bicep`](policies/07-parameters-and-allowed-values.bicep) | Array parameters, `strongType`, the `in`/`not` operators, why definitions stay generic and assignments supply the specifics |
| 08 | [`policies/08-logical-operators-and-count.bicep`](policies/08-logical-operators-and-count.bicep) | `allOf`/`anyOf`/`not` nesting, the `count()` field function with `where`, `[*]` array field paths |
| 09 | [`policies/09-policy-initiative.bicep`](policies/09-policy-initiative.bicep) | `policySetDefinitions` (initiatives), `policyDefinitionReferenceId`, remapping initiative parameters onto member-policy parameters |
| 10 | [`policies/10-policy-assignment.bicep`](policies/10-policy-assignment.bicep) | `policyAssignments`, assigning to a resource-group scope from a subscription deployment, managed identity, granting the identity a role, `nonComplianceMessages`, `enforcementMode`, `notScopes` |
| 11 | [`policies/11-policy-exemption.bicep`](policies/11-policy-exemption.bicep) | `policyExemptions`, per-policy exemption within an initiative, `exemptionCategory`, `expiresOn` |
| 12 | [`policies/12-remediation-task.bicep`](policies/12-remediation-task.bicep) | `Microsoft.PolicyInsights/remediations`, why `modify`/`deployIfNotExists` don't retroactively fix pre-existing resources, `resourceDiscoveryMode` |

---

## Reading Order

1. **01 → 02**: the two simplest effects — flag vs. block — and how a
   condition (`if`) is built from `field`/`exists`/`equals`/`allOf`.
2. **03 → 04**: effects that *change* a request instead of just
   accepting/rejecting it — and the RBAC (`roleDefinitionIds`) that
   `modify` needs to actually write anything.
3. **05 → 06**: effects that reason about a *related* resource rather than
   the one being evaluated — read-only (`auditIfNotExists`) then
   self-healing (`deployIfNotExists`).
4. **07 → 08**: making a definition reusable (parameters) and expressive
   (logical operators, `count()` over arrays) — the two things that turn a
   one-off rule into something worth publishing for others to assign.
5. **09**: bundling several definitions into one assignable initiative.
6. **10**: the step that actually turns everything on — scope, identity,
   RBAC, enforcement mode.
7. **11 → 12**: living with an assignment over time — exempting a specific
   case, and retroactively fixing resources that predate it.

---

## Key Concepts Glossary

- **Policy definition** — the reusable "what to check, what to do" unit:
  `policyRule` (`if`/`then`), `parameters`, `mode`, `metadata`. Inert on
  its own; must be assigned to take effect.
- **`mode`** — `Indexed` evaluates only resource types that support tags
  and location (skip this for non-tag/location rules and you silently miss
  matches); `All` evaluates every resource type Policy can see, including
  ones without tags (role assignments, policy assignments themselves).
- **Alias** — a named path into a resource's properties that the Policy
  engine knows how to read for use in a `field` condition, beyond the
  generic `type`/`name`/`location`/`tags`. Aliases are per resource
  provider and API version; list them with `az provider show --namespace
  <ns> --expand "resourceTypes/aliases"` or the Portal's policy definition
  editor ("... Available Aliases").
- **Effect** — what happens when `if` matches: see the table above. Every
  built-in and custom definition should expose its effect as a parameter
  (`allowedValues: [Audit, Deny, Disabled]`) so an assignment can dial
  strictness up or down without editing the definition.
- **Initiative / policy set** — a named bundle of definitions
  (`policyDefinitions[]`), each given a `policyDefinitionReferenceId` that
  scopes it *within* the initiative. Assignments, exemptions, and
  remediations that target "one policy inside an initiative" use this id,
  not the underlying definition's own name.
- **Assignment** — attaches a definition or initiative to a scope
  (management group, subscription, or resource group) with concrete
  parameter values. Nothing is evaluated or enforced until this exists.
  Effective policy is the union of every assignment at or above a
  resource's scope — a resource group inherits everything assigned at its
  subscription and management groups too.
- **Managed identity on an assignment** — required by `modify` and
  `deployIfNotExists`, because those effects perform a real write. The
  identity needs the specific role(s) listed in the definition's
  `roleDefinitionIds`, granted separately (a normal `roleAssignment`
  resource) — Policy does not grant this automatically.
- **`enforcementMode`** — `Default` actively enforces `deny`/`append`/
  `modify`/`deployIfNotExists`; `DoNotEnforce` still evaluates and reports
  compliance but suspends those effects, useful for rolling out a new
  assignment without risking an outage while you watch its impact.
- **`notScopes`** — permanent, no-reason-required exclusions set on the
  assignment itself. Prefer an **exemption** instead when you want a
  reason, an expiry, or to exempt only some policies within an initiative
  rather than the whole assignment.
- **Exemption** — a separate resource that opts a specific scope out of a
  specific assignment (optionally: out of only specific policies within an
  initiative), with an `exemptionCategory` (`Waiver` vs `Mitigated`) and
  usually an `expiresOn` so it can't be forgotten.
- **Remediation** — an on-demand task that re-applies a `modify` or
  `deployIfNotExists` policy's fix to resources that were already
  non-compliant *before* the assignment existed or before the resource was
  last written. Without one, those effects only ever protect new/changed
  resources going forward.
- **Compliance state** — the result of evaluation (`Compliant` /
  `NonCompliant` / `Exempt` / `Unknown`), shown per resource, per
  assignment, in the Policy compliance dashboard. Evaluation happens on
  resource write and on a periodic scan cycle (roughly every 24h), not
  continuously — a manual "Trigger evaluation scan" exists for when you
  need current results sooner.

---

## Using These Files

These are reference examples, not wired into a live subscription from this
sandbox. To try one for real, in a subscription you're happy to
experiment in:

```bash
# 1. Deploy the definitions (01, 02, 04, 05, 06, 07, 08 — pick whichever you want to try)
az deployment sub create \
  --location uksouth \
  --template-file policies/01-audit-effect-basics.bicep

# 2. Once the definitions an initiative references exist, deploy the initiative
az deployment sub create \
  --location uksouth \
  --template-file policies/09-policy-initiative.bicep

# 3. Create the target resource group, then assign
az group create --name example-rg --location uksouth
az deployment sub create \
  --location uksouth \
  --template-file policies/10-policy-assignment.bicep \
  --parameters targetResourceGroupName=example-rg

# 4. Grab the assignment id from the deployment output, then exempt/remediate
az deployment sub show --name 10-policy-assignment --query properties.outputs.assignmentId.value -o tsv

az deployment sub create \
  --location uksouth \
  --template-file policies/12-remediation-task.bicep \
  --parameters targetResourceGroupName=example-rg policyAssignmentId=<paste-id>
```

Notes:

- `04`/`06`/`10` grant a managed identity a role right after creating it —
  Azure AD replication can lag a few seconds behind the identity's
  creation, so a role assignment deployed in the very same `az deployment
  sub create` occasionally needs a re-run if it 404s on the principal.
- `09` references `01`, `02`, `04`, and `07` by name via
  `subscriptionResourceId(...)` — deploy those four first, in the same
  subscription, or the initiative deployment will fail to resolve them.
- `11` and `12` need a real `policyAssignmentId` — paste the `assignmentId`
  output from step 3 above rather than guessing the resource ID.
- Compliance results aren't instant: after assigning, either wait for the
  periodic scan or trigger one on-demand with
  `az policy state trigger-scan --resource-group example-rg`.
- Clean up with `az policy assignment delete`, `az policy set-definition
  delete`, and `az policy definition delete` — in that order, since a
  definition or initiative still in use by an assignment can't be deleted.
