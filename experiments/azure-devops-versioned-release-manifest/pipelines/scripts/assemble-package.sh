#!/usr/bin/env bash
# assemble-package.sh <api> <project-file> <version> <output-dir>
#
# Builds the contents of one API's release package in <output-dir>:
#   app.zip             the `dotnet publish` output, stamped with <version>
#   release-rules.yaml  copied from beside the project file
#   build-info.json     the commit, branch and build that produced it
# The build pipeline publishes <output-dir> as a Universal Package. The local tests
# (scripts/test-packaging.cs) run this same script, so what they check is what CI ships.
# The Azure DevOps predefined variables below are set automatically on a pipeline agent.
set -euo pipefail
api="$1" project="$2" version="$3" out="$4"
rules="$(dirname "$project")/release-rules.yaml"

# Catch a copied-and-not-edited rules file before it's published under the wrong name.
declared=$(grep -E '^api:' "$rules" | awk '{print $2}')
if [ "$declared" != "$api" ]; then
  echo "##vso[task.logissue type=error]$rules declares api '$declared', but this is the $api package."
  exit 1
fi

work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT
dotnet publish "$project" -c Release -o "$work/publish" -p:Version="$version" --nologo -v quiet

mkdir -p "$out"
(cd "$work/publish" && zip -qr "$work/app.zip" .)
mv "$work/app.zip" "$out/app.zip"
cp "$rules" "$out/release-rules.yaml"
cat > "$out/build-info.json" <<JSON
{
  "api": "$api",
  "version": "$version",
  "commit": "${BUILD_SOURCEVERSION:-local}",
  "branch": "${BUILD_SOURCEBRANCH:-local}",
  "buildId": "${BUILD_BUILDID:-local}",
  "buildUrl": "${SYSTEM_COLLECTIONURI:-}${SYSTEM_TEAMPROJECT:-}/_build/results?buildId=${BUILD_BUILDID:-local}"
}
JSON
echo "Assembled $api $version in $out"
