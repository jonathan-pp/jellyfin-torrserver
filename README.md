# Jellyfin TorrServer

<img src="assets/logo.png" width="140" alt="Logo TorrServer">

Plugin Jellyfin 12.4 : des films, séries et animes **en streaming via torrents, sans rien télécharger sur le disque**, depuis l'interface native de Jellyfin.

Le plugin ajoute de **vrais titres à la bibliothèque** (affiches, résumés, « vu » / « reprendre » natifs, tous clients). À la lecture il :

1. cherche les sources dans **Prowlarr** (recherche par identifiant TMDB + recherche textuelle sur tous les indexeurs, titre français / original / anglais) ;
2. choisit la meilleure : langue préférée, résolution, nombre de sources, taille raisonnable, H.264 préféré, pas de CAM/TS ;
3. l'envoie à **TorrServer**, trouve le bon fichier (épisode dans un pack de saison, numérotation absolue des animes comprise) et relaie le flux (Range géré).

Les fichiers de la bibliothèque sont de simples `.strm` de quelques octets.

## Prérequis
- Jellyfin **12.4** (.NET 10), [TorrServer](https://github.com/YouROK/TorrServer), [Prowlarr](https://prowlarr.com) avec vos indexeurs, un jeton TMDB.
- Le plugin **File Transformation** (`https://www.iamparadox.dev/jellyfin/plugins/manifest.json`), qui ajoute la page Découvrir et la recherche TMDB à l'interface de Jellyfin.

## Installation
Ajouter le dépôt de plugins dans Jellyfin (*Tableau de bord → Extensions → Dépôts*) :
`https://raw.githubusercontent.com/jonathan-pp/jellyfin-torrserver/main/manifest.json`
puis installer **TorrServer** et redémarrer. Configurer ensuite *Extensions → TorrServer* (TMDB, Prowlarr, TorrServer, « Tester les connexions », « Créer les bibliothèques »).

## Utilisation
- **Découvrir (TMDB)** : lien dans l'en-tête (et dans le tiroir sur mobile). Onglets Séries / Films / Animes, genres, défilement infini, flèches de défilement.
- **Recherche Jellyfin** : une ligne « Sur TMDB » s'ajoute aux résultats.
- Un clic sur une affiche crée le titre et ouvre **la vraie fiche Jellyfin** (avec un pourcentage de chargement). Le menu **Version** liste les sources
  (`00 · ⚡ Meilleure source` = choix automatique, puis 01, 02… avec qualité, langues, nombre de sources, taille, indexeur).
- Les titres non sortis sont signalés ; les épisodes ont leurs propres sources (packs de saison reconnus).

## Gestion des utilisateurs
*Extensions → TorrServer → Utilisateurs* : par utilisateur, accès à « Découvrir », trackers/indexeurs autorisés, et un profil de sources personnalisé
(langues, résolution max, H.264, sources minimum, tailles max). Appliqué à la lecture et au menu Version.

## Détails techniques
- Durées : les versions jamais analysées reçoivent la durée d'une autre version ou de TMDB ; la version lancée est analysée **avant** que le lecteur la reçoive (durée exacte, enchaînement des épisodes).
- Les analyses automatiques de Jellyfin sur chaque `.strm` (qui chargeraient un torrent par version) sont refusées (détection du processus `ffprobe` via `/proc`, Linux).
- Source morte : si un torrent n'envoie aucune donnée en 45 s au début de la lecture, ou si la version choisie est inutilisable, le plugin passe à la source suivante sans erreur côté lecteur.
- Enchaînement : pendant la lecture d'un épisode, le suivant est préparé à l'avance. Un pack choisi dans le menu Version reste utilisé pour la suite de la saison.
- Versions stables : une source garde son fichier tant qu'elle reste proposée (son étiquette n'est rafraîchie qu'au bout de 7 jours), ce qui évite à Jellyfin de tout réanalyser.
- Conseillé pour démarrer plus vite : limiter l'analyse de ffmpeg par `JELLYFIN_FFmpeg__analyzeduration=10M` et `JELLYFIN_FFmpeg__probesize=50M` dans l'environnement du service Jellyfin (valeurs par défaut : 200M / 1G, soit environ 75 Mo de torrent à charger avant la première image).
- Les URL de lecture contiennent une clé secrète générée automatiquement. TorrServer n'a pas d'authentification : à garder sur le réseau local.

## Limites
- La liste des versions est commune ; seul le filtrage est par utilisateur. Les lecteurs externes qui lisent le `.strm` directement utilisent les réglages globaux.
- Le transcodage (HEVC, etc.) se fait au processeur sauf accélération matérielle configurée dans Jellyfin.

## Compiler
```sh
dotnet build src -c Release       # .NET 10, Jellyfin 12.4
./deploy.sh                       # installe dans le Jellyfin local
python3 package.py                # dist/TorrServer-<version>.zip + manifest.json
```
