# MTP Code Coverage Per Test Project (Azure DevOps)

A small Orders API with two test projects that run on **Microsoft.Testing.Platform (MTP)**,
the test platform Microsoft now recommends in place of VSTest. Each test project has its own
coverage settings and its own minimum coverage, set in that project's `testconfig.json`. An
Azure DevOps pipeline runs the tests, publishes test results and coverage, and fails the build
if a project is under its minimum.

Built and run here on the .NET 10 SDK: 55 tests pass, and the coverage gate passes and fails
when it should. The Azure DevOps pipeline itself hasn't been run in Azure DevOps from this
sandbox. [`.github/workflows/mtp-code-coverage.yml`](../../.github/workflows/mtp-code-coverage.yml)
runs the same `dotnet` commands on every change.

---

## Layout

```
global.json                     SDK version, MTP as the dotnet test runner, MSTest.Sdk version
Directory.Build.props           net10.0, nullable, warnings as errors, CI-deterministic builds
Directory.Packages.props        Central Package Management: every package version in one place
Orders.slnx                     the new XML solution format

src/Orders.Domain/              pricing rules, order lifecycle, Money. No ASP.NET Core.
src/Orders.Api/                 minimal API: TypedResults, route groups, .NET 10 validation, OpenAPI

tests/Orders.Domain.Tests/      MSTest unit tests for the domain           testconfig.json: domain only, 95% / 90%
tests/Orders.Api.Tests/         in-memory HTTP tests (WebApplicationFactory) testconfig.json: API only, 85% / 75%

scripts/check-coverage.cs       per-project coverage gate (.NET 10 file-based app)
pipelines/azure-pipelines.yml   the demo pipeline
pipelines/templates/dotnet-test-mtp.yml        the reusable test + coverage + publish steps
pipelines/azure-pipelines.per-project.yml      alternative: one parallel job per test project
```

---

## The MTP Pieces

**`global.json` switches `dotnet test` to MTP.** From the .NET 10 SDK, `dotnet test` runs
MTP natively instead of going through VSTest:

```json
{
  "sdk":  { "version": "10.0.100", "rollForward": "latestFeature" },
  "test": { "runner": "Microsoft.Testing.Platform" },
  "msbuild-sdks": { "MSTest.Sdk": "4.5.1" }
}
```

Two things change once this is on. You pass the solution or project with `--solution` or
`--project`, not as a positional path. And every option after that goes to the test apps
themselves (`--coverage`, `--report-trx`, `--filter`, and so on). The VSTest switches
(`--collect`, `--logger`, `--settings` for runsettings) no longer apply.

**`MSTest.Sdk` replaces the usual package list.** Each test csproj is just:

```xml
<Project Sdk="MSTest.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\..\src\Orders.Domain\Orders.Domain.csproj" />
  </ItemGroup>
</Project>
```

MSTest.Sdk sets `OutputType=Exe`, enables the MTP runner, and brings in MSTest, its analyzers,
and its default extensions: **code coverage** (`Microsoft.Testing.Extensions.CodeCoverage`)
and **TRX reports**. You don't add `Microsoft.NET.Test.Sdk`, `coverlet.collector` or
`xunit.runner.visualstudio`. Each test project builds to an executable. You can run it
directly (`./Orders.Domain.Tests --coverage`) or through `dotnet test`.

The tests use the MSTest 4 assertion APIs (`Assert.ThrowsExactly`, `Assert.HasCount`,
`Assert.ContainsSingle`, `Assert.Contains`) and `TestContext.CancellationToken`. They use
`FakeTimeProvider` for the one time-dependent rule (the weekend coupon).

---

## Coverage Per Test Project

Every MTP test app reads a **`testconfig.json`**. The build copies it next to the app as
`<Project>.testconfig.json`, so a project's file only ever applies to that project. From
version 18.0, the coverage extension reads its settings from a `codeCoverage` section in that
file. These are the same settings you would put in a runsettings `DataCollector` block, written
as JSON.

