# RFC: New or extended shared module

File this as an issue **before** writing Bicep. The point is to settle the contract — what's
required, what's extensible, what's off-limits — while it's a paragraph to change, not a PR
with working code attached that nobody wants to rework.

Delete this line and everything above it before submitting.

---

**Module name:**

**Resource type(s) it wraps:**

**Requesting team:**

**Target tier** (pick one — see `../README.md#three-tier-ownership`):
- [ ] `foundation/` — I'm proposing this as a platform-owned module (expect platform to take
      ownership going forward; higher bar, expect more review rounds)
- [ ] `contributed/` — I'm proposing this as a squad-owned module living in the shared catalog
      (my team keeps ownership; lighter review, still gated)

## Why does this need to exist?

- Is there an AVM module for this resource type already? Link it.
- Does an existing `foundation/` or `contributed/` module already cover this need? If so, why
  doesn't extending it (see the extension patterns in `../examples/extending-a-module.md`) work
  instead of a new module?

## Proposed contract

| Parameter | Required or optional? | Type | Why |
|---|---|---|---|
| e.g. `name` | Required | `string` | no sensible default |
| e.g. `network` | Required | `networkPosture` (shared) | reuse the org-wide contract |
| e.g. `sku` | Optional | closed allow-list | which SKUs, and why those specifically |
| e.g. `advanced` | Optional | sealed escape hatch | which extra AVM properties, and why these specifically |

## Sealed vs unsealed decisions

For every object-typed parameter above: is it sealed? If unsealed, what's the reason (mirrors
an AVM surface that changes often; genuinely squad-specific and nothing downstream depends on
a fixed shape)? See `shared/types.bicep` for the reasoning this project already applies.

## Ownership after merge

Who is on the hook for this module long-term — reviewing extension requests, bumping the
pinned AVM version, fixing it when AVM ships a breaking change? For `contributed/` modules
this is the requesting team, not platform, unless/until the module is promoted (see
`contributed-module-checklist.md`).

## Reviewers requested

- [ ] `@contoso/platform-engineering` (required for `foundation/`)
- [ ] `@contoso/module-reviewers` (required for `contributed/`)
