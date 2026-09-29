#!/usr/bin/env bash
# Calls the Azure DevOps Pipelines Preview API to fully resolve a pipeline — every `template:`
# reference, repository resource, and compile-time expression — without queuing a single job.
# Exits non-zero with the API's own error text if resolution fails; that's the whole mechanism.
#
# Illustrative: the Preview API's exact response shape has shifted across API versions —
# check `finalYaml` / error field names against the api-version you're actually pinned to
# before relying on this in a real gate.

set -euo pipefail

organization_uri="$1"   # e.g. https://dev.azure.com/contoso/
project="$2"             # e.g. platform-engineering
pipeline_id="$3"         # the harness pipeline's registered ID
source_branch="$4"       # e.g. refs/heads/feature/new-tier-param
access_token="$5"        # $(System.AccessToken) — this pipeline's own scoped OAuth token

api_url="${organization_uri%/}/${project}/_apis/pipelines/${pipeline_id}/preview?api-version=7.1"

response=$(curl -sS -w '\n%{http_code}' \
  -H "Authorization: Bearer ${access_token}" \
  -H "Content-Type: application/json" \
  -X POST "$api_url" \
  -d "{\"resources\":{\"repositories\":{\"self\":{\"refName\":\"${source_branch}\"}}}}")

http_status=$(echo "$response" | tail -n1)
body=$(echo "$response" | sed '$d')

if [ "$http_status" -ge 400 ]; then
  echo "Preview API returned HTTP ${http_status} — the harness pipeline failed to resolve on branch ${source_branch}:"
  echo "$body" | jq -r '.message // .' 2>/dev/null || echo "$body"
  exit 1
fi

# A 200 with no finalYaml (or with a populated validationResult.errors array, depending on
# api-version) means the request was accepted but the pipeline itself didn't compile — this is
# exactly what an unresolved `@reponame` alias, an undefined template parameter, or a broken
# expression looks like: no job ever gets far enough to run, but the HTTP call itself succeeds.
final_yaml=$(echo "$body" | jq -r '.finalYaml // empty' 2>/dev/null || true)

if [ -z "$final_yaml" ]; then
  echo "Preview API accepted the request but returned no resolved YAML — the harness pipeline did not compile on branch ${source_branch}:"
  echo "$body" | jq '.' 2>/dev/null || echo "$body"
  exit 1
fi

echo "Harness pipeline resolved cleanly on branch ${source_branch} — every template reference and expression in it compiled."
