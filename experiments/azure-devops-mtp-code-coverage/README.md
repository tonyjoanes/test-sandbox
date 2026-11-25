# MTP Unit Testing and Code Coverage (Azure DevOps + PowerShell)

A small Orders Web API with three test projects on **Microsoft.Testing.Platform (MTP)**, the test
platform Microsoft now recommends in place of VSTest. It shows how to set MTP up, how code coverage is
collected and combined across several test projects, and how to read and gate the results in an Azure
DevOps pipeline written in PowerShell.

**Start here:**

| If you want to... | Read |
|---|---|
| Get MTP running, or fix an MTP error | [docs/setting-up-mtp.md](docs/setting-up-mtp.md) |
| Understand what the coverage numbers mean across several test projects | [docs/how-coverage-works.md](docs/how-coverage-works.md) |
| See it run | `./scripts/Invoke-TestCoverage.ps1` (below) |

Verified here on the .NET 10 SDK (10.0.115) and PowerShell 7.7: 58 tests pass, the gate passes and fails
when it should, and every number in the docs comes from a real run. The Azure DevOps pipelines haven't
been run in Azure DevOps from this sandbox. Their PowerShell steps were run locally with the pipeline
variables substituted, and [`.github/workflows/mtp-code-coverage.yml`](../../.github/workflows/mtp-code-coverage.yml)
runs the same scripts on every change.

---

## Layout

```
global.json                     SDK version + "test": { "runner": "Microsoft.Testing.Platform" }
Directory.Build.props           net10.0, nullable, warnings as errors, CI-deterministic builds
Directory.Packages.props        Central Package Management (note the UsingMSTestSdk condition)
.config/dotnet-tools.json       ReportGenerator, as a local tool (dotnet tool restore)
Orders.slnx

src/Orders.Domain/              pricing rules, order lifecycle, Money. No ASP.NET Core.
src/Orders.Api/                 minimal API over the domain

tests/Orders.Domain.Tests/      MSTest (MSTest.Sdk)   unit tests         measures Orders.Domain      min 95% line / 90% branch
tests/Orders.Api.Tests/         MSTest (MSTest.Sdk)   in-memory HTTP     measures Orders.Api         min 85% / 75%
tests/Orders.Integration.Tests/ xUnit v3 (plain SDK)  end-to-end flows   measures Api + Domain       min 60% / 40%

scripts/Invoke-TestCoverage.ps1 local one-liner: build, test with coverage, gate, merged HTML report
scripts/Test-CoverageGate.ps1   each test project against its own minimum (from its testconfig.json)
scripts/New-CoverageReport.ps1  merges every project's report (ReportGenerator): HTML + summary
scripts/Show-CoverageModes.ps1  teaching aid: the same code measured unfiltered / filtered / merged

pipelines/azure-pipelines.yml                  the demo pipeline
pipelines/templates/dotnet-test-mtp.yml        reusable test + coverage + publish + gate steps (pwsh)
pipelines/azure-pipelines.per-project.yml      alternative: one parallel job per test project

docs/setting-up-mtp.md          MTP setup for MSTest and xUnit, plus troubleshooting with real errors
docs/how-coverage-works.md      how coverage is collected, merged and evaluated, with Mermaid diagrams
```

Two MSTest projects and one xUnit v3 project on purpose. `MSTest.Sdk` hides most of the MTP setup.
The xUnit project shows the pieces you add yourself with any other framework.

---

## Running It

Needs the .NET 10 SDK and PowerShell 7.

```powershell
cd experiments/azure-devops-mtp-code-coverage
./scripts/Invoke-TestCoverage.ps1
```

Output from a real run:

```
## Code coverage per test project

| Project                  | Line   | Min line | Branch | Min branch | Result  |
|--------------------------|-------:|---------:|-------:|-----------:|---------|
| Orders.Api.Tests         | 100.0% |    85.0% | 100.0% |      75.0% | ✅ pass |
| Orders.Domain.Tests      |  97.9% |    95.0% | 100.0% |      90.0% | ✅ pass |
| Orders.Integration.Tests |  83.2% |    60.0% |  70.0% |      40.0% | ✅ pass |

Merged coverage (a line is covered if ANY test project hit it):
Assembly                 Line %  Branch %  Covered  Coverable
Orders.Api                  100       100       75         75
Orders.Domain              97.8       100       90         92
Total: 98.8% line, 100% branch
HTML report: .../TestResults/CoverageReport/index.html
```

Then:

