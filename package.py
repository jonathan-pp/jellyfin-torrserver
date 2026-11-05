#!/usr/bin/env python3
"""Construit dist/TorrServer-<version>.zip et met à jour manifest.json."""
import hashlib, json, subprocess, zipfile, datetime, pathlib, re
root = pathlib.Path(__file__).parent
subprocess.run(["dotnet", "build", str(root / "src"), "-c", "Release"], check=True)
version = re.search(r"<Version>([^<]+)", (root / "src/Jellyfin.Plugin.TorrServer.csproj").read_text()).group(1)
dll = root / "src/bin/Release/net10.0/Jellyfin.Plugin.TorrServer.dll"
meta = {"category": "General", "guid": "655fbf1b-525b-4f82-bdeb-f27dd0bdd606", "name": "TorrServer",
        "description": "Films et séries en streaming via Prowlarr + TorrServer, sans rien télécharger.",
        "overview": "Bibliothèque de films et séries lus en streaming.", "owner": "jonathan-pp",
        "targetAbi": "12.4.0.0", "version": version, "status": "Active", "autoUpdate": True, "imagePath": "", "assemblies": []}
(root / "dist").mkdir(exist_ok=True)
zpath = root / "dist" / f"TorrServer-{version}.zip"
with zipfile.ZipFile(zpath, "w", zipfile.ZIP_DEFLATED) as z:
    z.write(dll, dll.name)
    z.writestr("meta.json", json.dumps(meta, ensure_ascii=False, indent=2))
md5 = hashlib.md5(zpath.read_bytes()).hexdigest()
(root / "dist" / f"TorrServer-{version}.zip.sha256").write_text(hashlib.sha256(zpath.read_bytes()).hexdigest() + f"  {zpath.name}\n")
manifest = [{"guid": meta["guid"], "name": "TorrServer", "description": meta["description"], "overview": meta["overview"],
             "owner": "jonathan-pp", "category": "General", "imageUrl": "https://raw.githubusercontent.com/jonathan-pp/jellyfin-torrserver/main/assets/logo.png",
             "versions": [{"version": version, "changelog": "Réécriture : bibliothèque native (.strm), choix automatique de la meilleure source.",
                           "targetAbi": "12.4.0.0", "sourceUrl": f"https://raw.githubusercontent.com/jonathan-pp/jellyfin-torrserver/main/dist/{zpath.name}",
                           "checksum": md5, "timestamp": datetime.datetime.now(datetime.UTC).strftime("%Y-%m-%dT%H:%M:%SZ")}]}]
(root / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2))
print(zpath, md5)
