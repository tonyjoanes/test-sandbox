#!/usr/bin/env bash
# Packs the templates, installs them into a throwaway template cache, generates a project from
# every option combination we care about and builds it. Used by the pipeline and runnable locally.
#
#   ./scripts/smoke-test.sh                 # packs 0.0.0-local
#   PACKAGE_VERSION=1.2.3 ./scripts/smoke-test.sh
#   OUTPUT_DIR=./artifacts ./scripts/smoke-test.sh   # keep the .nupkg somewhere useful
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
version="${PACKAGE_VERSION:-0.0.0-local}"
work="$(mktemp -d)"
out="${OUTPUT_DIR:-$work/nupkg}"
# --debug:custom-hive keeps these installs out of your real `dotnet new` template cache.
hive="$work/hive"
trap 'rm -rf "$work"' EXIT

echo "==> Packing Contoso.Platform.Templates $version"
dotnet pack "$root/Contoso.Platform.Templates.csproj" -c Release -o "$out" -p:PackageVersion="$version" --nologo
nupkg="$out/Contoso.Platform.Templates.$version.nupkg"

echo "==> Installing $nupkg into a scratch template cache"
dotnet new install "$nupkg" --debug:custom-hive "$hive"

# generate <project name> <template short name> [template options...]
generate() {
  local name="$1" template="$2"
  shift 2
  local dir="$work/generated/$name"
  echo "==> dotnet new $template -n $name $*"
  dotnet new "$template" -n "$name" -o "$dir" "$@" --debug:custom-hive "$hive"

  # The template's placeholder names must never leak into a generated project.
  if grep -rnE 'Contoso\.(WebApi|FunctionApp)' "$dir" --exclude-dir=bin --exclude-dir=obj >&2; then
    echo "FAIL: placeholder name left in $name (see above)" >&2
    exit 1
  fi
  test -f "$dir/$name.csproj" || { echo "FAIL: $name.csproj was not created" >&2; exit 1; }

  dotnet build "$dir" -c Release -warnaserror --nologo -v quiet
}

generate Acme.Orders.Api    contoso-webapi
generate Acme.Bare.Api      contoso-webapi --openapi false --samples false
generate Acme.Orders.Worker contoso-func
generate Acme.Sched.Worker  contoso-func --timer --schedule "0 0 2 * * *"

echo "==> All template smoke tests passed"