- open `TestResults/CoverageReport/index.html` (how to read it: [docs/how-coverage-works.md § 6](docs/how-coverage-works.md#6-evaluating-results))
- run `./scripts/Show-CoverageModes.ps1` to see why per-project, unfiltered and merged numbers differ
- raise a `minimumLineRate` in any `tests/*/testconfig.json` above that project's number and run again
  to watch the gate fail

One project only: `./scripts/Invoke-TestCoverage.ps1 -Project tests/Orders.Api.Tests`.

---

## The Design in Four Points

1. **`global.json` switches `dotnet test` to MTP.** After that, `dotnet test` takes `--solution` /
   `--project`, and every other option goes to the test apps (`--coverage`, `--report-trx`, `--filter`).
   VSTest switches (`--collect`, `--logger`, `--settings`) fail with exit code 5.
2. **Each test project's `testconfig.json` says what it measures and how much is enough.** The
   `codeCoverage` section (filters, Cobertura format, a unique file name) is read by the coverage
   extension. The `coverageGate` section (minimums) is read by `Test-CoverageGate.ps1`. The pipeline
   passes the same arguments to every project, and a new test project needs no pipeline change.
3. **Per-project gates, merged overview.** Merged coverage is a union, and it hides unit test gaps
   that integration tests happen to run. The docs show this with a real run. So the build fails per
   project, and the merged number is for overview.
4. **Everything scripted is PowerShell 7**, in files you can run locally exactly as the pipeline
   does. `PowerShell@2` with `pwsh: true` everywhere: Windows PowerShell 5.1 can't parse the `//`
   comments in `testconfig.json`.

---

## The Pipeline

[`pipelines/azure-pipelines.yml`](pipelines/azure-pipelines.yml) runs the test stage, then packages the
API on `main` only. The test steps are in [`pipelines/templates/dotnet-test-mtp.yml`](pipelines/templates/dotnet-test-mtp.yml):

| Step | Task | Result |
|---|---|---|
| Install SDK | `UseDotNet@2`, `useGlobalJson: true` | SDK version from `global.json`, the same file that turns on MTP |
| Build | `PowerShell@2` (pwsh) | `dotnet build`, once |
| **Test + coverage** | `PowerShell@2` (pwsh) | `dotnet test --solution ... --no-build --coverage --report-trx --report-trx-filename '{asm}.trx'` |
| Publish test results | `PublishTestResults@2` | **Tests** tab, one run per project, even if tests failed |
| Publish coverage | `PublishCodeCoverageResults@2` | **Code Coverage** tab: all projects merged into one report |
| Merged summary | `PowerShell@2` → `New-CoverageReport.ps1` | Per-assembly merged table on the **Extensions** tab |
| Artifact | `PublishPipelineArtifact@1` | Raw `.trx`, `.cobertura.xml` and the HTML report |
| **Gate** | `PowerShell@2` → `Test-CoverageGate.ps1` | Per-project table on **Extensions**; build fails if any project is under its minimum |

Easy to get wrong:

- **Don't use `DotNetCoreCLI@2` with `command: test`.** It was built for VSTest and adds `--logger trx`.
  Under MTP every test app rejects that: exit code 5, no tests run.
- **Each project needs a unique `CoverageFileName`.** All projects write to one `--results-directory`.
- **Azure DevOps keeps one coverage report per run.** The Code Coverage tab is always the merged view.
  The per-project view is the gate table.
- **Telemetry:** set `TESTINGPLATFORM_TELEMETRY_OPTOUT=1` alongside `DOTNET_CLI_TELEMETRY_OPTOUT`.

[`pipelines/azure-pipelines.per-project.yml`](pipelines/azure-pipelines.per-project.yml) runs each test
project in its own parallel job. Each job gates its own project, and a final job collects every job's
Cobertura files and publishes them as one merged report.

### Setting it up in Azure DevOps

1. **Pipelines → New pipeline**, pick the repo, then **Existing Azure Pipelines YAML file** →
   `experiments/azure-devops-mtp-code-coverage/pipelines/azure-pipelines.yml`.
2. Run it. The **Tests**, **Code Coverage** and **Extensions** tabs fill in on the run page.
3. To block PRs on it, add the pipeline as a **build validation** branch policy on `main`.

---

## Known Gaps

- **Not run in Azure DevOps from here.** The YAML parses, its PowerShell steps were run locally with
  pipeline variables substituted (including `##vso` output and failing exit codes), and the GitHub
  workflow runs the same scripts. The Azure DevOps task inputs follow the task docs but haven't been
  tested in a real organisation.
- **The gate checks whole-project coverage, not changed lines.** For coverage of changed lines in a PR,
  Azure DevOps has its own check: an `azurepipelines-coverage.yml` at the repo root, which uses the
  coverage published above.
- **Two lines in `Orders.Domain` can never be covered.** They're `public const decimal` declarations,
  which the compiler puts in a static constructor that never runs. Explained in
  [docs/how-coverage-works.md § 5](docs/how-coverage-works.md#5-why-your-numbers-dont-match-someone-elses).
  They're left in so the numbers show what the tool really reports.
- **Mermaid diagrams** render on GitHub and in VS Code. In an Azure DevOps **wiki**, wrap each one in
  `::: mermaid` … `:::` instead of a ```` ```mermaid ```` fence.
