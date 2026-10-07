# Azure App Configuration Feature Flag Registry

A central registry for feature flags, backed by Azure App Configuration's **classic** feature
flag model — one JSON file per flag, schema-validated in CI, applied per environment via
`az appconfig feature enable/disable`. Unlike most of the reference material elsewhere in this
repo, the validation and dry-run parts of this one are genuinely runnable, with tests proving
it — see [Running This Yourself](#running-this-yourself).

> This covers **Part 1 (the registry)** and **Part 2 (the registry pipeline)** of the original
> five-part plan — the core loop. Parts 3–5 (app-side compile check, RBAC gates, full demo
> walkthrough) aren't built yet.

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
| `dry-run` | Every PR, one per environment | Prints the exact `az` commands a merge would run, no calls made | No |
| `apply-dev` / `apply-staging` / `apply-prod` | Push to main only | Actually runs `az appconfig feature set` + `enable`/`disable` per environment, in order | Yes — and only runs at all if `vars.AZURE_CLIENT_ID` is set |

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

## Running This Yourself

No Azure needed for either of these — that's the point.

```bash
cd scripts
npm install

# Validate every real flag in ../flags/ — same thing CI runs on every PR.
node validate.mjs

# Run the validator's own test suite: 8 cases, including fixtures designed to fail
# (bad name pattern, missing field, filename/name mismatch, expired flag, ...).
npm test

# See exactly what a merge would do, for one environment, with zero Azure credentials.
APPCONFIG_STORE_DEV=appcs-contoso-dev node apply.mjs --environment dev --dry-run
```

Try breaking something: edit `flags/payments-v2.json` to set `"name": "Payments-V2"` (capital
letters) and re-run `node validate.mjs` — it fails with the exact schema rule it violated, not
a generic error.

---

## Running This For Real

To actually apply flags, a real deployment needs:

1. Three Azure App Configuration stores (dev/staging/prod), and their names set as GitHub repo
   variables: `APPCONFIG_STORE_DEV`, `APPCONFIG_STORE_STAGING`, `APPCONFIG_STORE_PROD`.
2. An Entra app registration with a federated credential trusting this repo's GitHub Actions
   OIDC issuer (subject: `repo:<org>/<repo>:ref:refs/heads/main` for the push-triggered apply
   jobs) — no client secret. Its client ID, tenant ID, and (per-environment) subscription ID
   set as repo variables: `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`,
   `AZURE_SUBSCRIPTION_ID_DEV` / `_STAGING` / `_PROD`.
3. That app registration granted **App Configuration Data Owner** on the dev and staging
   stores, **App Configuration Data Reader** on prod — see Part 4 (not yet built) for the
   reasoning and the human-approval gate that goes with the prod/Reader split.

Once `AZURE_CLIENT_ID` is set, the `apply-*` jobs stop no-op'ing and start actually running —
no other YAML change needed.

---

## Known Gaps

- **Expired flags warn, don't fail.** A hard failure on a past-expiry flag is the obvious next
  step, but it needs a decision on grace period and who's allowed to override it — left open
  rather than guessed at.
- **No stale-flag detection.** Nothing here checks whether a flag is still referenced in code —
  that's Part 3 (the compile-time check), not built yet.
- **No approval gate on prod.** `apply-prod`'s comment marks exactly where `environment: prod`
  attaches once that GitHub Environment and its required reviewer exist — that's Part 4.
- **One registry, one set of three environments.** Multiple domains/squads each needing their
  own store naming convention isn't modelled — out of scope for the core loop.
