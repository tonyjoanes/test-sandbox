# Who approves what

Azure Repos has no CODEOWNERS file. The same effect comes from **branch policies on `main`**
(Project settings → Repos → this repo → Policies → Automatically included reviewers), each
scoped to a path filter. Suggested setup:

| Path filter | Required reviewers | Why |
|---|---|---|
| `/environments/dev.yaml` | none (build pipelines bump it with auto-complete PRs) | Dev follows main. |
| `/environments/uat.yaml` | `[Platform]\QA Leads` | Agree what's under test. |
| `/environments/staging.yaml` | `[Platform]\Release Managers` | |
| `/environments/live.yaml` | `[Platform]\Release Managers` + `[Platform]\CAB` | Plus the change request and the `live` environment's approval in the pipeline. |
| `/components/*` | `[Platform]\Cloud Engineers` + the component's owning team | Changes where things deploy to. |
| `/policy.yaml`, `/hooks/*` | `[Platform]\Cloud Engineers` | Changes the rules and runs code during releases. |

Add a **build validation** policy on `main` using the release validation pipeline
(`pipelines/release-validation.yml`), so no PR can merge with a broken manifest.
