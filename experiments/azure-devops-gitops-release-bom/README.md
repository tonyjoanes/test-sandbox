# GitOps-Style Releases on Azure DevOps: a Release BOM and `relctl`

Releases today are a spreadsheet: people add the release pipelines they need to an Excel file,
and cloud engineers run them, one by one, from release branches. This experiment replaces that
with the approach Argo CD takes for Kubernetes, adapted to Azure DevOps, App Service, Function
Apps and static React front ends:

- **Build and deploy are separate.** A build produces an immutable, versioned package, and
  that's all it does.
- **Each environment has a manifest in git.** `environments/live.yaml` lists every component
  at its exact version. It is the environment's **bill of materials (BOM)**.
- **A release is a pull request** that changes a manifest. The PR is linted, reviewed and
  approved. Merging it deploys exactly the difference, and nothing else.
- **`relctl` is the small tool that makes this work on Azure DevOps.** It lints the repo,
  compares the manifest with what's deployed, plans the order, deploys it wave by wave, and
  writes the change request and the BOM.

Everything here runs locally: the tool, its 79 tests, an end-to-end demo, and an expansion of
the Azure DevOps pipelines. What can't be tested without Azure DevOps and Azure is listed in
[Known Gaps](#known-gaps).

> **How this relates to [`azure-devops-versioned-release-manifest`](../azure-devops-versioned-release-manifest):**
> that experiment makes the manifest *itself* a pipeline file, with one manifest per release
> and APIs only. This one keeps a desired-state manifest **per environment**, plus a tool that
> works out what to change. It covers Function Apps and React front ends too, and adds drift
> detection, hooks, change requests and a BOM. It keeps that experiment's best idea:
> a component's dependency rules ship *inside its package*, so a release is checked against
> the rules of the exact versions being released.

---

## The Shape of It

```
  App repos (many)                Azure Artifacts            Release repo (one)
  ────────────────                ───────────────            ──────────────────
  merge to main                   orders-api 1.8.0  ◄──┐     components/      how to deploy each thing
    │                             orders-api 1.8.2     │     environments/    what runs where: the BOM
    ▼                             portal-web 5.1.0     │       dev.yaml       ◄── bumped by builds
  build + test ──► package ──────►  app.zip / site.zip │       uat.yaml       ◄── PR: promote dev → uat
                    + release-metadata.yaml            │       staging.yaml   ◄── PR: promote uat → staging
    │                                                  │       live.yaml      ◄── PR: promote staging → live
    └──► PR: bump dev.yaml ────────────────────────────┼──►  policy.yaml      promotion chain, freezes
                                                       │     hooks/           pre/post-sync scripts
                                                       │
           merge to main ──► sync-<env> pipeline ──────┘
                             plan ─► change request ─► sync (pre-sync, wave 1, pause, wave 2, …, post-sync) ─► record
                                                        └── reads/writes relctl.<component>.version tags on each Azure resource
```

| Folder | What it is |
|---|---|
| `src/Relctl/` | **relctl**, a .NET 10 tool (`dotnet tool install Relctl`). |
| `tests/Relctl.Tests/` | 79 MSTest tests (Microsoft.Testing.Platform), against a fake Azure. |
| `release-repo/` | An example release repo: six components, four environments, a policy, hooks. In real life this is its own Azure Repos repo. |
| `sample-feed/` | Stands in for the Azure Artifacts feed: `<package>/<version>/release-metadata.yaml`. |
| `pipelines/` | The Azure DevOps pipelines: build template, PR validation, a sync per environment, nightly drift. |
| `demo/run-demo.sh` | A whole release on your machine, from promotion to rollback. |
| `scripts/test-pipeline-expansion.cs` | Expands the pipelines with an emulator and checks the job graph. |

---

## 1. Build: Packages, Not Deployments

An app repo's pipeline extends [`build-component.yml`](pipelines/templates/build-component.yml):

```yaml
name: 1.8.$(Rev:r)        # the build number IS the version
extends:
  template: pipelines/templates/build-component.yml@release
  parameters: { component: orders-api, type: app-service, project: src/Orders.Api/Orders.Api.csproj }
```

On `main` it tests, then publishes a Universal Package `orders-api 1.8.2` containing:

- `app.zip`, or `site.zip` for a static site: the build output, identical for every environment
- `release-metadata.yaml`: the commit and run it came from, and **what this version needs**:

```yaml
component: orders-api
version: 1.8.0
build: { commit: 4ff6a59…, run: "4527", branch: main }
requires:
  - component: payments-api
    minVersion: 2.3.0
    reason: Uses the payment-intents endpoint added in payments-api 2.3.0.
```

The `requires` list comes from a `release-requires.yaml` in the app repo, so the team that writes
the code that needs the dependency also states the rule. Finally, the build opens an
auto-complete PR moving `dev.yaml` to the new version (`relctl bump`). The dev sync deploys it.
Builds never deploy anywhere else.

**React needs one change to work this way:** no environment URLs baked in at build time (no
`VITE_*`/`REACT_APP_*` API URLs). At deploy time relctl writes `/config.json`
from the catalog, and the app fetches it at startup. That's what lets one `site.zip` go to
every environment.

---

## 2. The Release Repo

**The catalog** ([`components/*.yaml`](release-repo/components)) says *how* and *where*. It
rarely changes:

```yaml
name: customers-api
type: app-service                 # app-service | function-app | static-site
owner: '[Platform]\Customers Team'
versionPath: /version             # relctl checks this before and after going live
hooks:
  preSync:  [ { name: migrate-database, script: hooks/run-migration-bundle.sh } ]
  postSync: [ { name: ready, http: /health/ready } ]
targets:
  live: { subscription: <guid>, resourceGroup: rg-customers-prd, app: app-customers-prd, slot: staging }
  # dev, uat, staging…
```

**Environment manifests** ([`environments/*.yaml`](release-repo/environments)) say *what*.
They change with every release:

```yaml
environment: live
release: 2026.11.3

components:
  customers-api: 1.2.0
  orders-api: 1.8.0
  orders-func: 1.4.0
  payments-api: 2.3.1
  portal-web: 5.1.0
  notifications-func: { version: 3.0.9, rollback: "INC0012345: duplicate emails in 3.1.0" }

pauses:
  - before: payments-api
    release: 2026.11.3            # a pause belongs to one release
    instructions: |
      Run the "customers-billing-backfill" WebJob and wait for it to finish…

hooks:
  preSync:  [ { name: announce, script: hooks/announce-release.sh } ]
  postSync: [ { name: storefront-up, http: https://www.contoso.example/health } ]
```

- **Every component, every time.** The file is the whole environment, not just the changes. That's
  what makes it a BOM, and what makes `git show live/2026.11.3:environments/live.yaml` answer
  "what was live after that release?"
- **The diff is the release.** A single-API change is a one-line PR. A big release is a
  bigger one. It's the same process, so there's no separate "hotfix process" to get wrong.
- **`relctl promote --from staging --to live`** writes the PR for you. It copies exactly what's
  upstream and carries the release name across, and it edits the file as text, so comments survive.

**The policy** ([`policy.yaml`](release-repo/policy.yaml)) sets the promotion chain
(dev → uat → staging → live), which environments need a change request, freeze windows, and the
feed.

**Approvals** are Azure Repos branch policies with path filters: required reviewers on
`environments/live.yaml`, owning teams on `components/*`. See
[`CODEOWNERS.md`](release-repo/CODEOWNERS.md).

---

## 3. The Lint: What a PR Can't Get Past

`relctl validate --base origin/main` runs as the build validation policy on every PR. All
messages name the file and line. Example messages are taken from the tests:

| Rule | Example |
|---|---|
| **No typos.** Unknown keys are errors. | ``unknown key `versionpath` in a component. Allowed: hooks, name, owner, package, targets, type, versionPath.`` |
| **Exact versions** only: no ranges, `latest` or pre-release tags. | `"customers-api" version "^1.2.0" isn't an exact x.y.z version.` |
| **It exists**: the version is in the feed. | `orders-api 1.8.3 doesn't exist in the Azure Artifacts feed releases.` |
| **It has somewhere to go**: in the catalog, with a target for this environment. | `"orders-func" has no `live` target in components/orders-func.yaml` |
| **Its dependencies hold**, using the rules in *that version's* package. | `payments-api 2.3.1 needs customers-api >= 1.2.0, but uat pins customers-api 1.1.4. Why: Calls GET /customers/{id}/billing-profile…` |
| **Promotion order**: it must be in the previous environment first. | `orders-api 1.8.2 must be in staging before it can go to live, but staging has orders-api 1.8.0. Use relctl promote…` |
| **No silent downgrades.** Going backwards needs a `rollback:` reason. | `"notifications-func" goes backwards from 3.2.0 to 3.1.0. If that's a rollback, say so: …` |
| **Rollbacks skip promotion order** (live can't wait for UAT), but must go backwards. | `is marked as a rollback, but 3.2.1 isn't older than the current 3.2.0.` |
| **A new release name** when versions change. | `versions changed but `release` is still 2026.11.2.` |
| **Freezes.** | `live is frozen from 2026-12-18 to 2027-01-04 (Christmas trading freeze).` |
| **No stale one-offs.** A pause or hook for an old release fails, so it can't stop the next one. | `this pause is for release 2026.11.3, but this is release 2026.11.4. Delete it…` |
| **Hook scripts exist**, there are **no dependency cycles**, and file names match contents. | |

The same PR run also posts a **plan preview** for every environment: what merging would change,
in what order, with which pauses.

---

## 4. Planning: Waves, Pauses and Hooks

`relctl plan` compares the manifest (desired) with what's deployed (actual) and only plans the
difference. It then orders that difference into **waves**, using the `requires` rules. The
example release, staging promoted to live:

```
| Wave | Component     | From  | To    |
| 1    | customers-api | 1.1.4 | 1.2.0 |
| 2    | payments-api  | 2.2.7 | 2.3.1 |   ⏸ pause before: billing backfill
| 3    | orders-api    | 1.7.3 | 1.8.0 |
| 4    | orders-func   | 1.3.0 | 1.4.0 |
| 4    | portal-web    | 5.0.2 | 5.1.0 |
```

The rule is that **between any two waves, everything running still has what it needs.** So two
changing components are only ordered when the other order would break something:

- **The new version needs a newer dependency:** the dependency goes first (the usual case).
- **The *current* version needs something that's going backwards:** the current version must
  go first. This is the rollback case, and the demo found this bug in the first version of the
  planner. Rolling `orders-api` back from 1.8.0 to 1.7.3 must first move `orders-func` and
  `portal-web` off the versions that need 1.8.0. So a rollback runs in reverse order, automatically.
- Anything **not changing** is already in place, so it holds nothing up.

**Pauses** (Argo CD has none; we wanted them) hold the wave of the component they come before.
They're Manual Validation tasks with the instructions from the manifest.

**Hooks** use Argo CD's names:
- **Release-level hooks** (in the environment manifest) run once, before the first wave and after the last.
- **Component hooks** (in the catalog) run around each deploy. Pre-sync runs before anything
  changes, for example the EF Core migration bundle that ships in the package. Post-sync runs
  after the swap, for example a readiness check.
- A hook is a script from `hooks/`, or an `http` check.

---

## 5. Deploying

| Type | How | If post-deploy checks fail |
|---|---|---|
| `app-service` (C# APIs) | Zip deploy to the `staging` slot → wait until the slot reports the new version on `versionPath` → swap → check production → post-sync hooks | **Swaps back automatically.** The old build is still warm in the slot. |
| `function-app` | The same, with `az functionapp deployment source config-zip` and a slot swap | The same. Without a slot (like dev here), it says how to roll back. |
| `static-site` (React) | Unpack `site.zip`, write `config.json` (this environment's settings + version), upload to the storage account's `$web` | No slot, so the error says how to roll back via the manifest. |

After each component, relctl tags its Azure resource `relctl.<component>.version=1.8.0`
(and `.release`). **Those tags are the "actual" state.** There's no database to run, they
survive anything, and anyone can see them in the portal. Before deploying, relctl re-reads the
tag. If the component is already at the target version it skips it, so re-running a failed wave
is safe. If something else changed it since the plan, it stops rather than guessing.

All Azure calls go through `az`, using the pipeline's `AzureCLI@2` login. That means `--dry-run`
can print every command, and the tests can swap in a fake `az`.

---

## 6. The Pipelines

**Sync, one pipeline per environment** ([`sync-live.yml`](pipelines/sync-live.yml), which
extends [`sync-environment.yml`](pipelines/templates/sync-environment.yml)). It's triggered when
that environment's manifest changes on `main`:

```
plan ──► change_request ──► sync ─────────────────────────────────────────────────────► record
         (live)              pre_sync ► pause_1 ► wave_1 ► pause_2 ► wave_2 ► … ► post_sync   tag live/2026.11.3,
                             └─ one stage: the environment's approval, lock and CR check     publish the BOM
                                run ONCE, not once per wave
```

Azure DevOps builds the job graph when a run is **queued**, before anything can read the
manifest. So the template always has five wave jobs, each with a pause job in front. The plan
step sets output variables (`wave3=true`, `pause2=true`, …) that decide which ones run. The
rest show as skipped. Other pipelines:

- [`release-validation.yml`](pipelines/release-validation.yml): PR build validation (lint plus a plan preview).
- [`build-component.yml`](pipelines/templates/build-component.yml): the build template for app repos.
- [`drift-nightly.yml`](pipelines/drift-nightly.yml): `relctl diff --fail-on-drift` across every environment.
- [`relctl-tool.yml`](pipelines/relctl-tool.yml): builds and publishes relctl itself.

**Change requests:** the plan becomes a ServiceNow `change_request` body
(`relctl change-request`). It covers what's changing, the implementation plan by wave and pause,
a backout plan with the versions to return to, the test plan from the hooks, the CIs and the
owners. The pipeline posts it, and the `live` environment's check waits for it to be approved.

**The BOM:** after a successful sync, `relctl bom` writes the environment as a
**CycloneDX 1.6** document. It lists every component at its exact version, the commit and build
it came from, and its dependencies. It's published with the run, and the commit is tagged
`live/<release>`.

---

## 7. Day to Day

| You want to… | Do this |
|---|---|
| Get a change into dev | Merge to main in the app repo. The build bumps `dev.yaml`, and dev syncs. |
| Release to UAT, staging or live | `relctl promote --from uat --to staging`, then commit, push and open a PR. Reviewers see the diff and the plan. Merge. |
| Ship one API on its own | `relctl promote --from staging --to live --only orders-api --release 2026.11.3-hf1`. It's a one-line PR. |
| Roll back | Set the old version with a reason: `orders-api: { version: 1.7.3, rollback: "INC…" }`. Validation tells you what else must roll back with it, and the planner orders it. |
| Hold a release for a manual step | Add a `pauses:` entry for this release. |
| Know what's in live | Read `environments/live.yaml`, check the `live/*` tags, or open the BOM artifact of the last sync. |
| Check nothing was deployed by hand | The nightly drift run, or `relctl diff`. |

---

## Testing

Needs only the [.NET 10 SDK](https://dotnet.microsoft.com/download) and git.

```bash
cd experiments/azure-devops-gitops-release-bom

dotnet test                                    # 79 tests, ~2s
./demo/run-demo.sh                             # a full release, end to end, ~6s
dotnet run --project src/Relctl -- validate --repo release-repo --local-feed sample-feed
cd scripts && dotnet run test-pipeline-expansion.cs   # ~1 min the first time (installs Runner.Server)
```

| What | What's real | What's faked | What it proves |
|---|---|---|---|
| `LoaderTests`, `ConsistencyRuleTests`, `ChangeRuleTests` | The loader and validator, git (for `--base`) | Nothing: the example repo, broken one rule at a time | Every lint rule in section 3 fires with a useful message, and a clean repo passes. |
| `PlannerTests` | The planner | Deployed state (a JSON file) | Waves, the rollback reverse order, pauses, max waves, freezes at deploy time, pipeline variables. |
| `ApplierTests` | The applier, hook scripts (real bash) | `az` and HTTP, through a fake Azure that tracks what each slot runs and swaps it | Slot → check → swap → tag; **swap-back** when post-sync fails; nothing swapped if the slot never comes up; re-runs skip; drift since plan stops it; `config.json` per environment; no-slot deploys; hook environment variables; a dry run changes nothing. |
| `ManifestEditorTests`, `ReportTests`, `CliTests` | Everything | `az` (in one CLI test) | Promote/bump keep comments; the BOM is valid CycloneDX; the change request has its plans; exit codes. |
| `demo/run-demo.sh` | The relctl binary, git | Azure (dry run and a state file) | The whole flow, including a mistake caught at PR time and a dependency-aware rollback. |
| `test-pipeline-expansion.cs` | The pipeline templates, expanded by [Runner.Server](https://github.com/ChristopherHX/runner.server) | Azure DevOps itself | The stages and jobs, their `dependsOn` and conditions, one `relctl apply` part per job, and that a typo in a parameter fails. |

As a check on the tests themselves: removing the rollback-ordering rule from the planner fails
exactly one test, and so does removing the swap-back. Both are named after the behaviour.

[`.github/workflows/gitops-release-bom.yml`](../../.github/workflows/gitops-release-bom.yml) runs
all of it on every change.

---

## Setting It Up in Azure DevOps

1. **Repos**: create a `release` repo from `release-repo/`, plus this experiment's `pipelines/`, `src/` and `tests/`.
2. **Feeds**:
   - `releases` (Universal Packages), for app packages. Give the build service *Contributor*.
     Set retention so packages referenced by a tag are never deleted.
   - `tools` (NuGet), for relctl. Register [`relctl-tool.yml`](pipelines/relctl-tool.yml) to publish it.
3. **Service connections**: workload identity federation.
   - `sc-release-nonprod` and `sc-release-prod`: on their resource groups, *Reader*, *Website
     Contributor* (deploy and swap), *Storage Blob Data Contributor* (upload static sites) and
     *Tag Contributor* (the version tags).
   - `sc-release-reader`: *Reader* everywhere, for the drift check.
4. **Environments** `dev`, `uat`, `staging` and `live`, with checks on each:
   - **Exclusive lock** on all four.
   - **Approvals** on `staging` and `live`.
   - **Business hours** on `live`, if you want it.
   - **Invoke REST API** on `live`: it polls ServiceNow for an approved change request whose
     `u_release_commit` is the run's commit. Adapt this to your instance, or use the ServiceNow
     Change Management extension's own check instead.
   These checks live on the environment, not in the YAML, so nobody can remove a gate by editing a file.
5. **Pipelines**: register `sync-dev/uat/staging/live.yml`, `release-validation.yml` and
   `drift-nightly.yml`.
6. **Branch policies on `main`**:
   - Build validation with `release-validation.yml`.
   - Path-filtered required reviewers (see [`CODEOWNERS.md`](release-repo/CODEOWNERS.md)).
   - The build service may *Contribute* and *Create tag*, so builds can open the dev bump PRs
     and the record stage can push `live/*` tags.
7. **Variable group** `servicenow`: `SERVICENOW_URL`, `SERVICENOW_USER` and `SERVICENOW_PASSWORD` (secret).
8. **App repos**: extend `build-component.yml`, expose the version on `versionPath`, and move
   React environment settings into `/config.json`.

**Rolling it out without a big bang:** start with the release repo and `validate` alone, with
manifests that mirror what's deployed today. Add the version tags by hand once (or with `relctl`
from the current release). Then move one environment at a time onto the sync pipeline, dev first.

---

## Known Gaps

- **Not run against a real Azure DevOps org or Azure subscription.** The pipeline expansion is
  checked with an emulator. The `az` commands match their documented syntax and are exercised
  against a fake. Task inputs, the Invoke REST API check, and stage-to-job output variables
  across stages have not been run for real. The
  [Pipelines Preview API approach](../azure-pipeline-template-validation) is the next step.
- **ServiceNow** field names are the standard `change_request` table's. `u_release_commit` and
  `u_owners` are custom fields to create or map.
- **Waves run their components one after another**, for readable logs. Running them in parallel
  is a small change in `Applier.ApplyWave`.
- **Infrastructure isn't in the manifest.** Bicep and app settings still deploy separately.
  A natural next step is a `bicep` component type, a versioned template package, applied in wave 0.
- **Removing a component doesn't undeploy it.** relctl warns and leaves it running. Decommissioning
  stays a deliberate, separate act.
- **The demo's "after a real run" step** writes the state file by hand, because a dry run records
  nothing.
