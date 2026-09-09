# Adopting shared Bicep modules across multiple teams

A practical rollout plan for the pattern demonstrated in this repo: shared modules with a
contract teams must meet, and a bounded way to extend them, as more squads start authoring
against the catalog instead of just consuming it.

Read `README.md` first if you haven't — this doc assumes you know what required/optional/
sealed parameters and the foundation/contributed/workload split mean.

---

## 1. One registry, versioned, teams pin exact versions

Publish everything under `foundation/` and `contributed/` to a single private module registry
— an Azure Container Registry configured as a Bicep registry (`br/platform:...` in
`bicepconfig.json`). Don't let teams reference modules by relative path or git URL once more
than one team is consuming them; a registry with real versions is what makes the next two
rules possible.

Semver the contract, not just the code:

| Change | Version bump | Example |
|---|---|---|
| Docs, internal refactor, no contract change | Patch | Tidying up the AVM call, same params in/out |
| New optional parameter (with a default) added | Minor | Widening a sealed escape hatch (example 2 in `examples/extending-a-module.md`) |
| Existing optional parameter's allow-list widened | Minor | Adding a SKU (example 3) |
| Required parameter added, or an existing parameter's meaning changes | Major | Anything that breaks an existing caller without a code change on their side |
| Parameter or output removed | Major | — |

Teams pin an exact version (`br/platform:storage-account:1.4.0`, not `:1.*` or `:latest`).
Bumping is a deliberate, reviewed change **on the consuming side** — this is what stops
"platform shipped a change and my prod deployment silently picked it up." Publish a changelog
per module (a `CHANGELOG.md` next to each `main.bicep` is enough) so consumers know what a
version bump actually changed before they take it.

## 2. Three-tier ownership, not two

The reference repo already had `foundation/` (platform-owned) and `workload/` (squad-owned,
private to one team). Once multiple teams want to *contribute to the shared catalog itself*,
add the middle tier: `contributed/`.

| | `foundation/` | `contributed/` | `workload/` |
|---|---|---|---|
| Who authors it | Platform | The requesting squad | The requesting squad |
| Who owns it long-term | Platform | The requesting squad (until promoted) | The requesting squad |
| Lives in the shared registry? | Yes | Yes | No — private to the team's own repo |
| Review gate | Platform, mandatory | A rotating platform+squad review pool | Normal team review |
| RFC required first? | Yes | Yes | No |
| Can loosen the org-wide contract (`shared/types.bicep`)? | Only via its own reviewed PR | No — declares closed types locally instead | No |

`contributed/` is what actually answers "teams want to use the shared modules more
themselves": it's a real path for a squad to add a module the whole org benefits from, without
platform having to author or indefinitely own everything in the catalog. See
`contributed/service-bus-namespace/main.bicep` for a full worked example, and
`governance/contributed-module-checklist.md` for the (lighter than foundation's) review bar.

A module that a `contributed/` team keeps extending and that other squads start depending on is
a promotion candidate — same code, ownership moves to platform, and its allow-lists usually get
tightened in the process (wide usage attracts more scrutiny than single-team usage). Promotion
is its own RFC; there's no automatic threshold, and rushing it just moves ownership before the
contract has actually been tested by multiple consumers.

## 3. RFC before code

Before anyone writes Bicep for a new shared module — `foundation/` or `contributed/` — file the
one-page RFC (`governance/rfc-template.md`): what resource, what's required, what's optional,
what's sealed and why, who owns it. This catches contract disagreements (is `environment` an
enum of four values or six? does this need a `subnetResourceId` at all?) while they're a
sentence to change, not a finished PR nobody wants to rework.

```
Idea → RFC issue (contract sketch, target tier, owner) → review/agreement
     → PR (module + tests + docs) → automated gate (build/lint/PSRule/what-if)
     → human review (platform for foundation/, review pool for contributed/) → merge → publish
```

Skipping straight to a PR is the single most common way this kind of process gets adversarial:
a reviewer rejecting finished code reads as gatekeeping; a reviewer negotiating a one-paragraph
contract sketch reads as collaboration. Same review, very different experience for the squad.

