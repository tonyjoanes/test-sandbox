# Bicep & Azure Verified Modules (AVM)

A progressive, heavily-commented set of Bicep files: a syntax primer for
Bicep itself, then a deep dive into Azure Verified Modules (AVM) — what
they are, how to consume them, and what they actually buy you over
hand-rolled templates.

> **Accuracy note:** the module *paths* used throughout (`avm/res/storage/storage-account`,
> `avm/res/key-vault/vault`, `avm/res/network/virtual-network`,
> `avm/res/web/serverfarm`, `avm/res/web/site`) were checked directly
> against the [`Azure/bicep-registry-modules`](https://github.com/Azure/bicep-registry-modules)
> source repository while writing this. Version tags (e.g. `:0.14.3`) and
> some nested parameter shapes are **illustrative** — AVM ships new
> versions regularly, so always confirm the current version and exact
> parameter shape via VS Code's Bicep extension (IntelliSense once you type
> a module path) or the module's own README before using these for a real
> deployment. Treat this folder as a map of the territory, not a
> copy-paste-ready template set.

---

## What Is Bicep?

Bicep is a domain-specific language that compiles to ARM (Azure Resource
Manager) JSON — the same deployment engine Azure has always used. You
write Bicep; `bicep build` (or the deployment CLI, transparently)
transpiles it to ARM JSON; Azure Resource Manager deploys that JSON. Bicep
exists because hand-writing ARM JSON directly is painful — no comments, no
real loops, no type checking, deep bracket nesting for simple things.
`01-bicep-fundamentals.bicep` is a syntax primer covering parameters,
variables, resources, loops, conditions, modules, and outputs — read it
first if Bicep itself, not just AVM, is new to you.

## What Is Azure Verified Modules?

AVM is a Microsoft-led (with community contributions) initiative
publishing a catalogue of **pre-built, tested, versioned Bicep (and
Terraform) modules** for Azure resources — hosted on the public Bicep
registry (backed by the Microsoft Container Registry) so anyone can
reference them with no authentication required. Instead of every team
re-deriving "how do I stand up a secure storage account" from Azure
documentation, you consume a module someone else built, tested, and keeps
current as Azure's APIs evolve.

Two module *kinds*, both under the `avm/` registry namespace:

| Kind | Namespace | Maps to | Example |
|---|---|---|---|
| **Resource module** | `avm/res/...` | One ARM resource type, 1:1 | `avm/res/storage/storage-account` |
| **Pattern module** | `avm/ptn/...` | A reference architecture, composed from several resource modules | `avm/ptn/app-service-lza` (an app-service "landing zone" pattern) |

---

## The Examples

| # | File | What It Shows |
|---|---|---|
| 01 | [`bicep/01-bicep-fundamentals.bicep`](bicep/01-bicep-fundamentals.bicep) | Plain Bicep — params/decorators, variables, resources, loops (`for`), conditions (`if`), the `module` keyword, outputs. No AVM yet. |
| 02 | [`bicep/02-storage-account-without-avm.bicep`](bicep/02-storage-account-without-avm.bicep) | A secure storage account hand-rolled from 5 separate resource types — the private endpoint + DNS zone group wiring, diagnostic category names, and role GUID all sourced manually. |
| 03 | [`bicep/03-storage-account-with-avm.bicep`](bicep/03-storage-account-with-avm.bicep) | The exact same outcome as 02, as parameters to one `avm/res/storage/storage-account` module call — direct side-by-side contrast. |
| 04 | [`bicep/04-avm-common-interface.bicep`](bicep/04-avm-common-interface.bicep) | Three unrelated resource types (storage, Key Vault, virtual network) deployed side by side to make visible how `name`/`location`/`tags`/`lock`/`roleAssignments`/`enableTelemetry` are spelled identically across every AVM module. |
| 05 | [`bicep/05-composing-avm-modules.bicep`](bicep/05-composing-avm-modules.bicep) | Composing 5 resource modules (identity, Key Vault, storage, app service plan, web app) into one small secure web app, wired together via module outputs — plus where a pattern module would take over. |
| 06 | [`bicep/06-versioning-and-telemetry.bicep`](bicep/06-versioning-and-telemetry.bicep) | Exact semver pinning (no floating `:latest`), digest pinning for supply-chain-sensitive cases, and the `enableTelemetry` opt-out. |

### Reading Order

1. **01** if Bicep syntax itself is new.
2. **02 → 03** back to back — this pairing is the fastest way to *feel* the difference AVM makes; read 02 fully, then see 03 collapse it.
3. **04** to see the payoff generalise: the parameter vocabulary from 03 works the same way on totally different resource types.
4. **05** for a realistic multi-resource composition, and where a pattern module would go further still.
5. **06** once you're ready to actually depend on a module in a real repo — pinning and telemetry are the two things worth understanding before that.

---

## Why AVM, Concretely

- **Security/best-practice defaults, not just less typing.** 02 vs 03 isn't only about line count — 02 requires knowing that a private endpoint needs a *separate* DNS zone group resource to actually resolve, which diagnostic category names a storage account supports, and the correct built-in role GUID. Get any of those wrong and the mistake is silent (a private endpoint that "works" in the portal but never gets the right DNS record, say). The module's own test suite is what verifies that wiring — you inherit that verification instead of re-deriving it per project.
- **One interface, many resources.** Section `04`'s whole point: `name`, `location`, `tags`, `lock`, `roleAssignments`, `enableTelemetry`, `managedIdentities` show up the same way on almost every module. Learning the pattern once transfers.
- **Maintained centrally, consumed everywhere.** When an Azure API adds a new required property or deprecates an old one, that's a module version bump maintained by AVM — not N separate PRs across every team's copy-pasted Bicep.
- **Reproducible by design.** Modules are published under exact version tags with no `:latest` alias to accidentally float on (see `06`) — what deploys today is what deploys again from the same source next year, until you deliberately bump the pin.
- **Discoverable.** The [AVM Module Index](https://aka.ms/AVM) and VS Code's Bicep extension (type `br/public:avm/` and let IntelliSense complete it) are a single place to find "is there already a module for this" instead of every team maintaining its own private module library from scratch.
- **Bicep and Terraform in parallel.** AVM publishes matching module sets for both languages, which matters for orgs that use both — the same architectural conventions and parameter vocabulary carry across.

---

## Consuming a Module, Practically

1. In VS Code with the Bicep extension, type `module foo 'br/public:avm/res/` — IntelliSense lists available resource-module paths and, once you pick one, available versions.
2. Read the module's own README (linked from its entry on [aka.ms/AVM](https://aka.ms/AVM), or browse `Azure/bicep-registry-modules` on GitHub directly) for its full parameter list, required vs optional inputs, and usage examples — this repo's files intentionally show only a subset of each module's parameters to keep the focus on the *pattern*, not a complete reference.
3. Pin an exact version (`06` covers why). Run `bicep restore` to cache it locally, or let your deployment pipeline do so implicitly.
4. Deploy as normal (`az deployment group create`, or from an Azure Pipelines/GitHub Actions pipeline) — an AVM module reference behaves exactly like a local module file once resolved; nothing else about your deployment process changes.

## Related

See `experiments/azure-yaml-pipelines-examples/` for how a Bicep deployment like these typically gets wrapped in an Azure Pipelines YAML stage (an `AzureCLI@2` or `AzureResourceManagerTemplateDeployment@3` task calling into a `.bicep` entry point).
