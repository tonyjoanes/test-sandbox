#!/bin/bash
# Runs as the App Service "Startup Command" (Configuration > General settings
# > Startup Command) before the Functions host starts, on every container
# start/restart.
#
# Deployed as part of the function app package (repo root, alongside
# host.json) rather than pushed separately via Kudu — this works whether or
# not the SCM/Kudu site is reachable. If your App Service has network
# restrictions or a private endpoint and Kudu is not on your allowed list,
# nothing else about this file changes: it lands at
# /home/site/wwwroot/startup.sh through the normal zip-deploy/run-from-package
# path used by `func azure functionapp publish`, and main.bicep's
# appCommandLine points straight at it.
#
# Why this exists: uploading an internal root/issuing CA certificate as a
# "public certificate" on App Service and adding it to WEBSITE_LOAD_CERTIFICATES
# only copies the file into the container (/var/ssl/certs). On multi-tenant
# Linux App Service that does NOT add it to the OS/OpenSSL trust store used
# by PowerShell 7 (.NET) when validating the SERVER certificate presented by
# the internal app. Without this step you get errors such as:
#   "unable to get local issuer certificate" / "PKIX path building failed"
# even though the client certificate itself (used for mTLS) loads fine.
#
# This script copies any certs staged by WEBSITE_LOAD_CERTIFICATES into the
# container's trust store and rebuilds it with update-ca-certificates. It
# must run on every start because the filesystem is not guaranteed to
# persist across scale/restart events on multi-tenant plans.
#
# Requires: Elastic Premium or Dedicated (Basic/Standard/Premium) plan.
# Consumption (Y1) Linux does not reliably support WEBSITE_LOAD_CERTIFICATES
# or custom startup commands — see README "Known limitation".

set -euo pipefail

CERT_SOURCE_DIR="/var/ssl/certs"
TRUST_DIR="/usr/local/share/ca-certificates"

# Thumbprints of the INTERNAL ROOT/ISSUING CA certs (not the client cert),
# set as app settings so this script doesn't hardcode environment-specific
# values. Comma-separated list.
IFS=',' read -ra CA_THUMBPRINTS <<< "${INTERNAL_CA_THUMBPRINTS:-}"

if [ ${#CA_THUMBPRINTS[@]} -eq 0 ] || [ -z "${CA_THUMBPRINTS[0]}" ]; then
    echo "startup.sh: INTERNAL_CA_THUMBPRINTS is not set — skipping trust store update."
    exit 0
fi

if [ ! -d "$CERT_SOURCE_DIR" ]; then
    echo "startup.sh: $CERT_SOURCE_DIR does not exist yet — WEBSITE_LOAD_CERTIFICATES may not have staged certs. Skipping."
    exit 0
fi

updated=0
for thumbprint in "${CA_THUMBPRINTS[@]}"; do
    src="$CERT_SOURCE_DIR/$thumbprint.der"
    if [ ! -f "$src" ]; then
        # Platform has staged this as PFX/PEM in some configurations — try both known extensions.
        src="$CERT_SOURCE_DIR/$thumbprint.pem"
    fi
    if [ ! -f "$src" ]; then
        echo "startup.sh: WARNING - no staged cert file found for thumbprint $thumbprint under $CERT_SOURCE_DIR"
        continue
    fi

    dest="$TRUST_DIR/internal-ca-$thumbprint.crt"
    if openssl x509 -inform DER -in "$src" -out "$dest" 2>/dev/null; then
        echo "startup.sh: converted DER -> PEM for $thumbprint"
    else
        cp "$src" "$dest"
        echo "startup.sh: copied $thumbprint as-is (already PEM)"
    fi
    updated=1
done

if [ "$updated" -eq 1 ]; then
    update-ca-certificates
    echo "startup.sh: trust store updated. Restart the app if this was applied after the host already started."
fi