## 4. Give teams a menu, not a wall

The single biggest lever for adoption without friction is making sure teams know **which** of
the extension patterns applies to their request before they open a PR. `examples/extending-a-
module.md` is written to be handed directly to a squad: "you want X, that's pattern 2/3/4/5,
here's a worked example, here's who reviews it." Most requests should resolve at pattern 1 or 2
(already exposed, or a small addition to a module's own sealed type) — if you're seeing a lot
of pattern 3 (allow-list widening) or teams going straight to raw AVM calls in `workload/`
because the catalog doesn't cover their need, that's a signal the foundation/contributed
contracts are too narrow for how the org actually builds, not that teams need more discipline.

## 5. Discoverability

A catalog nobody can find gets reinvented instead of reused. Keep (or generate, via
`bicep-docs`/PSDocs from module metadata) a table of every published module: name, tier, owner,
current version, and a one-line summary of what's required vs extensible. Put it at the top of
this repo's README so it's the first thing anyone lands on — `README.md`'s Files table is the
minimal version of this; at real scale, generate it from the registry rather than hand-maintain
it, since a hand-maintained catalog drifts the first time someone forgets to update it.

## 6. Governance cadence

The allow-lists and sealed types are only as good as how often someone revisits them.
Put a recurring (quarterly is reasonable) review on the platform team's calendar: which
`contributed/` modules have grown enough usage to be promotion candidates, which allow-lists
have accumulated enough widening RFCs that the underlying policy question should be revisited
directly instead of patched value-by-value, which foundation modules haven't picked up an AVM
version bump in too long. Skipping this is how a contract that started well-scoped calcifies —
and a calcified contract is what pushes teams toward deploying outside the pipeline entirely,
which is worse than any amount of allow-list sprawl.

## 7. Rollout sequencing

Don't mandate this catalog org-wide on day one — the contract hasn't been tested against real
usage yet, and a rigid untested contract is exactly what teaches people to route around
governance.

1. **Pilot** with one or two willing teams on `foundation/` modules for their highest-value
   resource types. Expect the sealed escape hatches to be wrong-sized at first; that's the
   point of a pilot.
2. **Tighten** based on what pilot teams actually needed to extend — most first-draft sealed
   types are either too narrow (constant PRs for small additions) or accidentally too wide
   (an `object` param nobody sealed). Both are visible within a few real extension requests.
3. **Open the `contributed/` tier** once the foundation contract and review process feel
   settled, so squads have a real, lightweight path to add modules the platform team hasn't
   gotten to.
4. **Mandate** — via Azure Policy denying resources deployed outside the tracked pipeline, not
   before — only once the catalog actually covers what most teams need day-to-day. Mandating
   earlier than that just converts "the catalog doesn't cover my case" into "I'm blocked,"
   which is the fastest way to lose the teams you need to keep contributing to it.

## 8. What to avoid

- **Sealing the wrong things.** Seal what downstream tooling depends on having a fixed shape
  (tags, anything joined on in a dashboard); leave unsealed what's meant to grow with the
  underlying AVM module's own surface (see the reasoning in `shared/types.bicep`). Sealing
  everything "to be safe" just relocates every future request into a platform-review queue.
- **Letting `workload/` modules call AVM directly** for a resource type a `foundation/` or
  `contributed/` module already covers. Nothing in Bicep stops this structurally — it's caught
  by `ci/ps-rule/ps-rule.yaml` and review, not the compiler. If it's happening, the catalog is
  probably missing something teams need, not that they're being careless.
- **Treating `contributed/` review as equivalent to `foundation/` review.** The whole point of
  the middle tier is a lighter bar with real ownership attached. Making it as heavy as
  `foundation/` just recreates the platform bottleneck one directory over.
- **Policy as the primary control.** Azure Policy (layer 5 in `README.md`'s governance table)
  should be catching drift and out-of-band changes, not doing the job the type system and CI
  gate already do for anything that goes through this pipeline. If policy denials are the main
  thing teams hear from, the earlier layers aren't catching enough.
