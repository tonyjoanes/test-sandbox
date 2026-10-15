# Issue Plan: Squad Workload Platform PoC

Six parts, sized for about 2–3 weeks part-time. Each part is one PR and is demoable on its own,
matching how the feature flag registry was built. Parts 1–2 need no Azure at all. Spend only
starts at Part 3.

Design reasoning lives in [ADR-0001](adr-0001-manifest-driven-squad-workloads.md). This file is
only the what and the order.

---

## Part 0: Subscription and cost guard (about half a day)

- [ ] Personal Azure subscription. Budget at £/$100 with alerts at 50/80/100% to a personal email
- [ ] Entra app registrations: `platform-deployer` and `squad-orders-deployer`, both with GitHub
      OIDC federated credentials and no secrets
- [ ] `teardown` workflow (manual dispatch) that deletes every `rg-squad-*` resource group
- **Done when:** a budget alert test email arrives, and `teardown` runs green against an empty
  subscription

## Part 1: The contract (no Azure, about 2 days)

- [ ] `schema/workload.schema.json` with `apiVersion: platform/v1`
- [ ] `scripts/validate.mjs` plus tests: valid manifest, unknown capability, bad squad name,
      `expose.path` collision across squads, `squad` not matching the folder name
- [ ] `squads/orders/workload.yaml` (the example from the plan)
- [ ] `CODEOWNERS.example` showing `squads/orders/` owned by the orders squad only
- [ ] Workflow: validate on every PR, scoped with `paths:`
- **Done when:** a bad manifest fails the PR with a readable message, and the tests run in CI

## Part 2: Modules (Bicep build and lint only, about 3 days)

- [ ] `modules/key-vault`, `app-config`, `storage-container`, `servicebus-topic`,
      `function-app` (Flex Consumption), `apim-frontdoor-route`
- [ ] Each one has a private endpoint, a diagnostic setting and RBAC built in, with **no** toggle
      to turn them off
- [ ] `modules/*/README.md` with inputs and outputs (one table each, no prose)
- [ ] `bicep build` and `bicep lint` in CI. Release workflow tags `<module>/vX.Y.Z` and publishes
      to ACR
- **Done when:** all modules build in CI, and a `v0.1.0` tag publishes (publishing needs the
  Part 3 ACR, so the tag can come after Part 3)

## Part 3: Backbone and policy (Azure starts here, about 3 days)

- [ ] `backbone/main.bicep`: Log Analytics, VNet and private DNS zones, Service Bus Standard,
      ACR Basic, APIM Developer (external VNet), Front Door Standard
- [ ] `policy/initiative.bicep`: deny public access (per-service built-ins), DINE diagnostics,
      Deny RG without `squad` tag plus Modify to inherit it on resources
- [ ] Exemptions in code for APIM, Front Door and the Service Bus namespace, each with an
      expiry and a justification
- [ ] Custom role `Squad Workload Contributor`
- **Done when:** the backbone deploys, the initiative shows compliant, and a manual
  `az storage account create --public-network-access Enabled` is denied

## Part 4: Platform pipeline (about 3 days)

- [ ] `platform/squad.bicep` reads parameters generated from the manifest and composes the
      modules. RG is `rg-squad-<name>`, tagged `squad=<name>`
- [ ] Function MI role assignments are scoped to the narrowest resource (container, topic, vault)
- [ ] Squad deployer gets `Squad Workload Contributor` on the RG only
- [ ] PR: validate, then `what-if`, posted as a PR comment. Merge to `main`: deploy using the
      platform identity
- [ ] Auto-merge when validation is green and CODEOWNERS approval is present. No platform
      human needed for `squads/**`
- **Done when:** merging `squads/orders/workload.yaml` produces a wired-up `rg-squad-orders` and
  a Front Door route that returns 404 because there's no code yet

## Part 5: Squad repo and the demo (about 2–3 days)

- [ ] `squad-template/`: a Function App (one HTTP trigger that reads one Key Vault secret and one
      App Config key, then publishes to `order-placed`), plus a deploy workflow using the squad
      identity
- [ ] `squad-template/infra/`: the escape hatch, a Bicep file the squad deploys with its own
      identity
- [ ] Demo 1: empty repo → manifest PR → merge → push code → `curl https://<fd-endpoint>/orders`
      returns 200
- [ ] Demo 2: squad adds a public storage account in `infra/` → deploy fails with
      `RequestDisallowedByPolicy`, **not** `AuthorizationFailed`
- [ ] Demo 3 (to show it isn't a ticket queue): the squad adds `topics: [order-cancelled]` by PR
      and it's live with no platform involvement
- [ ] `README.md` walkthrough with real output pasted in
- **Done when:** all three demos run from a clean subscription in under an hour, APIM aside

---

## Deliberately Out of Scope

- Multiple environments (dev/staging/prod). Everything is one environment
- Subscription-per-squad. See `azure-landing-zone-squad-vending`
- Service Bus Premium and Front Door Premium. Covered in the ADR, too expensive for a PoC
- Manifest in the squad repo (ADR option A)