So each project states its own coverage, and the pipeline passes the same arguments to every
project:

| | `Orders.Domain.Tests` | `Orders.Api.Tests` |
|---|---|---|
| Counts coverage for | `Orders.Domain.dll` only | `Orders.Api.dll` only |
| Also excludes | — | `Program.cs` (composition root), anything under `obj/` (source-generated), `[GeneratedCode]` |
| Report | `Orders.Domain.Tests.cobertura.xml` | `Orders.Api.Tests.cobertura.xml` |
| Minimum line / branch | 95% / 90% | 85% / 75% |

The include lists matter most. The API tests host the whole app in memory, so they also run
the domain code. Without `"Include": [".*Orders\\.Api\\.dll$"]`, the API project's report would
count `Orders.Domain` as covered by HTTP tests that never asserted on a pricing rule. With the
include list removed, the API report adds `Orders.Domain` at 86.2% line coverage, all of it
from HTTP tests.

The domain project's file, in full:

```jsonc
{
  "platformOptions": { "exitProcessOnUnhandledException": true },
  "codeCoverage": {
    "Configuration": {
      "Format": "cobertura",
      "CoverageFileName": "Orders.Domain.Tests.cobertura.xml",
      "CodeCoverage": {
        "ModulePaths": { "Include": [ ".*Orders\\.Domain\\.dll$" ] }
      }
    }
  },
  "coverageGate": { "minimumLineRate": 95, "minimumBranchRate": 90 }
}
```

`testconfig.json` accepts `//` comments. The real files in `tests/*/` use them to explain each
setting.

### The minimum-coverage gate

The Microsoft coverage extension collects coverage but has no option to fail a run under a
given percentage. `coverageGate` is not an MTP setting; MTP ignores it.
[`scripts/check-coverage.cs`](scripts/check-coverage.cs) does that check. For each
`tests/*/testconfig.json` it:

1. finds that project's report, using `codeCoverage.Configuration.CoverageFileName`
2. reads the line and branch rate from the Cobertura file
3. compares them with that project's `coverageGate`

It prints a table and fails if any project is under its minimum or has no report. On Azure
DevOps it also attaches the table to the run summary (`##vso[task.uploadsummary]`) and logs
each failure as a pipeline error. Keeping the minimum next to the coverage filters means one
file per project describes what is measured and what bar it has to clear.

The numbers from a real run:

```
| Project             | Line   | Min line | Branch | Min branch | Result  |
|---------------------|-------:|---------:|-------:|-----------:|---------|
| Orders.Api.Tests    | 100.0% |    85.0% | 100.0% |      75.0% | ✅ pass |
| Orders.Domain.Tests |  97.9% |    95.0% | 100.0% |      90.0% | ✅ pass |
```

---

## Running This Yourself

Needs the .NET 10 SDK.

```bash
cd experiments/azure-devops-mtp-code-coverage

dotnet build Orders.slnx -c Release

dotnet test --solution Orders.slnx -c Release --no-build \
  --results-directory TestResults \
  --coverage --report-trx --report-trx-filename '{asm}.trx'

dotnet run scripts/check-coverage.cs -- --results TestResults
```

`TestResults/` then holds one `.cobertura.xml` and one `.trx` per test project. To run just one
project: `dotnet test --project tests/Orders.Api.Tests --coverage`. It still uses that project's
coverage settings, because they're in its own `testconfig.json`.

To see the gate fail, raise a `minimumLineRate` in either `testconfig.json` above that project's
current number and run the last command again.

---

## The Pipeline

[`pipelines/azure-pipelines.yml`](pipelines/azure-pipelines.yml) runs a test stage, then
packages the API on `main` only. All the test steps are in
[`pipelines/templates/dotnet-test-mtp.yml`](pipelines/templates/dotnet-test-mtp.yml), so other
solutions can reuse them:

