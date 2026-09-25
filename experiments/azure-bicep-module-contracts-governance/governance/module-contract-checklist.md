# New/changed foundation module — contract checklist

Attach this checklist to any PR under `foundation/` or `shared/`. It exists so contract
reviews check the same things every time instead of relying on a reviewer remembering the
whole list from memory.

## Required vs optional surface

- [ ] Every parameter that must be an explicit, per-deployment decision has **no default**.
- [ ] Every parameter with a default is either a closed type (`@allowed([...])`, a union, or
      an exported allow-list type like `approvedStorageSku`) or is genuinely free-form on
      purpose — and the description says which, and why.
- [ ] No parameter accepts a plain `object` unless it is either (a) explicitly unsealed and
      intentionally open-ended (document why), or (b) a `@sealed()` user-defined type naming
      exactly the extra properties squads may set.

## Composition, not reimplementation

- [ ] The module wraps an AVM resource/pattern module (`br/public:avm/...`) rather than
      declaring the raw `resource` block directly, unless there is no matching AVM module —
      in which case say so in the PR description.
- [ ] The AVM module version is pinned to an exact version, not a floating tag.
- [ ] Bumping the pinned AVM version is its own PR, reviewed on its own, not bundled silently
      into an unrelated contract change.

## Output surface

- [ ] Outputs are limited to what a real, named consumer needs today — no speculative outputs
      "in case someone needs them later."
- [ ] No output can leak a secret (connection string, key, credential). If a consumer needs
      one, it goes through Key Vault reference, not a module output.

## Governance wiring

- [ ] `shared/` and `foundation/` paths are covered by CODEOWNERS for the platform group.
- [ ] `bicepconfig.json` linter rules and the PSRule baseline both still pass.
- [ ] A `what-if` was run against a sandbox subscription and the diff was read, not just
      "the pipeline stage went green."

## Backward compatibility

- [ ] If this changes an existing contract (removes a parameter, narrows an allow-list,
      renames an output), every known `workload/` consumer has been checked against the
      change, or a deprecation window has been communicated.
