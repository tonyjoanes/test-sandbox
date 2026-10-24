# `dotnet new` Project Templates via Azure Artifacts

A template pack containing a **.NET Web API** and an **Azure Functions (isolated worker) app**.
An Azure DevOps pipeline builds the pack, smoke-tests it and publishes it to an **Azure Artifacts
feed**. Any developer can then run:

```bash
dotnet new install Contoso.Platform.Templates
dotnet new contoso-webapi -n Acme.Orders.Api
dotnet new contoso-func   -n Acme.Orders.Worker --timer
```

They get a renamed, restored project that compiles, built on the platform team's current
defaults. There is no copying an old repo and find-and-replacing names.

> Like the rest of this repo, this is reference material. There is no Azure DevOps organisation
> in this sandbox, so the pipeline hasn't run for real. Everything below the pipeline has been
> run: packing, installing, generating every option combination, and building the output (see
> [What's been verified](#whats-been-verified)).

---

## Contents

- [Layout](#layout)
- [How it works end to end](#how-it-works-end-to-end)
- [How a `dotnet new` template works](#how-a-dotnet-new-template-works)
- [The template package](#the-template-package)
- [The templates in this pack](#the-templates-in-this-pack)
- [Setting it up in Azure DevOps](#setting-it-up-in-azure-devops)
- [Using the templates (consumer guide)](#using-the-templates-consumer-guide)
- [Changing or adding a template (author guide)](#changing-or-adding-a-template-author-guide)
- [Versioning](#versioning)
- [Troubleshooting](#troubleshooting)
- [What's been verified](#whats-been-verified)

---

## Layout

```
azure-devops-dotnet-new-templates/
├── Contoso.Platform.Templates.csproj   # pack project: zips templates/ into a Template .nupkg
├── PACKAGE.md                          # readme shown on the feed's package page
├── nuget.config.example                # consumer config pointing at the Azure Artifacts feed
├── pipelines/
│   └── templates-ci.yml                # build → smoke test → push to feed (main only)
├── scripts/
│   └── smoke-test.sh                   # pack, install to a scratch cache, generate, build
└── templates/
    ├── webapi/                         # "contoso-webapi"
    │   ├── .template.config/
    │   │   ├── template.json           # template definition: name, parameters, rules
    │   │   └── dotnetcli.host.json     # friendly CLI option names (--openapi, --samples)
    │   ├── Contoso.WebApi.csproj       # "Contoso.WebApi" is the placeholder that gets renamed
    │   ├── Program.cs
    │   ├── Endpoints/ItemEndpoints.cs  # removed when --samples false
    │   └── ...
    └── funcapp/                        # "contoso-func"
        ├── .template.config/...
        ├── Contoso.FunctionApp.csproj
        ├── Program.cs
        ├── Functions/HelloFunction.cs
        ├── Functions/TimerFunction.cs  # only included with --timer
        ├── local.settings.template.json  # renamed to local.settings.json on generation
        └── ...
```

---

## How it works end to end

```
 Template repo (this folder)          Azure DevOps pipeline                 Azure Artifacts feed
 ┌──────────────────────────┐   push  ┌─────────────────────────────┐ push ┌─────────────────────┐
 │ templates/webapi/        │ ──────▶ │ 1. dotnet pack (Template)   │ ───▶ │ Contoso.Platform.   │
 │ templates/funcapp/       │         │ 2. dotnet new install (tmp) │ main │   Templates 1.0.N   │
 │ *.csproj (PackageType=   │         │ 3. dotnet new … + build     │ only │                     │
 │          Template)       │         │    for every option combo   │      └──────────┬──────────┘
 └──────────────────────────┘         └─────────────────────────────┘                 │
                                                                                      │ dotnet new install
                                       Developer machine / other pipelines            ▼
                                       ┌───────────────────────────────────────────────────────┐
                                       │ dotnet new contoso-webapi -n Acme.Orders.Api          │
                                       │   → ./Acme.Orders.Api/Acme.Orders.Api.csproj          │
                                       │     namespace Acme.Orders.Api; packages restored      │
                                       └───────────────────────────────────────────────────────┘
```

1. **Templates are ordinary projects.** Each folder under `templates/` is a real project that
   builds and runs as it is (`dotnet run --project templates/webapi`). A `.template.config/`
   folder alongside it turns it into a template.
2. **The pack project zips them into a NuGet package** of type `Template`. No code is compiled.
3. **The pipeline proves the package works** by installing it into a throwaway template cache,
   generating a project for each option combination, and building each one with
   `-warnaserror`.
4. **On `main`, the pipeline pushes the package to an Azure Artifacts feed.**
5. **Developers install the package once** with `dotnet new install`, which downloads it through
   NuGet, so it works with any feed NuGet can authenticate to. From then on `dotnet new
   contoso-webapi` behaves like a built-in template, and the templates also appear in Visual
   Studio's and Rider's New Project dialogs.

---

## How a `dotnet new` template works

The .NET template engine copies the template folder to the output directory. While copying, it
applies the rules in `.template.config/template.json`. The parts this pack uses:

### Identity and the name you type

```jsonc
"identity": "Contoso.Templates.WebApi.CSharp",  // globally unique; never change it once published
"groupIdentity": "Contoso.Templates.WebApi",    // groups language variants under one short name
"shortName": "contoso-webapi",                  // what you type after `dotnet new`
"classifications": [ "Contoso", "Web", "WebAPI" ], // tags shown in `dotnet new list` and IDE filters
```

Prefix short names (`contoso-…`) so they never clash with Microsoft's `webapi` and `func`
templates.

### Renaming: `sourceName`

```jsonc
"sourceName": "Contoso.WebApi",
"preferNameDirectory": true
```

Every occurrence of `Contoso.WebApi` in **file contents and file names** is replaced with the
value of `-n` / `--name`. That covers the `.csproj` file name, `RootNamespace` and every
`namespace` line. The engine also replaces derived forms such as `Contoso_WebApi` and
lower-case variants, so a hyphenated name like `-n acme-orders` still produces a valid C#
namespace (`acme_orders`). With `preferNameDirectory`, leaving out `-o` creates a folder named
after the project.

Choose a placeholder that won't appear anywhere by accident. The smoke test fails if either
placeholder survives into a generated project.

### Parameters: `symbols`

```jsonc
"EnableOpenApi": { "type": "parameter", "datatype": "bool", "defaultValue": "true" }
```

Each parameter becomes a CLI option. Its default name is `--EnableOpenApi`, and
`dotnet new contoso-webapi --help` lists them all. `dotnetcli.host.json` renames options to
something friendlier (`--openapi`) and hides internal ones.

A `string` parameter with `"replaces"` does a text substitution. In this pack, `--schedule`
replaces the default cron expression `0 */5 * * * *` inside `TimerFunction.cs`.

A `generated` symbol computes a value. The `port` generator picks free random ports and
replaces `5100`/`7100` in `launchSettings.json`, so two generated APIs don't fight over a port.

### Conditional content inside files

Parameters can drive `#if` blocks written as comments, so the template keeps compiling as it is:

```csharp
//#if (EnableOpenApi)
builder.Services.AddOpenApi();
//#endif
```

```xml
<!--#if (IncludeTimerTrigger) -->
<PackageReference Include="Microsoft.Azure.Functions.Worker.Extensions.Timer" Version="4.3.1" />
<!--#endif -->
```

When the condition is false, the engine removes the block, marker comments included. When it is
true, it keeps the content and removes only the markers.

### Conditional files and renames: `sources`

```jsonc
"sources": [{
  "rename":    { "local.settings.template.json": "local.settings.json" },
  "modifiers": [{ "condition": "(!IncludeSampleEndpoints)", "exclude": [ "Endpoints/**" ] }]
}]
```

`exclude` drops whole files or folders when a condition is false. `rename` lets the template
store a file under a different name. The Function App template needs this because its generated
`.gitignore` ignores `local.settings.json`, and git would apply that `.gitignore` to the template
folder in *this* repo too, so the file would never be committed. Storing it as
`local.settings.template.json` avoids the problem.

By default `bin/`, `obj/` and `.template.config/` are never copied to the output.

### Post actions

```jsonc
"postActions": [{ "actionId": "210D431B-A78B-4D2F-B762-4ED3E3EA9025", ... }]  // = dotnet restore
```

This action runs `dotnet restore` on the `primaryOutputs` straight after generation, so the
project is ready to open. Other built-in actions include adding the project to a solution, and
the CLI can also run a script after generation, but that needs the user's confirmation.

---

## The template package

`Contoso.Platform.Templates.csproj` is a normal SDK project with a few settings that make it a
template pack instead of a library:

| Setting | Why |
|---|---|
| `<PackageType>Template</PackageType>` | Marks the package as a template pack. Without it, `dotnet new install` rejects the package. |
| `<IncludeBuildOutput>false</IncludeBuildOutput>` | No DLL goes in the package. The project never compiles anything. |
| `<ContentTargetFolders>content</ContentTargetFolders>` + `<Content Include="templates\**\*">` | Templates go under `content/` in the package, where the engine scans for `.template.config/`. |
| `<EnableDefaultItems>false</EnableDefaultItems>` | Stops the SDK from treating the templates' `.cs` files as this project's source. |
| `<NoDefaultExcludes>true</NoDefaultExcludes>` | **Important.** NuGet skips dot-folders by default, which means every `.template.config/`. Without this setting, the package installs successfully but contains no templates. |
| `Exclude="templates\**\bin\**;templates\**\obj\**"` | Leaves out build output from running a template in place. |

One package can hold any number of templates. Ship related templates together so teams install
one package and get them all at matching versions.

---

## The templates in this pack

### `contoso-webapi`: ASP.NET Core minimal API (.NET 10)

| Option | Default | Effect |
|---|---|---|
| `-n, --name` | folder name | Project, namespace and folder name |
| `--openapi` | `true` | Adds `Microsoft.AspNetCore.OpenApi` and maps `/openapi/v1.json` in Development |
| `--samples` | `true` | Includes `/api/items` sample endpoints, an in-memory store and a `.http` file. `false` gives an empty API |

Always included: `ProblemDetails` for every error response, an exception handler, `/health`,
and random dev ports.

### `contoso-func`: Azure Functions isolated worker (.NET 10, Functions v4)

| Option | Default | Effect |
|---|---|---|
| `-n, --name` | folder name | Project, namespace and folder name |
| `--timer` | `false` | Adds the Timer extension and a `TimerFunction` |
| `--schedule` | `0 */5 * * * *` | NCRONTAB expression for the timer (only used with `--timer`) |

Always included: an HTTP trigger using ASP.NET Core integration (`HttpRequest` →
`IActionResult`), Application Insights wiring, `host.json`, a `local.settings.json` for running
locally against Azurite, and a `.gitignore` that keeps `local.settings.json` out of source
control.

---

## Setting it up in Azure DevOps

### 1. Put the templates in a repo

Use a dedicated repo, for example `platform-templates`, or a folder in an existing one like
this. Update `templateRoot` and the `paths` filters in `pipelines/templates-ci.yml` to match
where the folder lives.

### 2. Create the feed

*Artifacts → Create Feed*

- **Name:** e.g. `platform-templates`
- **Visibility:** members of your organisation (or specific people/teams)
- **Scope:** *Project* (URL contains the project) or *Organisation*. The pipeline's `feedName`
  is `<project>/<feed>` for a project-scoped feed and just `<feed>` for an organisation-scoped
  one.
- **Upstream sources:** optional. Enabling nuget.org lets developers use one feed URL for
  everything.

### 3. Let the pipeline publish

*Feed settings → Permissions → Add users/groups*: give **`<Project> Build Service (<Org>)`** the
**Feed Publisher (Contributor)** role. If you skip this, the push step fails with
`403 Forbidden` even though the rest of the pipeline is green.

If consumers in *other* projects need to install from a project-scoped feed, also give them
(or "Project Collection Valid Users") the **Feed Reader** role.

### 4. Create the pipeline

*Pipelines → New pipeline → Azure Repos Git → (repo) → Existing YAML file* →
`experiments/azure-devops-dotnet-new-templates/pipelines/templates-ci.yml`.

| Stage | Runs on | Does |
|---|---|---|
| **Build** | every PR and `main` | Installs the .NET 10 SDK and runs `scripts/smoke-test.sh` (pack → install to a scratch cache → generate every option combination → `dotnet build -warnaserror`), then publishes the `.nupkg` as a pipeline artifact |
| **Publish** | `main` only | `NuGetAuthenticate@1` + `DotNetCoreCLI@2 push` to the feed |

Add the pipeline as a **branch policy build validation** on `main`. A template change that
breaks generated projects then can't be merged.

---

## Using the templates (consumer guide)

### One-time: authenticate to the feed

`dotnet new install` downloads through NuGet, so it uses NuGet's authentication.

1. **Install the Azure Artifacts Credential Provider** (one time per machine):
   - Windows: `iex "& { $(irm https://aka.ms/install-artifacts-credprovider.ps1) } -AddNetfx"`
   - macOS/Linux: `sh -c "$(curl -fsSL https://aka.ms/install-artifacts-credprovider.sh)"`
   - Visual Studio users usually have it already.
2. **Add the feed as a NuGet source.** Either copy [`nuget.config.example`](nuget.config.example)
   into your user-level NuGet config, or run:

   ```bash
   dotnet nuget add source \
     "https://pkgs.dev.azure.com/<org>/<project>/_packaging/<feed>/nuget/v3/index.json" \
     --name platform-templates
   ```

### Install

```bash
# --interactive allows the credential provider to prompt (device code / browser) on first use
dotnet new install Contoso.Platform.Templates --interactive

# Without adding a source first, point at the feed directly:
dotnet new install Contoso.Platform.Templates --interactive \
  --nuget-source "https://pkgs.dev.azure.com/<org>/<project>/_packaging/<feed>/nuget/v3/index.json"

# Pin a specific version:
dotnet new install Contoso.Platform.Templates::1.0.12 --interactive
```

The output lists the installed templates:

```
Template Name         Short Name      Language  Tags
--------------------  --------------  --------  -----------------------------
Contoso Function App  contoso-func    [C#]      Contoso/Azure Functions/Cloud
Contoso Web API       contoso-webapi  [C#]      Contoso/Web/WebAPI
```

### Create projects

```bash
dotnet new contoso-webapi -n Acme.Orders.Api                       # → ./Acme.Orders.Api/
dotnet new contoso-webapi -n Acme.Orders.Api --samples false       # empty API, no sample endpoints
dotnet new contoso-func   -n Acme.Orders.Worker -o src/Worker      # choose the output folder
dotnet new contoso-func   -n Acme.Orders.Worker --timer --schedule "0 0 2 * * *"

dotnet new contoso-webapi --help     # all options for a template
dotnet new list contoso              # everything tagged/named "contoso"
dotnet new contoso-func -n X --dry-run   # preview the files without writing them
```

To add the new project to an existing solution, run `dotnet sln add Acme.Orders.Api`.

In **Visual Studio** (*File → New → Project*) and **Rider** (*New Solution*), installed
templates appear next to the built-in ones. Search for "Contoso", and the bool options appear
as checkboxes.

### Stay current

```bash
dotnet new update --check-only   # is there a newer version on the feed?
dotnet new update                # update all installed template packages
dotnet new uninstall Contoso.Platform.Templates
```

Updating a template **does not change projects already generated from it**. Templates are a
starting point, not a dependency. Shared behaviour that must stay in sync belongs in a NuGet
library or a `Directory.Build.props`.

### In a pipeline (non-interactive)

To generate projects in CI, for example in a repo-scaffolding pipeline, authenticate first:

```yaml
- task: NuGetAuthenticate@1
- bash: |
    dotnet new install Contoso.Platform.Templates \
      --nuget-source "https://pkgs.dev.azure.com/<org>/<project>/_packaging/<feed>/nuget/v3/index.json"
    dotnet new contoso-webapi -n "$(serviceName)"
```

---

## Changing or adding a template (author guide)

### Edit and try a template in place

The templates are real projects, so you edit them like any other project:

```bash
dotnet run --project templates/webapi            # run the template as it is
dotnet build templates/funcapp
```

The `//#if` markers are ordinary comments, so the template keeps compiling. Keep it that way;
it's the quickest feedback loop.

### Test the generated output

```bash
./scripts/smoke-test.sh
```

The script packs the templates, installs them into a scratch template cache
(`--debug:custom-hive`, so your real `dotnet new` setup isn't touched), and generates and builds
every case. It also fails if a placeholder name survives into the output. **When you add a
parameter, add a `generate ...` line for it**: only combinations that are generated get tested.

To try the package by hand:

```bash
dotnet pack Contoso.Platform.Templates.csproj -o ./artifacts
dotnet new install ./artifacts/Contoso.Platform.Templates.0.0.0-local.nupkg   # local file works too
dotnet new contoso-webapi -n Scratch.Api -o /tmp/scratch
dotnet new uninstall Contoso.Platform.Templates
```

### Add a new template to the pack

1. Create `templates/<name>/` containing a working project. Name the project and its namespace
   with a unique placeholder, e.g. `Contoso.Worker`.
2. Add `.template.config/template.json` with a new unique `identity`, a `contoso-` prefixed
   `shortName`, and `"sourceName": "<your placeholder>"`. Copy one of the existing files as a
   starting point.
3. Add `generate` lines to `scripts/smoke-test.sh`, and add the placeholder to its leak-check
   regex.
4. Update the table in `PACKAGE.md`.

The pack project picks up anything under `templates/`, so the `.csproj` doesn't need changing.

### Keeping package versions current

Package versions inside the templates are hard-coded on purpose: generated projects should
start pinned. Dependabot and Renovate don't scan template content as a project, so bump
versions on a schedule (`dotnet list templates/webapi package --outdated`) and let the smoke
test confirm that the bumped versions still build.

---

## Versioning

The pipeline versions the package as **`<majorMinor>.<counter>`**, e.g. `1.0.0`, `1.0.1`. The
`patch` counter resets when you change `majorMinor` in the YAML.

- **Patch** (automatic): package bumps and small fixes to generated content.
- **Minor** (edit `majorMinor`): new templates or new parameters.
- **Major** (edit `majorMinor`): a template removed or renamed, a short name or parameter
  renamed, or a target framework change. Anything that breaks someone's script or muscle
  memory.

PR builds produce `…-ci.<buildId>` prerelease versions and never publish them. Feed versions
are immutable: once a version is pushed, that number can never be reused, even after the
package is deleted.

---

## Troubleshooting

| Symptom | Cause / fix |
|---|---|
| `401 Unauthorized` / `Unable to load the service index` on install | Credential provider not installed, or no `--interactive` so it couldn't prompt. In CI, run `NuGetAuthenticate@1` first. |
| Install says *success* but lists **no templates** | `.template.config/` didn't make it into the package. Check `NoDefaultExcludes` is `true` (`unzip -l *.nupkg` should show `content/templates/*/.template.config/template.json`). |
| `403 Forbidden` on the pipeline's push step | The Build Service identity doesn't have Feed Publisher on the feed (step 3 above). |
| `409 Conflict` on push | That version already exists. Feed versions are immutable. The counter normally prevents this. If you reset `majorMinor` to an older value, choose a higher one. |
| Generated project still says `Contoso.WebApi` somewhere | The text doesn't exactly match `sourceName` (case and punctuation matter). The smoke test catches this. |
| `dotnet new contoso-webapi` → *No templates found* | Not installed in this SDK's template cache. Run `dotnet new list` to check, then install. Each major SDK version keeps its own cache. |
| Short-name clash warning | Two installed packages use the same `shortName`. Keep the `contoso-` prefix. |
| New version on the feed but `dotnet new` uses the old one | Run `dotnet new update`. Installs never update themselves. |
| `local.settings.json` missing from the template in git | Expected. It's stored as `local.settings.template.json` and renamed on generation (see *Conditional files and renames*). |

---

## What's been verified

Run in this sandbox with .NET SDK 10.0.113 against nuget.org:

- `dotnet pack` produces `Contoso.Platform.Templates.<version>.nupkg` with both templates and
  their `.template.config/` folders under `content/`.
- `dotnet new install <nupkg>` registers `contoso-webapi` and `contoso-func`.
  `--help` shows the friendly `--openapi`, `--samples`, `--timer` and `--schedule` options.
- `scripts/smoke-test.sh` generates and builds with `-warnaserror` (0 warnings, 0 errors):
  - `contoso-webapi -n Acme.Orders.Api` (defaults)
  - `contoso-webapi -n Acme.Bare.Api --openapi false --samples false`: no OpenAPI package, no
    `Endpoints/`, no `.http` file, clean `Program.cs`
  - `contoso-func -n Acme.Orders.Worker`: no Timer package, no `TimerFunction.cs`
  - `contoso-func -n Acme.Sched.Worker --timer --schedule "0 0 2 * * *"`: schedule substituted
- No placeholder names remain in any generated project. Ports in `launchSettings.json` are
  randomised, and `local.settings.json` is produced from `local.settings.template.json`.

**Not verified here:** the Azure DevOps pipeline and the feed push/install (there is no Azure
DevOps organisation in this sandbox), and running the generated Function App under Functions
Core Tools.
