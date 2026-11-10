#!/bin/bash
# Compile et installe le plugin dans le Jellyfin local (arrête Jellyfin le temps de la copie).
set -e
ROOT="$(cd "$(dirname "$0")" && pwd)"
LOG=$(mktemp)
cd "$ROOT/src"
dotnet build -c Release 2>&1 | tee "$LOG" | grep -E " error |Build succeeded" || true
grep -q "Build succeeded" "$LOG" || { echo "ÉCHEC DE COMPILATION, rien déployé"; exit 1; }
VER=$(grep -oP '(?<=<Version>)[^<]+' Jellyfin.Plugin.TorrServer.csproj)
P=/var/lib/jellyfin/plugins
D="$P/TorrServer_$VER"
systemctl stop jellyfin
# nouvelle version : on reprend le dossier de la précédente (meta.json) ; la configuration et les données sont ailleurs
OLD=$(ls -d "$P"/TorrServer_* 2>/dev/null | head -1 || true)
if [ -n "$OLD" ] && [ "$OLD" != "$D" ]; then mv "$OLD" "$D"; fi
mkdir -p "$D"
cp bin/Release/net10.0/Jellyfin.Plugin.TorrServer.dll "$D/"
cp "$ROOT/assets/logo.png" "$D/logo.png"
[ -f "$D/meta.json" ] && sed -i -E "s/(\"version\": *\")[^\"]+/\1$VER/; s#(\"imagePath\": *\")[^\"]*#\1$D/logo.png#" "$D/meta.json"
chown -R jellyfin:jellyfin "$D"
systemctl start jellyfin
for i in $(seq 1 40); do curl -sf http://127.0.0.1:8096/System/Info/Public >/dev/null && break; sleep 2; done
