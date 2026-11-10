#!/usr/bin/env python3
"""Construit dist/TorrServer-<version>.zip et met à jour manifest.json."""
import hashlib, json, subprocess, zipfile, datetime, pathlib, re
root = pathlib.Path(__file__).parent
CHANGELOG = "Bibliothèque personnelle par utilisateur, prochains épisodes (date dans le résumé, page « À venir », avertissement à la sortie, agenda .ics), onglet Calendrier, genres et catégorie « En production » pour les animes, aucune recherche de source avant la sortie d'un épisode, étiquettes internes masquées, logo du plugin corrigé."
subprocess.run(["dotnet", "build", str(root / "src"), "-c", "Release"], check=True)
version = re.search(r"<Version>([^<]+)", (root / "src/Jellyfin.Plugin.TorrServer.csproj").read_text()).group(1)
dll = root / "src/bin/Release/net10.0/Jellyfin.Plugin.TorrServer.dll"
meta = {"category": "General", "guid": "655fbf1b-525b-4f82-bdeb-f27dd0bdd606", "name": "TorrServer",
        "description": "Films, séries et animes en streaming par torrent (Prowlarr + TorrServer) dans Jellyfin, sans rien télécharger.",
        "overview": "Dans Jellyfin : page Découvrir (TMDB), choix automatique de la meilleure source, animes avec passer l'intro, prochains épisodes, bibliothèque personnelle et quotas par utilisateur.", "owner": "jonathan-pp",
        "targetAbi": "12.4.0.0", "version": version, "status": "Active", "autoUpdate": True, "imagePath": "logo.png", "assemblies": []}
(root / "dist").mkdir(exist_ok=True)
zpath = root / "dist" / f"TorrServer-{version}.zip"
with zipfile.ZipFile(zpath, "w", zipfile.ZIP_DEFLATED) as z:
    z.write(dll, dll.name)
    z.write(root / "assets/logo.png", "logo.png")
    z.writestr("meta.json", json.dumps(meta, ensure_ascii=False, indent=2))
md5 = hashlib.md5(zpath.read_bytes()).hexdigest()
(root / "dist" / f"TorrServer-{version}.zip.sha256").write_text(hashlib.sha256(zpath.read_bytes()).hexdigest() + f"  {zpath.name}\n")
manifest = [{"guid": meta["guid"], "name": "TorrServer", "description": meta["description"], "overview": meta["overview"],
             "owner": "jonathan-pp", "category": "General", "imageUrl": "https://raw.githubusercontent.com/jonathan-pp/jellyfin-torrserver/main/assets/logo.png",
             "versions": [{"version": version, "changelog": CHANGELOG,
                           "targetAbi": "12.4.0.0", "sourceUrl": f"https://raw.githubusercontent.com/jonathan-pp/jellyfin-torrserver/main/dist/{zpath.name}",
                           "checksum": md5, "timestamp": datetime.datetime.now(datetime.UTC).strftime("%Y-%m-%dT%H:%M:%SZ")}]}]
old_manifest = json.loads((root / "manifest.json").read_text()) if (root / "manifest.json").exists() else []
if old_manifest:
    manifest[0]["versions"] += [v for v in old_manifest[0]["versions"] if v["version"] != version]
(root / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2))
print(zpath, md5)
