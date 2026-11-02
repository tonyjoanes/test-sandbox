#!/usr/bin/env bash
# verify-production.sh <resource-group> <app-name> <version> <slot>
#
# Runs after the swap. Fails if production isn't serving <version>, and prints the command
# that swaps the previous version back (it's now sitting in <slot>).
source "$(dirname "$0")/deploy-common.sh"
rg="$1" app="$2" want="$3" slot="$4"

got=$(app_version "$(app_host "$rg" "$app")")
if [ "$got" != "$want" ]; then
  echo "##vso[task.logissue type=error]Production reports $got after the swap, expected $want. Swap back with: az webapp deployment slot swap -g '$rg' -n '$app' --slot '$slot'"
  exit 1
fi
echo "Production is serving $app $got"
