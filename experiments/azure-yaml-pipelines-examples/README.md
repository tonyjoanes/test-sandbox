# Azure Pipelines YAML — Example Suite

A progressive, heavily-commented set of Azure Pipelines YAML files for
learning the syntax and structure from first principles up to a realistic
multi-environment CI/CD pipeline. Every file is self-contained and readable
on its own — the comments explain *why* each construct exists, not just
what it does.

> These files are reference material, not an executable demo. To actually
> run one, copy it (or point `azure-pipelines.yml` via `extends`/include) at
> a real Azure DevOps project connected to this repo — see [Using These
> Files](#using-these-files) below.

---

## Mental Model First

Every Azure Pipeline, however small, decomposes into the same hierarchy:

```
Pipeline
├── trigger / pr / schedules      what causes it to run
├── variables                     values usable throughout
└── stages (optional)             logical phases: Build, Test, Deploy...
    └── jobs                      unit of work assigned to ONE agent
        └── steps                 individual commands/tasks, run in order
```

If you omit `stages`, Azure implicitly wraps your `jobs`/`steps` in one
stage and one job — that's why `01-basic-ci.yml` can jump straight to
`steps:` with no ceremony.

**Three expression syntaxes**, resolved at different times — mixing these
up is the #1 source of confusion for newcomers:

| Syntax | Name | Resolved | Can change pipeline structure? |
|---|---|---|---|
| `${{ variables.x }}` | Compile-time (template) expression | Before the run is submitted | Yes — can add/remove steps, jobs, stages |
| `$[ variables.x ]` | Runtime expression | When the stage/job starts | No — value only |
| `$(x)` | Macro syntax | Just before each task runs | No — value only, string substitution |

Every example below calls out which syntax it's using and why.

---

## The Examples

| # | File | Concepts Covered |
|---|---|---|
| 01 | [`pipelines/01-basic-ci.yml`](pipelines/01-basic-ci.yml) | Minimal pipeline: `trigger`, `pool`, `steps`, `script` vs `task` |
| 02 | [`pipelines/02-variables-and-expressions.yml`](pipelines/02-variables-and-expressions.yml) | All three expression syntaxes, variable groups, `setvariable`, predefined system variables |
| 03 | [`pipelines/03-triggers-and-paths.yml`](pipelines/03-triggers-and-paths.yml) | CI trigger, PR trigger, path filters, `batch`, cron `schedules`, `trigger: none` |
| 04 | [`pipelines/04-multi-stage.yml`](pipelines/04-multi-stage.yml) | `stages`, `dependsOn`, stage-level `condition`, fan-out/fan-in, cross-stage output variables |
| 05 | [`pipelines/05-matrix-and-strategy.yml`](pipelines/05-matrix-and-strategy.yml) | `strategy.matrix` (cross-platform builds), `strategy.parallel` (test sharding), `maxParallel` |
| 06 | [`pipelines/06-caching-and-artifacts.yml`](pipelines/06-caching-and-artifacts.yml) | `Cache@2` vs pipeline artifacts, `publish`/`download`, `PublishTestResults@2` |
| 07 | [`pipelines/07-conditions-and-dependencies.yml`](pipelines/07-conditions-and-dependencies.yml) | `succeeded()`, `failed()`, `always()`, custom boolean expressions, cross-job `dependencies.*.outputs` |
| 08 | [`pipelines/08-container-jobs.yml`](pipelines/08-container-jobs.yml) | Running steps inside a container image, `resources.containers`, sidecar `services` (Postgres/Redis) |
| 09 | [`pipelines/09-deployment-environments.yml`](pipelines/09-deployment-environments.yml) | `deployment` jobs, `environment`, approval gates (UI-configured), `runOnce` vs `canary` strategy |
| 10 | [`pipelines/10-full-cicd-pipeline.yml`](pipelines/10-full-cicd-pipeline.yml) | Everything above combined into one realistic build-once/promote-everywhere pipeline, using templates |
| 11 | [`pipelines/11-variable-templates.yml`](pipelines/11-variable-templates.yml) | Variable templates — fixed shared values, and a parameterised template that branches per environment |
| 12 | [`pipelines/12-extends-and-loops.yml`](pipelines/12-extends-and-loops.yml) | `${{ each }}` loops for dynamic step/job generation, plus `extends:` governance templates |

### Reusable Templates

Azure Pipelines has templates for every level of the hierarchy — **steps**,
**jobs**, **stages**, **variables**, and whole-**pipeline** (`extends`).
Which one to reach for depends on what's actually repeating:

| File | Kind | Purpose |
|---|---|---|
| [`templates/steps/build-and-test.yml`](templates/steps/build-and-test.yml) | Step | Dedupes a restore/build/test sequence, parameterised by SDK version and config |
| [`templates/steps/steps-from-list.yml`](templates/steps/steps-from-list.yml) | Step (`${{ each }}`) | Generates one step per entry in a list parameter — e.g. N lint commands, each reporting pass/fail independently |
| [`templates/jobs/run-tests.yml`](templates/jobs/run-tests.yml) | Job | A whole test job, stamped out multiple times with different parameters (own pool, no shared steps needed) |
| [`templates/jobs/jobs-from-list.yml`](templates/jobs/jobs-from-list.yml) | Job (`${{ each }}`) | Generates one job per entry in an object-list parameter, each structurally different (own pool/steps) — for when a matrix isn't flexible enough |
| [`templates/stages/deploy-stage.yml`](templates/stages/deploy-stage.yml) | Stage | One deployment stage, invoked once per environment (dev/staging/production) |
| [`templates/variables/common-variables.yml`](templates/variables/common-variables.yml) | Variables | Fixed shared values (tool versions, image names) — one source of truth included via `variables: - template:` |
| [`templates/variables/per-environment-variables.yml`](templates/variables/per-environment-variables.yml) | Variables (parameterised) | Compile-time branches on an `environmentName` parameter to hand back a different variable set per environment |
| [`templates/extends/base-pipeline.yml`](templates/extends/base-pipeline.yml) | Extends (whole pipeline) | Org-owned pipeline structure with mandatory stages (policy check, security scan) that consumers can fill but not remove or bypass |

Use a **step** template to dedupe a repeated sequence of commands, a **job**
template when the reusable unit needs its own pool/strategy, a **stage**
template when whole phases (like "deploy to an environment") repeat with
only a parameter changing, a **variables** template to centralise shared or
per-environment values, and `${{ each }}` inside a step/job template when
the number of repetitions comes from a list rather than being fixed.
`extends` templates sit above all of these — they're how a platform team
makes parts of a pipeline **mandatory** rather than merely reusable. File
`10` combines step/job/stage templates; `11` and `12` cover variable
templates, `${{ each }}`, and `extends` respectively.

---

## Reading Order

1. **01 → 04**: the core structural building blocks, in order of increasing scope (steps → variables/triggers → stages).
2. **05 → 07**: execution control — how work gets parallelised, how data moves between jobs, and how flow is conditioned on outcomes/branches.
3. **08 → 09**: environment concerns — where code actually runs (containers) and how it ships safely (deployment jobs, approvals).
4. **`templates/steps`, `templates/jobs`, `templates/stages`, then 10**: once the individual concepts make sense, see how the three basic template kinds compose into one pipeline you'd actually put in a repo.
5. **`templates/variables`, then 11**: centralising and environment-branching variables via templates.
6. **`templates/*/…-from-list.yml`, `templates/extends`, then 12**: the two advanced patterns — generating steps/jobs from a list with `${{ each }}`, and locking down pipeline structure org-wide with `extends`.

---

## Key Concepts Glossary

- **Agent / pool** — the VM (or container) that executes a job. Microsoft-hosted (`vmImage: ubuntu-latest`) or self-hosted (`pool: { name: ... }`).
- **Stage** — a logical grouping of jobs; stages run sequentially by default via implicit `dependsOn`, but that's just a default you can override.
- **Job** — the unit of work scheduled onto a single agent; steps within a job always run in order on that one machine.
- **Deployment job** (`deployment:` instead of `job:`) — a job type specifically for shipping code, which unlocks `environment:` (approval gates, checks) and rollout `strategy:` (`runOnce`, `rolling`, `canary`).
- **Environment** — an Azure DevOps resource (configured in the portal, *not* YAML) that deployment jobs target. This is deliberate: approvers and checks live outside the YAML so whoever can edit a pipeline file can't also grant themselves deploy approval.
- **Template** (`template:`) — a separate YAML file *inserted* into whatever the including file already defines, parameterised with `parameters:`. Comes in step, job, stage, and variables flavours (see table above) depending on what level of the hierarchy it targets.
- **`${{ each x in parameters.list }}`** — a compile-time loop inside a template that stamps out one copy of a YAML node (a step, a job) per item in a list/object parameter. Use it instead of `strategy.matrix` when the generated units aren't identical (different steps, different pools) rather than just different variable values.
- **`extends:`** — the inverse of `template:`. A pipeline that uses `extends:` hands its *entire* structure to the named template and may only supply parameter values — it has no `stages:`/`jobs:`/`steps:` key of its own. This is how platform/security teams make stages (a security scan, an approved deploy path) mandatory across every pipeline in an org, rather than merely reusable. Parameter `values:` restriction (an allow-list on a parameter's valid inputs, checked at compile time) is often layered on top for the same reason.
- **Artifact vs Cache** — an artifact is pipeline *output* you depend on (build binaries, test results); a cache is a best-effort speed optimisation for *inputs* (downloaded packages) that can be silently missed without failing the build. Never use a cache to pass required data between jobs.

---

## Using These Files

These are reference examples, not wired into any Azure DevOps project from
this sandbox. To try one for real:

1. Create (or use an existing) Azure DevOps project with a pipeline pointed at this repo.
2. Copy the contents of whichever `pipelines/*.yml` file you want to try into your pipeline's YAML path (commonly `azure-pipelines.yml` at the repo root), or set the pipeline's YAML file path directly to `experiments/azure-yaml-pipelines-examples/pipelines/<file>.yml`.
3. Files `10`, `11`, and `12` reference templates via relative paths (`../templates/...`) — keep the `templates/` folder alongside `pipelines/` if you copy things out, or just point the pipeline definition at the file in place.
4. Files referencing `dotnet`/`npm`/language-specific tasks assume a matching app in the repo; swap those steps for your actual build tool if you're following along with a different stack — the pipeline *structure* (stages/jobs/conditions/templates) is language-agnostic.
5. `09` and `10`'s `environment:` targets (`dev`, `staging`, `production`) need to exist as Environments in your Azure DevOps project (Pipelines → Environments) before a deployment job can target them; that's also where you'd configure the approval gates the comments describe.
