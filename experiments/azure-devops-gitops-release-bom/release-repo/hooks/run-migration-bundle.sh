#!/usr/bin/env bash
# Pre-sync hook: apply the database migrations that ship inside the package being deployed.
#
# The build pipeline adds an EF Core migration bundle (`dotnet ef migrations bundle`) to the
# package as `efbundle`, next to app.zip. Running it here, before the swap, means the schema is
# ready when the new code goes live. It also means migrations must be backwards compatible
# (expand/contract), because the old code keeps running against them until the swap.
#
# relctl provides: RELCTL_ENVIRONMENT, RELCTL_RELEASE, RELCTL_COMPONENT, RELCTL_VERSION,
# RELCTL_PREVIOUS_VERSION, RELCTL_PACKAGE_DIR, RELCTL_SUBSCRIPTION, RELCTL_RESOURCE_GROUP,
# RELCTL_RESOURCE_NAME. The pipeline is logged in with `az` already (AzureCLI@2).
set -euo pipefail

bundle="$RELCTL_PACKAGE_DIR/efbundle"
if [[ ! -f "$bundle" ]]; then
  echo "$RELCTL_COMPONENT $RELCTL_VERSION ships no migration bundle. Nothing to migrate."
  exit 0
fi

# The connection string lives in the app's own Key Vault reference, so read it from there
# rather than storing it in the pipeline.
conn=$(az webapp config connection-string list \
  --subscription "$RELCTL_SUBSCRIPTION" --resource-group "$RELCTL_RESOURCE_GROUP" --name "$RELCTL_RESOURCE_NAME" \
  --query "[?name=='Default'].value | [0]" --output tsv)

chmod +x "$bundle"
"$bundle" --connection "$conn"
echo "Migrations for $RELCTL_COMPONENT $RELCTL_VERSION applied in $RELCTL_ENVIRONMENT."
