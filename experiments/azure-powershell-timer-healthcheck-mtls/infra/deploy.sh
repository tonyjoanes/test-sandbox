#!/bin/bash
# Illustrative end-to-end deployment. Review every value before running
# against a real subscription — this is a worked example, not a turnkey
# script for production use.
#
# Deliberately avoids the Kudu/SCM endpoint (<app>.scm.azurewebsites.net)
# everywhere. On a locked-down App Service — access restrictions, private
# endpoint, or a corporate proxy that simply doesn't allow *.scm.azurewebsites.net
# — Kudu-dependent tooling (log tail/stream, Kudu VFS/console) fails even
# though the app itself works fine. `func azure functionapp publish` and
# `az functionapp deployment source config-zip` both use the ARM control
# plane / SCM deployment API path, not the interactive Kudu UI, so they keep
# working; this script sticks to that surface throughout.
set -euo pipefail

RESOURCE_GROUP="${RESOURCE_GROUP:?set RESOURCE_GROUP}"
LOCATION="${LOCATION:?set LOCATION}"
NAME_PREFIX="${NAME_PREFIX:?set NAME_PREFIX}"
KEY_VAULT_RESOURCE_ID="${KEY_VAULT_RESOURCE_ID:?set KEY_VAULT_RESOURCE_ID (Key Vault must already hold the client cert)}"
CLIENT_CERT_KV_NAME="${CLIENT_CERT_KV_NAME:?set CLIENT_CERT_KV_NAME}"
HEALTHCHECK_TARGET_URL="${HEALTHCHECK_TARGET_URL:?set HEALTHCHECK_TARGET_URL}"
INTERNAL_CA_THUMBPRINTS="${INTERNAL_CA_THUMBPRINTS:-}"
LOG_ANALYTICS_WORKSPACE_ID="${LOG_ANALYTICS_WORKSPACE_ID:-}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_DIR="$(dirname "$SCRIPT_DIR")"

echo "==> Deploying infrastructure"
az deployment group create \
  --resource-group "$RESOURCE_GROUP" \
  --template-file "$SCRIPT_DIR/main.bicep" \
  --parameters \
      namePrefix="$NAME_PREFIX" \
      location="$LOCATION" \
      keyVaultResourceId="$KEY_VAULT_RESOURCE_ID" \
      clientCertKeyVaultCertName="$CLIENT_CERT_KV_NAME" \
      healthCheckTargetUrl="$HEALTHCHECK_TARGET_URL" \
      internalCaThumbprints="$INTERNAL_CA_THUMBPRINTS"

FUNCTION_APP_NAME="${NAME_PREFIX}-func"

echo "==> Deploying function code (includes startup.sh — no separate Kudu upload needed)"
(cd "$APP_DIR" && func azure functionapp publish "$FUNCTION_APP_NAME")

echo "==> Restarting app so the trust store update in startup.sh takes effect"
az functionapp restart --name "$FUNCTION_APP_NAME" --resource-group "$RESOURCE_GROUP"

if [ -n "$LOG_ANALYTICS_WORKSPACE_ID" ]; then
  echo "==> Wiring platform logs to Log Analytics (bypasses Kudu/SCM entirely)"
  az monitor diagnostic-settings create \
    --name "${NAME_PREFIX}-diag" \
    --resource "$FUNCTION_APP_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --resource-type "Microsoft.Web/sites" \
    --workspace "$LOG_ANALYTICS_WORKSPACE_ID" \
    --logs '[{"category":"FunctionAppLogs","enabled":true},{"category":"AppServiceConsoleLogs","enabled":true}]'
else
  echo "==> Skipping diagnostic settings — set LOG_ANALYTICS_WORKSPACE_ID to wire up Kudu-independent logging (see README 'Getting logs without Kudu')"
fi

echo "==> Done. See README 'Getting logs without Kudu' for how to query FunctionAppLogs / AppServiceConsoleLogs and Application Insights instead of log tail."
