# VSTest to MTP Migration, Without a Big Bang (Azure DevOps)

Moving existing test projects from VSTest to Microsoft.Testing.Platform (MTP) usually means
changing everything in one PR: the test framework, `global.json`, the coverage tool, coverage
settings, thresholds and pipeline tasks. When the coverage numbers then come out different,
it's hard to tell which change caused it.

This experiment splits the migration into small steps you can ship and roll back one at a time.
The key piece is a test project that runs under **both** runners, so the switch itself is one
line in `global.json`. Coverage stays on **coverlet**, so the numbers don't move. Each project's
coverage settings and minimums are carried over to MTP's `testconfig.json` by a script.

[`scripts/test-migration.cs`](scripts/test-migration.cs) proves this end to end, and
[`.github/workflows/vstest-to-mtp-migration.yml`](../../.github/workflows/vstest-to-mtp-migration.yml)
runs it on every change.

---

## Why It's Hard

These are things I hit while building this on the .NET 10 SDK:

| Problem | What happens |
|---|---|
| **No half-migrated solutions** | On the .NET 10 SDK, a project that is an MTP app (xunit.v3 4.x, MSTest.Sdk, MSTest with `EnableMSTestRunner`) fails VSTest-mode `dotnet test` with *"Testing with VSTest target is no longer supported by Microsoft.Testing.Platform on .NET 10 SDK and later"*. There's no opt-out property. Upgrading one project to an MTP-capable framework breaks the whole VSTest run. |
| **`global.json` is all-or-nothing** | `"test": { "runner": "Microsoft.Testing.Platform" }` switches `dotnet test` for every project under that `global.json`. In MTP mode, VSTest-only projects (xunit v2, for example) don't run. |
| **The coverage tool changes** | `coverlet.collector` is VSTest-only. Most MTP guides then move you to Microsoft's coverage extension, which counts branches differently. On the same code and tests: **coverlet 36.8% branch, Microsoft 42.9% branch** (line coverage within 0.1%). Every threshold you tuned now means something else. |
| **Thresholds fail silently** | Under `dotnet test`, a coverlet.MTP threshold failure shows only as `exit code 14`. The table saying which project and which number is printed by the test app but not passed through. |
| **Pipeline tasks change** | `DotNetCoreCLI@2` `command: test` adds `--logger trx`; MTP test apps reject that option and run no tests. |

---

## The Approach

### 1. One test project, both runners, chosen by `global.json`

[`Directory.Build.props`](Directory.Build.props) sets a `TestRunner` property from `global.json`:
`MTP` if `global.json` names `Microsoft.Testing.Platform`, otherwise `VSTest`. While it's
`VSTest`, it turns `IsTestingPlatformApplication` off. That clears the .NET 10 error above, so
the same xunit.v3 project runs happily under VSTest.

Each test csproj picks its packages by runner:

```xml
<ItemGroup>
  <PackageReference Include="xunit.v3" />
</ItemGroup>
<ItemGroup Condition="'$(TestRunner)' == 'VSTest'">
  <PackageReference Include="Microsoft.NET.Test.Sdk" />
  <PackageReference Include="xunit.runner.visualstudio" />
  <PackageReference Include="coverlet.collector" />
</ItemGroup>
<ItemGroup Condition="'$(TestRunner)' == 'MTP'">
  <PackageReference Include="coverlet.MTP" />
</ItemGroup>
```

The build and `dotnet test` both read the same `global.json`, so they can't disagree. Switching
is one line, and so is switching back.

### 2. Keep coverlet: `coverlet.MTP`

`coverlet.MTP` is coverlet's MTP extension: the same engine as `coverlet.collector`, still
writing Cobertura. Same tests, same code:

| | Orders.Domain line / branch | Orders.Api line / branch |
|---|---|---|
| VSTest + `coverlet.collector` | 98.31% / 100% | 100% / 100% |
| MTP + `coverlet.MTP` | 98.31% / 100% | 100% / 100% |

`test-migration.cs` checks these match exactly on every run. Your Cobertura publishing and your
thresholds carry over unchanged. Unlike Microsoft's coverage extension, `coverlet.MTP` also
**enforces thresholds itself** (`threshold`, `thresholdType`, `thresholdStat`), per project.

### 3. Per-project settings move from `.runsettings` to `testconfig.json`

| | VSTest (today) | MTP (after the flip) |
|---|---|---|
| Coverage scope per project | `tests/<Project>/coverage.runsettings`, attached by `RunSettingsFilePath` in [`Directory.Build.targets`](Directory.Build.targets) | `tests/<Project>/testconfig.json` → `platformOptions.Coverlet` |
| Minimum coverage | wherever yours lives today (coverlet.msbuild `<Threshold>`, a pipeline step...) | the same `testconfig.json`: `threshold`, `thresholdType`, `thresholdStat` |
| Report file | `<guid>/coverage.cobertura.xml` | `<Project>.coverage.cobertura.<timestamp>.xml` (prefix set in `Directory.Build.targets`) |

[`scripts/convert-coverage-config.cs`](scripts/convert-coverage-config.cs) writes the
`testconfig.json` section from the runsettings:

```bash
dotnet run scripts/convert-coverage-config.cs -- tests/Orders.Domain.Tests/coverage.runsettings \
  --threshold 95 --threshold-type line,branch --threshold-stat total
#   or: --csproj path/to/Old.Tests.csproj   to read coverlet.msbuild <Threshold>/<ThresholdType>/<ThresholdStat>
```

Why a script rather than copying by hand: once `testconfig.json` has a Coverlet section,
coverlet.MTP treats it as the complete config and adds **no defaults**. A half-copied section
silently changes what is measured. The script copies every setting. If the runsettings holds
something it can't translate, it refuses and writes nothing. `--check` writes nothing either: it
fails if the two files disagree, and CI runs it while both exist.

