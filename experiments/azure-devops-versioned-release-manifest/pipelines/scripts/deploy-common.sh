# Shared by the deploy scripts. Source it; don't run it.
#
# VERSION_URL_SCHEME exists for the local tests (scripts/test-deploy-scripts.cs). They run the
# real API over plain http on localhost, with a fake `az`. In a pipeline it's always https.

set -euo pipefail

# app_host <resource-group> <app-name> [slot]: prints the app's (or slot's) default host name.
app_host() {
  local slot_args=()
  [ -n "${3:-}" ] && slot_args=(--slot "$3")
  az webapp show -g "$1" -n "$2" "${slot_args[@]}" --query defaultHostName -o tsv
}

# app_version <host>: prints the version the API reports on /version, or "unreachable".
app_version() {
  curl -fsS --max-time 10 "${VERSION_URL_SCHEME:-https}://$1/version" | jq -r .version || echo "unreachable"
}
