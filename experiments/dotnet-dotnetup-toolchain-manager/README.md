# dotnetup — a Toolchain Manager for .NET

An experiment that exercises [`dotnetup`](https://devblogs.microsoft.com/dotnet/introducing-dotnetup/),
Microsoft's new `rustup`-style manager for user-level .NET SDKs and runtimes, and **checks each
claimed benefit with an assertion** instead of just describing it. Three sample "repositories"
with different `global.json` policies are installed, run side by side, updated, and cleaned up
from one dotnetup-managed root. A script prints pass/fail for about 30 checks.

> **Status:** `dotnetup` is in **public preview**. Commands and behaviour can change between
> preview builds. Everything below was checked against the docs in
> [`dotnet/sdk` → `documentation/general/dotnetup`](https://github.com/dotnet/sdk/tree/main/documentation/general/dotnetup)
> and the source on the `release/dnup` branch (October 2026). If something has drifted, those
> win over this README.

> **Where it runs:** the sandbox this repo is edited from can't reach the .NET download hosts,
> so the script was only dry-run there against a mock `dotnetup`, to check its control flow.
> It **really runs** in GitHub Actions
> ([`.github/workflows/dotnetup-experiment.yml`](../../.github/workflows/dotnetup-experiment.yml)),
> or on your own Linux or macOS machine. See [Running It](#running-it).

---

## The Problem It Solves

Before dotnetup, getting the right .NET SDK onto a machine meant choosing one of these:

| Method | Pain point |
|---|---|
| Machine-wide installers / `apt` / `brew` / Visual Studio | Needs admin. One shared location for every user. Hard to run several versions side by side. Previews and dailies are awkward. |
| `dotnet-install.sh` / `.ps1` | No admin needed, but it's fire-and-forget. Nothing records what was installed or why. There's no `update`, no uninstall, and no cleanup, so you re-run the script and delete folders by hand. |

dotnetup takes the user-level, no-admin approach of `dotnet-install` and adds what that script
never had: **it remembers what you asked for**. Then it can update, deduplicate, and garbage-collect
on your behalf.

---

## Mental Model: Requirements vs Installations

This is the one idea to take away. dotnetup stores two separate things in its manifest:

```
  REQUIREMENTS (what you asked for)            INSTALLATIONS (what's on disk)
  ──────────────────────────────────           ──────────────────────────────
  SDK 8.0       ← legacy-billing/global.json ─→ SDK 8.0.4xx
  SDK 10.0.1xx  ← orders-api/global.json     ─┐
  SDK 10.0.1xx  ← explicit `sdk install`     ─┴→ SDK 10.0.1yy   (one copy, two requirements)
  SDK 10.0.100  ← payments-pinned/global.json ─→ SDK 10.0.100   (exact = pinned, never updated)
```

- **A requirement** is a channel (`latest`, `lts`, `10`, `10.0`, `10.0.1xx`, `preview`, `daily`)
  or an exact version. It comes from the command line (`source: explicit`) or from a
  `global.json` (`source: globaljson`, with the file's path recorded).
- **An installation** is a concrete version on disk.
- `dotnetup update` re-resolves every channel requirement. Exact versions are skipped.
- `dotnetup ... uninstall` removes a *requirement*. Then garbage collection deletes only the
  files that **no remaining requirement** needs.

### How `global.json` becomes a requirement

With no channel argument, `dotnetup sdk install` reads the nearest `global.json` and maps
`rollForward` to a channel it can update:

| `rollForward` | Stored requirement for `sdk.version: 10.0.103` |
|---|---|
| omitted / `latestPatch` | `10.0.1xx` |
| `latestFeature` | `10.0` |
| `latestMinor` | `10` |
| `latestMajor` | `latest` |
| `disable`, `patch`, `feature`, `minor`, `major` | `10.0.103` (pinned) |

---

## The Experiment

### Sample repos ([`repos/`](repos/))

Each repo is a tiny console app that prints the runtime it actually ran on.

| Repo | `global.json` | Expected requirement | Represents |
|---|---|---|---|
| `legacy-billing` | `8.0.100`, `latestFeature` | `8.0` | An older LTS service you still maintain |
| `orders-api` | `10.0.100`, `latestPatch` | `10.0.1xx` | A current service that wants security patches |
| `payments-pinned` | `10.0.100`, `disable` | `10.0.100` (exact) | A service that must build on one exact SDK |

### What each phase proves ([`scripts/run-experiment.sh`](scripts/run-experiment.sh))

| Phase | What it does | Benefit it demonstrates |
|---|---|---|
| 0 · Setup | Uses a throwaway `DOTNET_DOTNETUP_DATA_DIR`. Snapshots the machine-wide `dotnet` and shell profiles. | Safe to try. Your real setup is never touched. |
| 1 · Install from `global.json` | Runs `dotnetup sdk install` with **no arguments** in each repo, then asserts the derived channel. | The repo declares what it needs, so nobody types SDK versions by hand. |
| 2 · Side by side | Runs `dotnetup dotnet -- --version` and `run` in each repo. | Three SDKs (8.0, latest 10.0.1xx, exact 10.0.100) live in one root. Each repo gets its own SDK with no `PATH` juggling. |
| 3 · Shared installs | Adds an explicit `10.0.1xx` requirement that `orders-api` already satisfies. | The requirement is tracked, but nothing new is downloaded. |
| 4 · Updates | Runs `dotnetup update`, then `sdk install --update-global-json` in `orders-api`. | Updating is one command. Exact pins survive it. `global.json` can be bumped to the newest patch automatically, keeping `rollForward` and formatting. |
| 5 · Uninstall + GC | Removes the explicit `10.0.1xx` requirement, then the `8.0` one. | Shared files stay while anything needs them, and the 8.0 SDK's disk space is reclaimed. `legacy-billing` then *fails* to find an SDK, which proves the SDK really came from dotnetup. |
| 6 · Isolated root | Installs into `--install-path .dotnet` with a separate `--manifest-path`. | The CI / repo-local pattern: fully separate state with the same tool. |
| 7 · Untouched | Re-checks the machine-wide SDK list, `which dotnet`, and shell profile hashes. | User-level and non-invasive. No sudo, and nothing under `/usr/share/dotnet`. |

At the end, `results/summary.md` holds the checks table, measurements (install times, disk used and
reclaimed, resolved versions), the `global.json` diff, and the final `dotnetup list`. In Actions
that summary appears on the run page, and the whole results folder is uploaded as an artifact.

---

## Running It

### In GitHub Actions (no setup needed)

The workflow runs automatically on PRs that touch this folder. To run it on demand, go to
**Actions → dotnetup Experiment → Run workflow**. Re-running it after a .NET Patch Tuesday
is an easy way to watch the channel requirements resolve to newer versions.

### On your own machine (Linux or macOS)

```bash
# 1. Get dotnetup (installs to ~/.dotnetup; doesn't touch PATH or any .NET)
curl -fsSL https://aka.ms/dotnetup/get-dotnetup.sh | bash
export PATH="$HOME/.dotnetup:$PATH"

# 2. Run the experiment (needs jq). Downloads ~3 SDKs into a temp dir.
experiments/dotnet-dotnetup-toolchain-manager/scripts/run-experiment.sh
```

If `dotnetup` isn't on `PATH`, the script downloads it into its work directory. The work
directory is kept afterwards so you can inspect it, and the script prints its path. Delete it
when you're done. Because the script sets `DOTNET_DOTNETUP_DATA_DIR`, your own dotnetup
manifest, config, and default root are never read or written.

### Trying it by hand

The commands worth knowing, roughly in the order you'd meet them:

```bash
dotnetup init                          # interactive first-time setup (channel + access mode)
dotnetup install                       # = sdk install; reads nearest global.json, else `latest`
dotnetup sdk install lts 10.0.1xx      # several requirements at once
dotnetup runtime install aspnetcore@10.0
dotnetup list                          # requirements + installations, grouped by root
dotnetup list --format json --no-verify
dotnetup update                        # re-resolve every channel; exact pins skipped
dotnetup sdk update --update-global-json
dotnetup sdk uninstall 8.0 --source globaljson
dotnetup dotnet -- --version           # run the managed dotnet without touching PATH
eval "$(dotnetup env script)"          # activate for this shell only
dotnetup env set shell | none          # persist (or remove) shell-profile wiring
dotnetup env clear                     # undo all environment changes (keeps SDKs)
```

---

## Benefits, Summarised

| | dotnetup | Installers / package managers | `dotnet-install` scripts |
|---|---|---|---|
| Scope | User | Usually machine | Any folder |
| Admin rights | No (except Windows `everywhere` mode) | Usually | No |
| Remembers what was installed and why | **Yes** | Varies | No |
| `update` | **One command** | Varies | Re-run script |
| Removes unused versions safely | **Yes (ref-counted GC)** | Varies | No |
| Reads `global.json` | **Yes, and can write it back** | No | `--jsonfile` only |
| Preview / daily builds | Yes | No | Yes |
| Same commands on Windows, macOS, Linux | Yes | No | Two scripts |
| Download verification | Signed release manifest → SHA-512 per archive | Platform-dependent | SHA-512 |
| Support status | **Public preview** | GA | GA |

(Comparison adapted from the official
[When to use dotnetup](https://github.com/dotnet/sdk/blob/main/documentation/general/dotnetup/choose-dotnetup.md) page.)

---

## Caveats Worth Knowing Before Adopting It

- **It's preview.** For production machines Microsoft still points to the installers, package
  managers, or Visual Studio.
- **Windows `everywhere` mode is the default on Windows, and it bites.** It edits the
  *system* `PATH` (UAC prompt) so the user-level root wins over `Program Files\dotnet`. From then
  on, SDKs and runtimes that only Visual Studio or the machine-wide installers provided **stop
  resolving**. Projects fail to build, and framework-dependent apps fail with "You must install
  or update .NET". Either accept setup's offer to migrate (`--migrate-from-system`) or pick
  `shell`/`none` mode.
- **`dotnetup dotnet` only knows the default root.** It doesn't pick up custom
  `--install-path` roots. For those, use `<root>/dotnet` directly or
  `dotnetup env script --dotnet --dotnet-install-path <root>`.
- **`global.json` quirks in this preview:**
  - `allowPrerelease` doesn't affect the derived channel.
  - The first `sdk.paths` entry is treated as a literal path, so `$host$` isn't understood.
  - A malformed `global.json` is a hard failure.
  - After editing `global.json`, re-run `dotnetup sdk install` in that repo. Otherwise the next
    `update` can still process the old requirement.
- **`--source globaljson` uninstall is per root, not per repo.** It removes *every* matching
  globaljson requirement in that root.
- **Shared lock:** installs, updates, and uninstalls serialise through one process-wide lock,
  even across separate manifests. Parallel CI jobs on one machine will queue.
- **Updating dotnetup itself:** re-run the `get-dotnetup` script. The current docs list no
  self-update command. Some third-party write-ups mention one, but it isn't in the CLI reference.
- **Online revocation checks:** signature verification needs network access to certificate
  revocation endpoints. Locked-down networks can fail installs that are otherwise valid.

## Known Gaps in This Experiment

- **Not executed in the authoring sandbox.** Download hosts are blocked there. It was validated
  only against a mock `dotnetup`, and the Actions run is the real test. If a preview build changes
  a command or the `list --format json` shape (`installSpecs[].versionOrChannel/source/globalJsonPath/installRoot`,
  `installations[].component/version/installRoot`), a check will fail loudly rather than pass
  silently.
- **`update` can't be shown moving a channel on demand.** That needs a newer release to appear
  between two runs. Phase 4 shows the same resolution through `--update-global-json`, which bumps
  `10.0.100` to the newest `10.0.1xx` patch. Re-run the workflow after a release to see
  `dotnetup update` move a channel.
- **Linux only in CI.** The script is written to be macOS-compatible, but it has only been dry-run
  on Linux. Windows (PowerShell, `everywhere` mode) isn't covered.
- **Runtimes aren't exercised.** `dotnetup runtime install` follows the same requirement and GC
  model as SDKs.
