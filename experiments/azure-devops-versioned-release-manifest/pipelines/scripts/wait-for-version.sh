#!/usr/bin/env bash
# wait-for-version.sh <resource-group> <app-name> <version> <slot>
#
# Polls the slot's /version until it reports <version>. A freshly deployed slot takes a while
# to restart. Fails if it never does, before anything touches production.
# ATTEMPTS and INTERVAL_SECONDS default to 30 x 10s.
source "$(dirname "$0")/deploy-common.sh"
rg="$1" app="$2" want="$3" slot="$4"
attempts="${ATTEMPTS:-30}" interval="${INTERVAL_SECONDS:-10}"

host=$(app_host "$rg" "$app" "$slot")
for attempt in $(seq 1 "$attempts"); do
  got=$(app_version "$host")
  if [ "$got" = "$want" ]; then
    echo "The '$slot' slot is serving $got"
    exit 0
  fi
  echo "Attempt $attempt of $attempts: the '$slot' slot reports $got, waiting..."
  sleep "$interval"
done
echo "##vso[task.logissue type=error]The '$slot' slot never reported $want. Production is untouched."
exit 1