### 4. One pipeline template for both runners

[`pipelines/templates/dotnet-test-coverage.yml`](pipelines/templates/dotnet-test-coverage.yml)
reads `global.json` at run time and runs one of two test steps:

- **VSTest:** your current `DotNetCoreCLI@2` `command: test` with `--collect "XPlat Code Coverage"`,
  unchanged.
- **MTP:** `DotNetCoreCLI@2` with `command: custom`, `custom: test`, and
  `--solution ... --coverlet --report-xunit-trx`. After it, `PublishTestResults@2` publishes the
  TRX files and [`scripts/coverage-report.cs`](scripts/coverage-report.cs) runs. That script shows
  the per-project threshold table that `dotnet test` hides, puts it on the run's Extensions tab,
  and logs a pipeline error for each project that misses its minimum.

Both paths finish with the same `PublishCodeCoverageResults@2` step. Its glob matches either
runner's Cobertura files. The flip PR needs no pipeline change.

---

## The Migration, Step by Step

Each step is its own PR. Each step leaves the build green and the coverage numbers the same.

| Step | Change | Runner | Coverage numbers |
|---|---|---|---|
| **0. Baseline** | Record today's per-project line/branch numbers from the pipeline. | VSTest | — |
| **1. Make projects dual-capable** | Add the `TestRunner` switch to `Directory.Build.props`/`.targets`. Per test project: move to an MTP-capable framework (xunit v2 → xunit.v3, the biggest code change). Add the conditional package groups. Run the converter to create `testconfig.json`. One project per PR is fine: everything still runs on VSTest. | VSTest | unchanged |
| **2. Pipeline template** | Replace your test steps with the template plus the `--check` step. It detects VSTest and does exactly what it did before. | VSTest | unchanged |
| **3. The flip** | Add `"test": { "runner": "Microsoft.Testing.Platform" }` to `global.json`. Nothing else. | **MTP** | identical (coverlet both sides) |
| **4. Clean up** | Delete the `coverage.runsettings` files, the VSTest package group, the `IsTestingPlatformApplication` switch, the template's VSTest branch and the `--check` step. | MTP | unchanged |
| *5. Optional* | Move to `Microsoft.Testing.Extensions.CodeCoverage` if you want it. Expect branch numbers to change (36.8% → 42.9% on the sample above), so retune thresholds on purpose, in their own PR. | MTP | **changes** |

Rolling back step 3 means removing that line again. `test-migration.cs` checks that VSTest then
runs and gives the step-1 numbers.

---

## Running This Yourself

Needs the .NET 10 SDK. The committed state is **before the flip** (VSTest).

```bash
cd experiments/azure-devops-vstest-to-mtp-migration

# Today: VSTest + coverlet.collector, per-project scope from coverage.runsettings
dotnet test Orders.slnx --collect "XPlat Code Coverage"

# The whole migration, in a throwaway copy: VSTest, flip, MTP with identical coverage,
# a threshold failure (exit code 14, explained by coverage-report.cs), rollback
dotnet run scripts/test-migration.cs            # --keep leaves the copy for a look around

# After flipping global.json yourself:
dotnet test --solution Orders.slnx --coverlet --results-directory TestResults
dotnet run scripts/coverage-report.cs -- --results TestResults
```

Output of `test-migration.cs`:

```
1. VSTest + coverlet.collector
  PASS VSTest run passes
       Orders.Api: line 100.00%, branch 100.00%
       Orders.Domain: line 98.31%, branch 100.00%
  PASS one coverage report per test project (each scoped by its runsettings)
2. testconfig.json matches coverage.runsettings
  PASS Orders.Domain.Tests: convert --check
  PASS Orders.Api.Tests: convert --check
3. Flip global.json to Microsoft.Testing.Platform
4. MTP + coverlet.MTP
  PASS MTP run passes, thresholds from testconfig.json met
  PASS Orders.Api: line 100.00%, branch 100.00% (VSTest: 100.00% / 100.00%)
  PASS Orders.Domain: line 98.31%, branch 100.00% (VSTest: 98.31% / 100.00%)
  PASS a TRX file per test project
  PASS coverage-report: every project at or above its threshold
5. Domain threshold raised to 99%
  PASS MTP run fails with exit code 14 (got 14)
  PASS coverage-report names the project and the line coverage
6. Roll back global.json to VSTest
  PASS VSTest run passes again
  PASS same coverage as step 1

All checks passed.
```

---

## Known Gaps

- **Only xunit.v3 is tested here.** For MSTest, the equivalent switch should be
  `EnableMSTestRunner` (MSTest.TestAdapter packages) or `UseVSTest` (MSTest.Sdk) instead of
  `IsTestingPlatformApplication`. That hasn't been tried in this experiment.
- **xunit v2 can't be dual-mode.** It has no MTP support, so moving to xunit.v3 is required
  before step 3. It's also the step most likely to need code changes (namespaces,
  `IAsyncLifetime` now returns `ValueTask`, `TestContext.Current`).
- **The Azure DevOps pipeline hasn't run in Azure DevOps.** The YAML is syntax-checked, and the
  GitHub workflow runs the same `dotnet` commands through `test-migration.cs`.
- **A very long `TMPDIR` broke MTP runs here.** With `TMPDIR` set to a deep path, `dotnet test`
  in MTP mode failed to talk to the test apps: zero tests ran, and the log showed a
  `KeyNotFoundException`. With the default temp folder, everything passed. I didn't pin down the
  exact cause. Azure DevOps and GitHub agents use short temp paths.
- **`coverage-report.cs` checks line and branch only.** A `method` threshold type is still
  enforced by coverlet.MTP itself; the table just marks it as not checked.
