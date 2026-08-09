#!/usr/bin/env bash
set -euo pipefail

# This script will configure your BTCPay Server developer environement to load the plugin during a debug session.

source plugin-env.sh

TARGET_PATH="$(dotnet build "src/$PROJECT/$PROJECT.csproj" -p:Configuration=Debug -getProperty:TargetPath)"

# Merge into appsettings.dev.json instead of overwriting, so dev-only keys
# (e.g. LnurlBackendAllowHttp) survive re-registration.
python3 - "$TARGET_PATH" <<'EOF'
import json, os, sys
target, path = sys.argv[1], "submodules/btcpayserver/BTCPayServer/appsettings.dev.json"
d = json.load(open(path)) if os.path.exists(path) else {}
d["DEBUG_PLUGINS"] = target
json.dump(d, open(path, "w"))
EOF

echo "The plugin will now start when debugging BTCPay Server"