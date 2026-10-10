# Azure App Configuration Feature Flag Registry

A central registry for feature flags, backed by Azure App Configuration's **classic** feature
flag model — one JSON file per flag, schema-validated in CI, applied per environment via
`az appconfig feature enable/disable`. Unlike most of the reference material elsewhere in this
repo, the validation, dry-run, and compile-time-check parts of this one are genuinely runnable,
with tests proving it — see [Running This Yourself](#running-this-yourself).

> This covers **Part 1 (the registry)**, **Part 2 (the registry pipeline)**, **Part 3
> (the app side + compile-time check)**, and **Part 4 (the gates)** of the original five-part
> plan. Part 5 (the full demo walkthrough) isn't built yet.

---

## Why Classic, Not Enhanced

Azure App Configuration has two feature flag models: the original ("classic") model — a flag
is on or off, optionally per label — and a newer **enhanced** model with richer targeting
(percentage rollout, user/group targeting, variants). Enhanced is still in preview as of this
writing. A registry meant to be relied on for environment promotion and CI gating shouldn't be
built on a preview surface — classic is the boring, stable choice, and it's enough for what
this registry actually needs: "is `payments-v2` on in staging, yes or no."

---

## The Flag Contract

Every flag is one file, `flags/<name>.json`, validated against `flags/schema.json`:

```json
{
  "name": "payments-v2",
  "description": "New payments processing flow replacing the legacy direct-integration path.",
  "owner": "payments-squad@contoso.com",
  "expiry": "2026-12-31",
  "environments": { "dev": true, "staging": true, "prod": false }
}
```

- **`name`** must be kebab-case and match the filename — the schema enforces the pattern,
  `scripts/validate.mjs` enforces the filename match (a schema alone can't see a filename).
- **`owner`** is a squad alias, not a person — same reasoning as the identity-first access
  model elsewhere in this repo: an owner that outlives whoever happened to add the flag.
- **`expiry`** is required on every flag, even one expected to live a long time. A flag with a
  past expiry gets a warning from the validator, not a hard failure yet — see
  [Known Gaps](#known-gaps).
- **`environments`** requires all three keys explicitly, even to set `false` — an omitted
  environment reads as "forgotten," not "off."

See `flags/schema.json` for the full JSON Schema, with field-by-field reasoning in its own
`description`s.

---

## The Pipeline

[`.github/workflows/feature-flag-registry.yml`](../../.github/workflows/feature-flag-registry.yml)
(at the repo root — GitHub only discovers workflows there, not inside this folder), scoped via
`paths:` to this experiment only:

| Job | Trigger | What it does | Needs Azure? |
|---|---|---|---|
| `validate` | Every PR + push to main | Schema validation + the validator's own test suite | No |
| `check-flag-references` | Every PR + push to main, after `validate` | Greps `app/` for flag references and fails on any not in the registry | No |
| `dry-run` | Every PR, one per environment | Prints the exact `az` commands a merge would run, no calls made | No |
| `apply-dev` / `apply-staging` | Push to main only, after `check-flag-references` | Actually runs `az appconfig feature set` + `enable`/`disable` | Yes — only runs if `vars.AZURE_CLIENT_ID` is set |
| `apply-prod` | Push to main only, after `apply-staging` | Same, but only after a human approves — see [The Gates](#the-gates) | Yes — only runs if `vars.AZURE_CLIENT_ID_PROD_DEPLOYER` is set |

The apply jobs use `azure/login@v2` with **workload identity federation (OIDC)** —
`client-id` / `tenant-id` / `subscription-id` as plain repo variables, no client secret stored
anywhere. In this sandbox repo none of those variables are set, so `apply-dev` (and everything
chained after it) simply doesn't run — validation and dry-run still do, and still prove the
registry is correct. See [Running This For Real](#running-this-for-real) for what setting those
variables actually involves.

`az appconfig feature set` alone — what the original plan named — only creates or updates a
flag's *description*; it doesn't toggle the on/off state. `scripts/apply.mjs` runs `feature set`
then `feature enable`/`feature disable` to actually match the JSON's declared state, and says so
in its own header comment.

---

## The App Side And The Compile-Time Check

[`app/src/index.mjs`](app/src/index.mjs) is a minimal, real app: it connects to Azure App
Configuration via `DefaultAzureCredential` (no connection string, same no-stored-secret
principle as the pipeline's OIDC) and the official
[`@microsoft/feature-management`](https://www.npmjs.com/package/@microsoft/feature-management)
library, then checks `payments-v2` with `featureManager.isEnabled('payments-v2')`. This is the
one part of the whole experiment that genuinely needs a live App Configuration store to run end
to end — everything else needs zero Azure access. Proven for real, not just written: with a
fake endpoint it reaches the actual Azure SDK and fails at the credential/network boundary, not
a code bug (see the commit history for the exact output).

[`scripts/check-flag-references.mjs`](scripts/check-flag-references.mjs) is the compile-time
check: it greps everything under `app/` for `.isEnabled("...")` calls, and fails the build if
any referenced flag has no matching file in the registry. Rename a flag in the registry without
updating the code (or vice versa), and this fails the build with the exact file and line —
before anything ships, not after a flag lookup silently returns the library's default at
runtime. It also warns (doesn't fail) on a registered flag nothing references — a candidate for
removal, same hygiene idea as the expiry warning.

This is a grep, not a real parser — it matches the literal-string-argument pattern the demo app
actually uses. A dynamically-built flag name (`isEnabled(someVariable)`) is invisible to it; see
[Known Gaps](#known-gaps).

```
$ node check-flag-references.mjs
  OK   payments-v2  (.../app/src/index.mjs:39)

Checked 1 reference(s) against 1 registered flag(s) — all referenced flags exist.
```

The pipeline runs this as its own job, `check-flag-references`, right after schema validation —
no Azure needed, no `npm ci` even, since it reads app source as plain text rather than running
it. The `apply-dev` job (and the `apply-staging` / `apply-prod` jobs chained after it) now also
depend on it passing, so a broken flag reference blocks a real deployment, not just a PR check.

---

## The Gates

The brief's RBAC model, as given: **Data Owner on dev and staging, Data Reader on prod.** Taken
completely literally, that's unworkable — if the identity that runs `apply-prod` only has Data
Reader, every `az appconfig feature enable/disable` call in that job returns `403 Forbidden`, no
matter how much human approval surrounds it. Read a different way, it's exactly right: **Data
Reader on prod describes the standing CI identity** — the one used for dev and staging, which
should never be able to touch prod at all, approved or not. Writing to prod needs a *second*,
narrower identity that doesn't exist until a human says so.

### Two identities, not one

| Identity | Repo variable | Role | Scope | Obtainable when? |
|---|---|---|---|---|
| Standing CI identity | `AZURE_CLIENT_ID` | **Data Owner** on dev, staging. **Data Reader** on prod. | Every environment | Any push to `main` — `apply-dev`/`apply-staging` use it freely |
| Prod deployer | `AZURE_CLIENT_ID_PROD_DEPLOYER` | **Data Owner** on prod only | prod only | Only inside a run of `apply-prod`, and only after the `prod` Environment's required reviewer approves |

The standing identity's Reader grant on prod isn't decorative — it's what the earlier `dry-run`
job would use if it ever needed to show a real diff against prod's current state (it doesn't
today; see [Known Gaps](#known-gaps)), and it's what makes "this identity cannot change prod"
a fact about its RBAC grant, not just a fact about which pipeline steps happen to call it.

### Why this can't be bypassed from inside the YAML

`apply-prod`'s federated credential (the OIDC trust relationship on the
`AZURE_CLIENT_ID_PROD_DEPLOYER` app registration) is scoped to GitHub's `environment` claim —
its subject is `repo:<org>/<repo>:environment:prod`. GitHub only includes that claim, and only
mints a token carrying it, for a job that references `environment: prod` *and* has already
cleared that environment's required reviewers. There is no code path — no YAML edit, no
rerunning a different job, no spoofed input — that produces a valid token for this identity
without GitHub itself having first gated the run on human approval. The approval isn't a
policy someone has to remember to enforce; it's the only way the credential exchange succeeds.

`apply-prod` in the workflow now carries `environment: prod` for exactly this reason — see its
comments for the details. Compare `apply-dev` / `apply-staging`, which still use the shared
`AZURE_CLIENT_ID` with no environment gate: fast, unapproved, because Data Owner on a lower
environment is cheap to get wrong and cheap to fix.

### Setting up the required reviewer

Creating a GitHub Environment with a protection rule is a repo-settings change, not something
done from a workflow file or scripted from this sandbox session — Settings → Environments → New
environment → name it exactly `prod` → **Required reviewers** → add yourself (or whoever should
approve prod changes) → Save. Once that exists, `apply-prod` will show as **Waiting** the moment
it would otherwise run, and stays there until an approver acts on it from the Actions tab — a
real pause, not a cosmetic one, the same mechanism GitHub uses for any protected deployment.

### What this actually demonstrates

With the Environment configured and `AZURE_CLIENT_ID_PROD_DEPLOYER` set (even to a value that
isn't yet a real Azure identity), merging a flag change to `main`:

1. `apply-dev` and `apply-staging` run immediately — no approval, matching their Data Owner
   grant on those environments.
2. `apply-prod` appears in the Actions run as **Waiting for review**, before anything in it has
   executed — no Azure call has been attempted, because no token exists yet.
3. Approving it lets the job proceed; declining or ignoring it means prod never changes. A
   portal edit was never offered as an alternative at any point — there is no UI path to set
   `AZURE_CLIENT_ID_PROD_DEPLOYER`'s token without this exact sequence.

---

## Running This Yourself

No Azure needed for either of these — that's the point.

```bash
cd scripts
npm install

# Validate every real flag in ../flags/ — same thing CI runs on every PR.
node validate.mjs

# Run the validator's and compile-time check's own test suites: 11 cases, including
# fixtures designed to fail (bad name pattern, missing field, filename/name mismatch,
# expired flag, a flag referenced in code but missing from the registry, ...).
npm test

# Check the real app's code against the real registry — no Azure needed, it's a grep.
node check-flag-references.mjs

# See exactly what a merge would do, for one environment, with zero Azure credentials.
APPCONFIG_STORE_DEV=appcs-contoso-dev node apply.mjs --environment dev --dry-run
```

Try breaking something: edit `flags/payments-v2.json` to set `"name": "Payments-V2"` (capital
letters) and re-run `node validate.mjs` — it fails with the exact schema rule it violated, not
a generic error. Or edit `app/src/index.mjs` to check `'payments-v3'` instead and re-run
`node check-flag-references.mjs` — same idea, a different layer of the pipeline catching it.

---

## Running This For Real

To actually apply flags, a real deployment needs:

1. Three Azure App Configuration stores (dev/staging/prod), and their names set as GitHub repo
   variables: `APPCONFIG_STORE_DEV`, `APPCONFIG_STORE_STAGING`, `APPCONFIG_STORE_PROD`.
2. **Two** Entra app registrations, both with federated credentials — no client secret on
   either — see [The Gates](#the-gates) for why it's two, not one:
   - The standing CI identity, subject `repo:<org>/<repo>:ref:refs/heads/main`. Repo variables
     `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID_DEV` / `_STAGING` / `_PROD`.
     Granted **Data Owner** on the dev and staging stores, **Data Reader** on prod.
   - The prod deployer, subject `repo:<org>/<repo>:environment:prod`. Repo variable
     `AZURE_CLIENT_ID_PROD_DEPLOYER` (reuses the same `AZURE_TENANT_ID` /
     `AZURE_SUBSCRIPTION_ID_PROD`). Granted **Data Owner** on the prod store only.
3. The `prod` GitHub Environment itself, with a required reviewer — see
   [Setting up the required reviewer](#setting-up-the-required-reviewer). Without this, the
   `environment:` subject claim above is never satisfied and the prod deployer's federated
   credential can never actually exchange for a token.

Once `AZURE_CLIENT_ID` is set, `apply-dev`/`apply-staging` stop no-op'ing. Once
`AZURE_CLIENT_ID_PROD_DEPLOYER` is also set *and* the `prod` Environment exists with a required
reviewer, `apply-prod` starts pausing for approval instead of staying skipped — no further YAML
change needed either way.

---

## Known Gaps

- **Expired flags warn, don't fail.** A hard failure on a past-expiry flag is the obvious next
  step, but it needs a decision on grace period and who's allowed to override it — left open
  rather than guessed at.
- **The compile-time check is a grep, not a parser.** It matches literal-string arguments to
  `.isEnabled(...)` — a flag name built at runtime (`isEnabled(someVariable)`,
  `` isEnabled(`${prefix}-v2`) ``) is invisible to it. Fine for a small demo app; a real
  codebase with many call sites would want an actual AST-based check eventually.
- **The unused-flag warning only scans `app/`.** A flag genuinely used by some other service
  entirely would show as a false "candidate for removal" — it's a hint for a human to check,
  not an automatic deletion signal.
- **The `prod` Environment isn't configured in this sandbox repo.** `apply-prod` carries
  `environment: prod` in the workflow, but creating the Environment and its required reviewer
  is a repo-settings change made in Settings → Environments, not something scriptable from this
  sandbox session — see [Setting up the required reviewer](#setting-up-the-required-reviewer).
  Until it exists, `environment: prod` is a no-op gate (GitHub auto-creates an unprotected
  Environment the first time a workflow references one that doesn't exist).
- **`dry-run` doesn't actually use the standing identity's Data Reader grant on prod.** It
  prints what *would* run without calling Azure at all, for any environment. A version that
  showed a real diff against prod's current state would need to authenticate with the standing
  identity — not built here, noted as a natural next step, not a gap in what's shipped.
- **One registry, one set of three environments.** Multiple domains/squads each needing their
  own store naming convention isn't modelled — out of scope for the core loop.
