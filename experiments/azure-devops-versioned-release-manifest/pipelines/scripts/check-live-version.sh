#!/usr/bin/env bash
# check-live-version.sh <resource-group> <app-name> <version>
#
# Sets the pipeline variable upToDate=true when production already reports <version>. Every
# later deploy step is conditioned on upToDate, so an unchanged API costs one HTTP call.
# If live can't be reached, it reports "unreachable" and the deploy goes ahead. A failing `az`
# call (a bad service connection or a missing app) fails the job.
source "$(dirname "$0")/deploy-common.sh"
rg="$1" app="$2" want="$3"

host=$(app_host "$rg" "$app")
live=$(app_version "$host")
echo "$app is running $live. This release wants $want."
if [ "$live" = "$want" ]; then
  echo "Already at $want, so nothing to deploy."
  echo "##vso[task.setvariable variable=upToDate]true"
fi
