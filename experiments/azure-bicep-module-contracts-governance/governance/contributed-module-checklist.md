# New contributed module — checklist

Attach to any PR under `contributed/`. Lighter than `module-contract-checklist.md`
(the `foundation/` checklist) — a contributed module is squad-owned, so the review is about
"does this fit the shared contract vocabulary and not surprise anyone," not "does platform want
to own this forever."

## Before the PR

- [ ] An RFC (`rfc-template.md`) was filed and linked in this PR, targeting the `contributed/`
      tier.
- [ ] No existing `foundation/` or `contributed/` module already covers this — checked the
      catalog table in `../README.md`.

## Contract fit

- [ ] Required parameters reuse the shared types (`mandatoryTags`, `networkPosture`) from
      `shared/types.bicep` rather than reinventing an equivalent shape. If this module
      genuinely needs a tag or network shape the shared types don't cover, that's itself an RFC
      against `shared/`, not a local reinvention here.
- [ ] Optional parameters follow the same required/allow-list/sealed pattern used in
      `foundation/` modules — no bare `object` params standing in for "whatever AVM accepts."
- [ ] Wraps a pinned AVM module version, not a raw `resource` block, unless the RFC explains
      why no AVM module exists for this resource type.

## Ownership

- [ ] The PR description names the owning team and an on-call/contact path for extension
      requests — a contributed module without a clear owner is the one thing this tier can't
      tolerate, since platform isn't implicitly picking up that slack.
- [ ] `governance/CODEOWNERS.example` (or the real `CODEOWNERS`) has an entry routing changes
      under this module's path to the owning team, not just the generic `@contoso/module-reviewers`
      pool.

## Promotion path (informational, not a gate)

A contributed module that ends up used by several unrelated squads is a candidate for
promotion to `foundation/` — same code, ownership transfers to platform, and its optional
surface usually gets tightened in the process (widely-used modules attract more scrutiny on
their allow-lists than single-team ones). Promotion is its own RFC; nothing here blocks on it.
