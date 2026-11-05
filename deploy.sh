#!/bin/bash
# Compile et installe le plugin dans le Jellyfin local.
set -e
LOG=$(mktemp)
cd "$(dirname "$0")/src" && dotnet build -c Release 2>&1  | tee "$LOG" | grep -E " error |Build succeeded"; grep -q "Build succeeded" "$LOG" || { echo "ÉCHEC DE COMPILATION, rien déployé"; exit 1; }
D=/var/lib/jellyfin/plugins/TorrServer_0.2.0.0
systemctl stop jellyfin
cp bin/Release/net10.0/Jellyfin.Plugin.TorrServer.dll $D/ && chown -R jellyfin:jellyfin $D
systemctl start jellyfin
for i in $(seq 1 40); do curl -sf http://127.0.0.1:8096/System/Info/Public >/dev/null && break; sleep 2; done
