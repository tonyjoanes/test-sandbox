#!/bin/bash
# Illustrative end-to-end deployment. Review every value before running
# against a real subscription — this is a worked example, not a turnkey
# script for production use.
set -euo pipefail

RESOURCE_GROUP="${RESOURCE_GROUP:?set RESOURCE_GROUP}"
LOCATION="${LOCATION:?set LOCATION}"
NAME_PREFIX="${NAME_PREFIX:?set NAME_PREFIX}"
KEY_VAULT_RESOURCE_ID="${KEY_VAULT_RESOURCE_ID:?set KEY_VAULT_RESOURCE_ID (Key Vault must already hold the client cert)}"
CLIENT_CERT_KV_NAME="${CLIENT_CERT_KV_NAME:?set CLIENT_CERT_KV_NAME}"
HEALTHCHECK_TARGET_URL="${HEALTHCHECK_TARGET_URL:?set HEALTHCHECK_TARGET_URL}"
INTERNAL_CA_THUMBPRINTS="${INTERNAL_CA_THUMBPRINTS:-}"

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

echo "==> Uploading startup.sh to the persistent /home share via Kudu VFS"
CREDS=$(az webapp deployment list-publishing-credentials \
  --name "$FUNCTION_APP_NAME" --resource-group "$RESOURCE_GROUP" \
  --query "{u:publishingUserName,p:publishingPassword}" -o tsv)
KUDU_USER=$(echo "$CREDS" | cut -f1)
KUDU_PASS=$(echo "$CREDS" | cut -f2)

curl -sS --fail -u "${KUDU_USER}:${KUDU_PASS}" \
  -X PUT \
  -T "$SCRIPT_DIR/startup.sh" \
  "https://${FUNCTION_APP_NAME}.scm.azurewebsites.net/api/vfs/startup.sh"

echo "==> Deploying function code"
(cd "$APP_DIR" && func azure functionapp publish "$FUNCTION_APP_NAME")

echo "==> Restarting app so the trust store update in startup.sh takes effect"
az functionapp restart --name "$FUNCTION_APP_NAME" --resource-group "$RESOURCE_GROUP"

echo "==> Done. Tail logs with:"
echo "    az webapp log tail --name $FUNCTION_APP_NAME --resource-group $RESOURCE_GROUP"
