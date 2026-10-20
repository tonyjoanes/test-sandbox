#!/usr/bin/env bash
# run-experiment.sh — exercises dotnetup end to end and checks each claimed benefit with an
# assertion, rather than just printing output and hoping someone reads it.
#
# Everything runs inside a throwaway work directory:
#   - DOTNET_DOTNETUP_DATA_DIR points dotnetup's manifest, config, and default install root there,
#     so the experiment never reads or changes your real dotnetup state.
#   - The sample repos are copied there first, because phase 4 rewrites a global.json on purpose.
#   - No `dotnetup init` / `dotnetup env set` is run, so shell profiles and PATH are left alone —
#     every .NET command goes through `dotnetup dotnet`, which sets PATH/DOTNET_ROOT for one
#     child process only. Phase 7 checks that claim.
#
# Usage:
#   scripts/run-experiment.sh                 # downloads dotnetup into the work dir if not on PATH
#   WORK_DIR=/tmp/x scripts/run-experiment.sh # use a specific work dir
#   RESULTS_DIR=out scripts/run-experiment.sh # write the report somewhere other than <work>/results
#
# Needs: bash, curl, jq. Network access to aka.ms and builds.dotnet.microsoft.com (plus
# nuget.org for `dotnet run`). Linux or macOS. Exit code is non-zero if any check fails.

set -uo pipefail

HERE=$(cd "$(dirname "$0")" && pwd)
EXP_ROOT=$(dirname "$HERE")

if [ -n "${WORK_DIR:-}" ]; then
    mkdir -p "$WORK_DIR"
    WORK=$(cd "$WORK_DIR" && pwd -P)
else
    # pwd -P: dotnetup records real paths (on macOS /tmp is a symlink to /private/tmp).
    WORK=$(cd "$(mktemp -d "${TMPDIR:-/tmp}/dotnetup-experiment.XXXXXX")" && pwd -P)
fi
RESULTS=${RESULTS_DIR:-$WORK/results}
mkdir -p "$RESULTS"

export DOTNET_DOTNETUP_DATA_DIR="$WORK/data"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
DEFAULT_ROOT="$DOTNET_DOTNETUP_DATA_DIR/dotnet"
REPOS="$WORK/repos"

# ── Reporting helpers ─────────────────────────────────────────────────────────────────────────

PASSED=0
FAILED=0
CHECKS_MD="$RESULTS/checks.md"
FACTS_MD="$RESULTS/facts.md"
: > "$CHECKS_MD"
: > "$FACTS_MD"

phase() {
    CURRENT_PHASE="$1"
    printf '\n\033[1;36m━━ %s ━━\033[0m\n' "$1"
}

# check <description> <command...> — runs the command, records PASS/FAIL against the current phase.
check() {
    local desc="$1"; shift
    if "$@"; then
        PASSED=$((PASSED + 1))
        printf '  \033[32m✔ PASS\033[0m %s\n' "$desc"
        printf '| %s | ✅ | %s |\n' "$CURRENT_PHASE" "$desc" >> "$CHECKS_MD"
    else
        FAILED=$((FAILED + 1))
        printf '  \033[31m✘ FAIL\033[0m %s\n' "$desc"
        printf '| %s | ❌ | %s |\n' "$CURRENT_PHASE" "$desc" >> "$CHECKS_MD"
    fi
}

# fact <label> <value> — a measured value worth reporting, not a pass/fail.
fact() {
    printf '  • %s: %s\n' "$1" "$2"
    printf '| %s | %s |\n' "$1" "$2" >> "$FACTS_MD"
}

# run <log-name> <command...> — runs a command, tees output to results/<log-name>.log, returns its
# exit code. Prints the command first so the console reads like a walkthrough.
run() {
    local name="$1"; shift
    printf '  \033[2m$ %s\033[0m\n' "$*"
    "$@" 2>&1 | tee "$RESULTS/$name.log" | sed 's/^/    /'
    return "${PIPESTATUS[0]}"
}

now() { date +%s; }
eq() { [ "$1" = "$2" ]; }
matches() { printf '%s' "$1" | grep -Eq "$2"; }
disk_mb() { du -sk "$1" 2>/dev/null | awk '{printf "%.0f", $1/1024}'; }
file_hash() { if [ -f "$1" ]; then cksum < "$1"; else echo absent; fi; }

