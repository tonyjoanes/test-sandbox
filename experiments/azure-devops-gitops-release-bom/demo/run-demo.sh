#!/usr/bin/env bash
# A whole release, end to end, on your machine: no Azure and no Azure DevOps.
#
# It copies release-repo/ into a scratch git repo (.demo/repo), then plays the part of each
# person and pipeline in turn: a release manager promotes staging to live on a branch, the PR
# validation runs, the PR is merged, and the live sync plans, raises a change request and
# applies wave by wave (as a dry run, since there's no Azure here).
#
#   ./demo/run-demo.sh
#
# Needs the .NET 10 SDK and git.
set -euo pipefail

here=$(cd "$(dirname "$0")/.." && pwd)
work="$here/.demo"
rm -rf "$work" && mkdir -p "$work"

dotnet build "$here/src/Relctl" --nologo --verbosity quiet --output "$work/bin" >/dev/null
relctl() { dotnet "$work/bin/Relctl.dll" "$@" --local-feed "$here/sample-feed"; }
step() { printf '\n\033[1m━━ %s\033[0m\n' "$*"; }
git_() { git -c user.name=demo -c user.email=demo@example.invalid "$@"; }

cp -r "$here/release-repo" "$work/repo"
cd "$work/repo"
git init --quiet --initial-branch main && git add . && git_ commit --quiet -m "The release repo as it is today"

# What's really deployed in live. In Azure this is read from tags on each resource.
cat > "$work/live-state.json" <<'EOF'
{ "live": { "customers-api": "1.1.4", "notifications-func": "3.1.0", "orders-api": "1.7.3",
            "orders-func": "1.3.0", "payments-api": "2.2.7", "portal-web": "5.0.2" } }
EOF

step "1. Live matches its manifest"
relctl diff --env live --state-file "$work/live-state.json"

step "2. Release manager: promote what's in staging to live, on a branch"
git checkout --quiet -b release/2026.11.3
relctl promote --from staging --to live
cat >> environments/live.yaml <<'EOF'

pauses:
  - before: payments-api
    release: 2026.11.3
    instructions: |
      Run the "customers-billing-backfill" WebJob on app-customers-prd and wait for it to finish.
      The "Customers without billing profile" tile on the Customers dashboard must read 0.
EOF
git --no-pager diff --stat && git --no-pager diff -U0 environments/live.yaml | tail -n +5

step "3. A mistake the PR check catches: orders-api 1.8.2 is only in uat, not staging"
cp environments/live.yaml "$work/live.yaml.good"
relctl bump --env live --component orders-api --version 1.8.2
relctl validate --base main || echo "(exit $? — the PR can't merge like this)"
cp "$work/live.yaml.good" environments/live.yaml

step "4. PR validation: the real change passes, and the PR shows what merging it would do"
relctl validate --base main
relctl plan --env live --base main --out "$work/pr-plan" >/dev/null
cat "$work/pr-plan/plan.md"
git_ commit --quiet -am "Release 2026.11.3 to live"

step "5. Approved and merged. The live sync pipeline starts: plan against what's really deployed"
git checkout --quiet main && git merge --quiet --no-ff --no-edit release/2026.11.3
relctl plan --env live --state-file "$work/live-state.json" --out "$work/plan" | head -3

step "6. Change request body, from the plan (the pipeline posts it to ServiceNow)"
relctl change-request --plan "$work/plan/plan.json" --out "$work/change-request.json"

step "7. Apply, one job per part, exactly as the pipeline runs it (dry run: nothing in Azure here)"
relctl apply --plan "$work/plan/plan.json" --pre-sync --dry-run --state-file "$work/live-state.json"
for wave in 1 2 3 4; do
  [[ $wave == 2 ]] && echo -e "\n⏸  The pipeline pauses here for the backfill (Manual Validation), then carries on."
  relctl apply --plan "$work/plan/plan.json" --wave $wave --dry-run --state-file "$work/live-state.json" | grep -vE '^\[dry run\] would (check|wait)' || true
done
relctl apply --plan "$work/plan/plan.json" --post-sync --dry-run --state-file "$work/live-state.json"

step "8. After a real run, the resource tags hold the new versions, and diff is clean again"
cat > "$work/live-state.json" <<'EOF'
{ "live": { "customers-api": "1.2.0", "notifications-func": "3.1.0", "orders-api": "1.8.0",
            "orders-func": "1.4.0", "payments-api": "2.3.1", "portal-web": "5.1.0" } }
EOF
relctl diff --env live --state-file "$work/live-state.json" --fail-on-drift
git tag live/2026.11.3

step "9. The bill of materials for live, as released (CycloneDX)"
relctl bom --env live --out "$work/live-2026.11.3.cdx.json"
grep -E '"(name|version)"' "$work/live-2026.11.3.cdx.json" | head -8

step "10. Something's wrong with orders-api 1.8.0: roll back just that one"
git checkout --quiet -b rollback/orders-api
sed -i 's|^  orders-api: 1.8.0$|  orders-api: { version: 1.7.3, rollback: "INC0012345 checkout errors after 1.8.0" }|' environments/live.yaml
sed -i 's|^release: 2026.11.3$|release: 2026.11.3-rb1|' environments/live.yaml
sed -i '/^pauses:/,$d' environments/live.yaml
relctl validate --base main || echo "(exit $?: orders-func 1.4.0 and portal-web 5.1.0 need orders-api >= 1.8.0, so they must roll back too)"
sed -i 's|^  orders-func: 1.4.0$|  orders-func: { version: 1.3.0, rollback: "INC0012345 needs orders-api 1.8.0" }|' environments/live.yaml
sed -i 's|^  portal-web: 5.1.0$|  portal-web: { version: 5.0.2, rollback: "INC0012345 needs orders-api 1.8.0" }|' environments/live.yaml
relctl validate --base main
relctl plan --env live --base main --out "$work/rollback-plan" | sed -n '5,10p'

step "Done. Everything is in $work"
