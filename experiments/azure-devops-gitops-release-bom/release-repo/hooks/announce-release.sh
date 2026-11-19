#!/usr/bin/env bash
# Release-level pre-sync hook: tell people a release is starting.
# Posts to a Teams incoming webhook if TEAMS_WEBHOOK_URL is set (a secret pipeline variable),
# otherwise just logs. Receives RELCTL_ENVIRONMENT, RELCTL_RELEASE and RELCTL_CHANGES.
set -euo pipefail

message="Release ${RELCTL_RELEASE:-unnamed} is starting in ${RELCTL_ENVIRONMENT}: ${RELCTL_CHANGES//,/, }"
echo "$message"
if [[ -n "${TEAMS_WEBHOOK_URL:-}" ]]; then
  curl --fail --silent --show-error -H 'Content-Type: application/json' \
    --data "$(jq -n --arg text "$message" '{text: $text}')" "$TEAMS_WEBHOOK_URL"
fi
