# Pipeline Template Validation — Catching Broken References Before They Run

A harness pipeline, and a PR gate that expands it via Azure DevOps' Pipelines Preview API,
catching template resolution errors — like an unnecessary `@repo` alias on a template that was
already in the same repo — before they ever reach a real pipeline run.

> Like the rest of this repo, this is reference material — no Azure DevOps project in this
> sandbox. See [Using These Files](#using-these-files) to wire it up for real.

> **Where this fits:** `azure-squad-cicd-identity/pipelines/templates/deploy-stage.yml` is the
> real template this experiment validates. That template's own comments already note the
> "get the service connection name wrong and it fails at runtime, not authoring time" risk —
> this experiment is the fix for exactly that class of problem, at the point a template
> changes rather than the point it's next used.

---

## The Bug This Answers

`template: some/path.yml@reponame` only needs the `@reponame` part when the template lives in
a **different** repository than the pipeline calling it — and even then, `reponame` has to be
declared as a repository resource. When a template lives in the **same** repo, no alias is
needed at all; `template: some/path.yml` is enough. Add an alias anyway — copied from habit, or
from a different pipeline that genuinely does reference an external template repo — and one of
two things happens: Azure DevOps can't resolve the alias at all and the pipeline fails outright,
or (worse) the alias happens to match a repository resource that IS declared, just not the one
the author meant, and the pipeline silently pulls the template from the wrong place.

Either way, **this is a compile-time error** — YAML pipelines are fully expanded (every
`template:`, every `${{ }}` expression, every repository resource) before a single job runs.
The bug was always going to be there the moment the file was saved; the only question was
whether something caught it before a real pipeline run did.

---

## The Mechanism

Azure DevOps' **Pipelines Preview API** (`POST .../pipelines/{id}/preview`) performs exactly
that compile-time expansion — resolving every template reference, repository resource, and
expression — and returns either the fully resolved YAML or the precise resolution error.
Critically, **it never queues a job**. Nothing runs, no agent picks up work, no deployment step
executes, regardless of what the pipeline actually contains. That's what makes it safe to call
on every PR: worst case, it costs an API call.

Two things had to exist for the Preview API to be useful here:

1. **Something to preview.** The API previews an already-registered Pipeline object, not
   arbitrary YAML — so `harness/validate-templates.yml` is a real, once-registered pipeline
   whose entire job is calling the templates under test. It's never queued as a live run.
2. **Representative calls, not synthetic ones.** The harness's two stage calls are copies of
   the real invocations in `azure-squad-cicd-identity/pipelines/deploy-workload.yml` — the
   sandbox and landing-zone tier calls a real squad pipeline actually makes. This isn't
   exhaustive permutation testing (see `../azure-squad-cicd-identity` discussion this grew out
   of) — it's testing the shapes that are actually used, which is what catches a real breakage
   without needing to enumerate every consumer across the org.

---

## Worked Example: The Actual Bug

Say `deploy-stage.yml` and the harness both live in the same repo — no alias needed:

```yaml
# harness/validate-templates.yml — correct
stages:
  - template: ../../azure-squad-cicd-identity/pipelines/templates/deploy-stage.yml
    parameters:
      tier: sandbox
      # ...
```

Someone "fixes" what looks like a missing reference — maybe copying a pattern from a pipeline
that really does pull from an external template repo:

```yaml
# harness/validate-templates.yml — broken
stages:
  - template: ../../azure-squad-cicd-identity/pipelines/templates/deploy-stage.yml@platform-templates
    parameters:
      tier: sandbox
      # ...
```

`platform-templates` was never declared in `resources.repositories` for this pipeline. A live
run fails with something like `Reference to a non-existing repository "platform-templates"` —
correct, but discovered by whoever's pipeline happened to run next, potentially hours after the
change merged. `gate/validate-template-pr.yml` calling the Preview API on the PR that
introduced this returns the same error, attached to the PR, before merge:

```
Preview API returned HTTP 400 — the harness pipeline failed to resolve on branch refs/heads/fix-template-path:
Reference to a non-existing repository "platform-templates".
```

---

## Files

| File | Role |
|---|---|
| [`harness/validate-templates.yml`](harness/validate-templates.yml) | Never queued directly — a registered Pipeline object whose sole job is being something Preview can expand, calling the real template with representative parameter sets |
| [`gate/validate-template-pr.yml`](gate/validate-template-pr.yml) | The actual PR check — triggers on template changes, calls the Preview API against the harness, fails the PR on any resolution error |
| [`gate/scripts/preview-check.sh`](gate/scripts/preview-check.sh) | The Preview API call itself — exits non-zero with the API's own error text on failure |

---

## Reading Order

1. **The Bug This Answers**, above — the concrete failure mode, in the terms it actually showed
   up in.
2. **`harness/validate-templates.yml`** — note the `trigger: none` / `pr: none` — it exists to
   be previewed, never run, and its stage calls mirror the real ones in
   `azure-squad-cicd-identity/pipelines/deploy-workload.yml`.
3. **`gate/scripts/preview-check.sh`** — the actual API call and what "resolution failed" looks
   like in the response.
4. **`gate/validate-template-pr.yml`** — how the script becomes an actual required PR check,
   and the `System.AccessToken` note on why no stored credential is needed for it.

---

## Key Concepts Glossary

- **Template resolution** — resolving a `template:` reference (and any `@repo` alias on it) to
  an actual file, at pipeline compile time, before any job runs.
- **Compile-time vs runtime pipeline error** — a broken template/repository reference or
  undefined expression fails before any job starts; a missing service connection or Environment
  typically fails later, when a specific job actually tries to use it. The Preview API only
  catches the first category — see `gate/scripts/preview-check.sh`'s comments.
- **Pipelines Preview API** — `POST .../pipelines/{id}/preview`, fully expands a registered
  pipeline (templates, resources, expressions) and returns the resolved YAML or the resolution
  error, without queuing any job.
- **Harness pipeline** — a pipeline whose purpose is being a realistic, representative consumer
  of a template for validation, rather than something anyone runs for its own sake.
- **Representative parameter matrix** — testing the actual shapes a template is really called
  with, rather than every value it could theoretically be called with — bounded and tractable,
  unlike full permutation testing.
- **`System.AccessToken`** — a pipeline's own scoped OAuth token, usable for calling the Azure
  DevOps REST API from within a pipeline run with no stored secret — same "no stored credential"
  principle as the federated identities in `azure-squad-cicd-identity`.

---

## Using These Files

Reference examples — no Azure DevOps project in this sandbox. To wire this up for real:

1. Register `harness/validate-templates.yml` as a Pipeline in Azure DevOps (Pipelines → New
   pipeline → point it at this file) — you never run it manually; it just needs to exist so the
   Preview API has something to target. Note its pipeline ID from the URL or the Pipelines list.
2. Set `harnessPipelineId` in `gate/validate-template-pr.yml` to that ID.
3. On the pipeline running `gate/validate-template-pr.yml` itself: Edit → ... → Settings →
   enable "Allow scripts to access the OAuth token" — this is what lets
   `gate/scripts/preview-check.sh` call the REST API with `$(System.AccessToken)` instead of a
   stored PAT.
4. Register `gate/validate-template-pr.yml` as its own pipeline, with a PR trigger on the repo
   containing the templates — this is the pipeline that actually becomes a required PR check
   (Branch policies → Build validation) on that repo.
5. `harness/validate-templates.yml`'s two stage calls reference
   `azure-bicep-module-contracts-governance/workload/team-blob-store/main.bicep` and use
   placeholder subscription IDs (`00000000-...`) — real subscription IDs aren't needed for
   Preview to work (nothing is deployed), but keep the file paths accurate to whatever your
   repo layout actually is.