# ── dotnetup query helpers (via the documented `list --format json` contract) ──────────────────

list_json() { dotnetup list --format json --no-verify "$@"; }

# SDK versions installed in a root, one per line, sorted.
sdk_versions() {
    list_json "$@" | jq -r '.installations[] | select(.component == "SDK") | .version' | sort -V
}

# The channel dotnetup derived and stored for a given repo's global.json.
globaljson_channel() {
    local repo="$1"
    list_json | jq -r --arg suffix "/$repo/global.json" '
        .installSpecs[]
        | select(.source == "GlobalJson" and (.globalJsonPath // "" | endswith($suffix)))
        | .versionOrChannel'
}

spec_count() { list_json "$@" | jq '.installSpecs | length'; }
sdk_count() { list_json "$@" | jq '[.installations[] | select(.component == "SDK")] | length'; }
has_sdk() { sdk_versions | grep -qx "$1"; }
# True when every installation root in the default manifest is inside the work dir.
roots_under_work() { ! list_json | jq -r '.installations[].installRoot' | grep -qv "^$WORK/"; }
no_sdk_matching() { ! sdk_versions | grep -Eq "$1"; }

# Run the dotnetup-managed `dotnet` from inside a repo, so that repo's global.json applies.
repo_dotnet() {
    local repo="$1"; shift
    (cd "$REPOS/$repo" && dotnetup dotnet -- "$@")
}

# ── Phase 0: get dotnetup, snapshot the machine ────────────────────────────────────────────────

phase "Phase 0 · Setup"

if ! command -v dotnetup >/dev/null 2>&1; then
    echo "  dotnetup not on PATH — downloading the preview build into $WORK/bin"
    curl -fsSL https://aka.ms/dotnetup/get-dotnetup.sh | bash -s -- --install-dir "$WORK/bin" \
        || { echo "Could not download dotnetup" >&2; exit 2; }
    export PATH="$WORK/bin:$PATH"
fi
fact "dotnetup version" "$(dotnetup --version 2>/dev/null | head -1)"
fact "Platform" "$(uname -sm)"
fact "Work directory" "$WORK"

rm -rf "$REPOS"
cp -R "$EXP_ROOT/repos" "$REPOS"

# Machine-wide .NET (e.g. /usr/share/dotnet on GitHub's Ubuntu runners), captured so phase 7 can
# prove dotnetup left it alone.
SYSTEM_DOTNET=$(command -v dotnet || true)
if [ -n "$SYSTEM_DOTNET" ]; then
    dotnet --list-sdks > "$RESULTS/system-sdks-before.txt" 2>/dev/null
    fact "Machine-wide dotnet" "$SYSTEM_DOTNET ($(wc -l < "$RESULTS/system-sdks-before.txt" | tr -d ' ') SDKs)"
else
    fact "Machine-wide dotnet" "none"
fi
PROFILE_HASHES_BEFORE=$(for f in ~/.bashrc ~/.bash_profile ~/.profile ~/.zshrc; do file_hash "$f"; done)

# ── Phase 1: install what each repo's global.json asks for ────────────────────────────────────

phase "Phase 1 · Install from global.json (no version typed by hand)"

# repo:expected-channel — the documented rollForward → channel mapping for these three files.
for entry in legacy-billing:8.0 orders-api:10.0.1xx payments-pinned:10.0.100; do
    repo=${entry%%:*}; expected=${entry#*:}
    rf=$(jq -r '.sdk.rollForward' "$REPOS/$repo/global.json")
    ver=$(jq -r '.sdk.version' "$REPOS/$repo/global.json")
    echo "  $repo: global.json says $ver / rollForward=$rf"
    start=$(now)
    (cd "$REPOS/$repo" && run "install-$repo" dotnetup sdk install --no-progress --interactive false)
    rc=$?
    fact "Install time · $repo" "$(( $(now) - start ))s"
    check "$repo: \`dotnetup sdk install\` with no arguments succeeds" eq "$rc" 0
    check "$repo: rollForward=$rf on $ver is tracked as channel \`$expected\`" \
        eq "$(globaljson_channel "$repo")" "$expected"
done

echo
run list-after-install dotnetup list --no-verify

# ── Phase 2: side by side, from one root, with no PATH changes ────────────────────────────────

phase "Phase 2 · Side-by-side SDKs, each repo gets its own"

LATEST_10_1XX=$(sdk_versions | grep -E '^10\.0\.1[0-9]{2}$' | tail -1)
fact "Installed SDKs" "$(sdk_versions | tr '\n' ' ')"

v_legacy=$(repo_dotnet legacy-billing --version)
v_orders=$(repo_dotnet orders-api --version)
v_payments=$(repo_dotnet payments-pinned --version)
fact "\`dotnet --version\` in legacy-billing" "$v_legacy"
fact "\`dotnet --version\` in orders-api" "$v_orders"
fact "\`dotnet --version\` in payments-pinned" "$v_payments"

check "legacy-billing resolves an 8.0 SDK" matches "$v_legacy" '^8\.0\.'
check "orders-api resolves the newest 10.0.1xx SDK ($LATEST_10_1XX)" eq "$v_orders" "$LATEST_10_1XX"
check "payments-pinned resolves exactly 10.0.100" eq "$v_payments" "10.0.100"
check "orders-api and payments-pinned get different SDKs from the same root" \
    test "$v_orders" != "$v_payments"

for entry in legacy-billing:'\.NET 8\.' orders-api:'\.NET 10\.' payments-pinned:'\.NET 10\.'; do
    repo=${entry%%:*}; want=${entry#*:}
    out=$(repo_dotnet "$repo" run 2>&1 | tail -1)
    echo "    $out"
    check "$repo builds and runs on the expected runtime" matches "$out" "^$repo: $want"
done

# ── Phase 3: two requirements, one copy on disk ───────────────────────────────────────────────

phase "Phase 3 · Shared installs (requirements are deduplicated)"

specs_before=$(spec_count); sdks_before=$(sdk_count); mb_before=$(disk_mb "$DEFAULT_ROOT")
run install-explicit-10.0.1xx dotnetup sdk install 10.0.1xx --no-progress --interactive false
specs_after=$(spec_count); sdks_after=$(sdk_count); mb_after=$(disk_mb "$DEFAULT_ROOT")
fact "Disk before/after adding explicit 10.0.1xx" "${mb_before} MB → ${mb_after} MB"
check "a new explicit requirement is tracked (specs $specs_before → $specs_after)" \
    eq "$specs_after" "$((specs_before + 1))"
check "…but no new SDK is downloaded, it is already satisfied (SDKs stay at $sdks_before)" \
    eq "$sdks_after" "$sdks_before"

# ── Phase 4: update semantics ─────────────────────────────────────────────────────────────────

phase "Phase 4 · Updates move channels, never pinned versions"

run update dotnetup update --no-progress
rc=$?
check "\`dotnetup update\` succeeds across every tracked requirement" eq "$rc" 0
check "the exact 10.0.100 pin survives the update" has_sdk 10.0.100
check "payments-pinned still resolves 10.0.100" eq "$(repo_dotnet payments-pinned --version)" "10.0.100"

# --update-global-json: the repo file is still at 10.0.100 (when it was written). dotnetup
# installs the newest SDK in the derived 10.0.1xx channel and writes that version back.
GJ="$REPOS/orders-api/global.json"
cp "$GJ" "$RESULTS/orders-api-global.json.before"
(cd "$REPOS/orders-api" && run install-update-global-json \
    dotnetup sdk install --update-global-json --no-progress --interactive false)
cp "$GJ" "$RESULTS/orders-api-global.json.after"
diff -u "$RESULTS/orders-api-global.json.before" "$RESULTS/orders-api-global.json.after" \
    | sed 's/^/    /' || true
new_ver=$(jq -r '.sdk.version' "$GJ")
fact "orders-api global.json sdk.version" "10.0.100 → $new_ver"
check "--update-global-json bumps orders-api to the newest 10.0.1xx ($LATEST_10_1XX)" \
    eq "$new_ver" "$LATEST_10_1XX"
check "…and leaves rollForward untouched" eq "$(jq -r '.sdk.rollForward' "$GJ")" "latestPatch"

# ── Phase 5: uninstall + garbage collection ───────────────────────────────────────────────────

phase "Phase 5 · Uninstall only removes what nothing else needs"

run uninstall-explicit dotnetup sdk uninstall 10.0.1xx
check "removing the explicit 10.0.1xx requirement keeps $LATEST_10_1XX (orders-api still needs it)" \
    has_sdk "$LATEST_10_1XX"

mb_before=$(disk_mb "$DEFAULT_ROOT")
run uninstall-legacy dotnetup sdk uninstall 8.0 --source globaljson
mb_after=$(disk_mb "$DEFAULT_ROOT")
fact "Disk reclaimed by removing the 8.0 requirement" "$((mb_before - mb_after)) MB (${mb_before} → ${mb_after} MB)"
check "the 8.0 SDK is gone from disk once nothing requires it" no_sdk_matching '^8\.0\.'
check "…while both 10.0 SDKs remain" has_sdk 10.0.100
check "legacy-billing now fails to resolve an SDK (proves it really came from dotnetup)" \
    eval '! repo_dotnet legacy-billing --version >/dev/null 2>&1'

# ── Phase 6: an isolated root + manifest, the CI / repo-local pattern ─────────────────────────

phase "Phase 6 · Isolated install root and manifest (CI pattern)"

CI_ROOT="$WORK/ci/.dotnet"
CI_MANIFEST="$WORK/ci/dotnetup_manifest.json"
mkdir -p "$WORK/ci"
(cd "$REPOS/orders-api" && run install-ci-root dotnetup sdk install \
    --install-path "$CI_ROOT" --manifest-path "$CI_MANIFEST" --no-progress --interactive false)
ci_ver=$(cd "$REPOS/orders-api" && "$CI_ROOT/dotnet" --version)
fact "\`.dotnet/dotnet --version\` in the isolated root" "$ci_ver"
check "the isolated root serves orders-api's SDK" eq "$ci_ver" "$LATEST_10_1XX"
check "the isolated root is tracked only in its own manifest" \
    eval '! list_json | jq -e --arg r "$CI_ROOT" "any(.installSpecs[]; .installRoot == \$r)" >/dev/null'
check "…and its own manifest knows about it" \
    eval 'list_json --manifest-path "$CI_MANIFEST" | jq -e --arg r "$CI_ROOT" "any(.installSpecs[]; .installRoot == \$r)" >/dev/null'

# ── Phase 7: what dotnetup did NOT touch ──────────────────────────────────────────────────────

phase "Phase 7 · Machine-wide .NET and shell profiles untouched"

if [ -n "$SYSTEM_DOTNET" ]; then
    "$SYSTEM_DOTNET" --list-sdks > "$RESULTS/system-sdks-after.txt" 2>/dev/null
    check "machine-wide SDK list is identical before and after" \
        cmp -s "$RESULTS/system-sdks-before.txt" "$RESULTS/system-sdks-after.txt"
    check "\`dotnet\` on PATH is still the machine-wide one" eq "$(command -v dotnet)" "$SYSTEM_DOTNET"
fi
PROFILE_HASHES_AFTER=$(for f in ~/.bashrc ~/.bash_profile ~/.profile ~/.zshrc; do file_hash "$f"; done)
check "shell profiles are unchanged (no \`init\`/\`env set\` was run)" \
    eq "$PROFILE_HASHES_AFTER" "$PROFILE_HASHES_BEFORE"
check "every managed SDK lives in a user-owned dir under the work dir (no sudo, no /usr/share/dotnet)" \
    roots_under_work

echo
run list-final dotnetup list
list_json > "$RESULTS/list-final.json"

# ── Summary ───────────────────────────────────────────────────────────────────────────────────

{
    echo "# dotnetup experiment results"
    echo
    echo "**$PASSED passed, $FAILED failed**"
    echo
    echo "## Checks"
    echo
    echo "| Phase | Result | Check |"
    echo "|---|---|---|"
    cat "$CHECKS_MD"
    echo
    echo "## Measurements"
    echo
    echo "| What | Value |"
    echo "|---|---|"
    cat "$FACTS_MD"
    echo
    echo "## Final \`dotnetup list\`"
    echo
    echo '```'
    cat "$RESULTS/list-final.log"
    echo '```'
} > "$RESULTS/summary.md"

if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
    cat "$RESULTS/summary.md" >> "$GITHUB_STEP_SUMMARY"
fi

printf '\n\033[1m%d passed, %d failed\033[0m — full report: %s\n' "$PASSED" "$FAILED" "$RESULTS/summary.md"

echo "Work dir kept for inspection: $WORK (delete it when done — it holds several SDKs)"

[ "$FAILED" -eq 0 ]