| Step | Task | What it does |
|---|---|---|
| Install SDK | `UseDotNet@2` with `useGlobalJson: true` | Installs the SDK version from `global.json`, the same file that turns on MTP |
| Restore / build | `DotNetCoreCLI@2` | Builds the solution once, in Release |
| **Run tests + coverage** | `DotNetCoreCLI@2`, `command: custom`, `custom: test` | `dotnet test --solution Orders.slnx --no-build --coverage --report-trx --report-trx-filename {asm}.trx --results-directory $(Agent.TempDirectory)/TestResults` |
| **Publish test results** | `PublishTestResults@2`, `testResultsFormat: VSTest` | Adds the TRX files to the run's **Tests** tab. Runs even if tests failed. |
| **Publish coverage** | `PublishCodeCoverageResults@2` | Merges both Cobertura files into the run's **Code Coverage** tab, with an HTML report |
| Artifact | `PublishPipelineArtifact@1` | Keeps the raw `.trx` and `.cobertura.xml` files |
| **Coverage gate** | `DotNetCoreCLI@2`, `custom: run` | `scripts/check-coverage.cs`, per-project table on the **Extensions** tab |

Points that are easy to get wrong:

- **Use `command: custom`, not `command: test`.** `DotNetCoreCLI@2`'s `test` command was
  built for VSTest. It adds `--logger trx` and its own `--results-directory`. Under MTP, every
  test app treats `--logger` as an invalid option: it exits with code 5 and runs no tests
  (checked locally). With `custom`, the task runs exactly the arguments you give it.
- **No coverage options per project in the YAML.** Every project gets the same `--coverage`.
  Its `testconfig.json` decides what is measured and what the file is called. A new test
  project needs a `testconfig.json`, not a pipeline change.
- **Unique `CoverageFileName` per project.** All projects write to one `--results-directory`,
  so each report needs its own name. `{asm}.trx` does the same for TRX files.
- **Azure DevOps keeps one coverage report per run.** `PublishCodeCoverageResults@2` takes a
  glob and merges every file it matches. That gives a single combined view. The per-project
  numbers are in the gate's summary table.
- **Telemetry opt-out.** `TESTINGPLATFORM_TELEMETRY_OPTOUT=1` alongside the usual
  `DOTNET_CLI_TELEMETRY_OPTOUT`.

### One job per test project

[`pipelines/azure-pipelines.per-project.yml`](pipelines/azure-pipelines.per-project.yml) runs
each test project in its own parallel matrix job, with `dotnet test --project tests/<name>`.
Use this layout when projects are slow or need different agents. Each job publishes its own
test results and runs the gate for its own project only. Because a run keeps a single coverage
report, a final job downloads every job's Cobertura files and publishes them together.

### Setting it up in Azure DevOps

1. **Pipelines → New pipeline**, pick the repo, then **Existing Azure Pipelines YAML file** →
   `experiments/azure-devops-mtp-code-coverage/pipelines/azure-pipelines.yml`.
2. Run it. The **Tests**, **Code Coverage** and **Extensions** tabs fill in on the run page.
3. To block PRs on it, add the pipeline as a **build validation** branch policy on `main`.
   Both the test step and the coverage gate fail the build, so a PR can't merge under its
   project's minimum.

---

## Known Gaps

- **Not run in Azure DevOps from here.** The YAML is checked for syntax, and the GitHub workflow
  runs the same commands. The Azure DevOps task inputs are written from the task docs but not
  tested in a real organisation.
- **The gate checks the whole report.** It doesn't check coverage of only the changed lines.
  For that, Azure DevOps has its own pull request coverage check: an `azurepipelines-coverage.yml`
  at the repo root, which uses the coverage published above.
- **`Quote` record lines.** The two uncovered domain lines are compiler-generated members of
  the positional `Quote` record. They're left in, so the domain number reflects what the
  coverage tool really reports.
