# Contribuer

Merci de votre intérêt ! Les rapports de bugs, idées et pull requests sont les bienvenus (en français ou en anglais).

## Signaler un bug

Ouvrez une [issue](https://github.com/jonathan-pp/jellyfin-torrserver/issues/new/choose) avec :
- les versions de Jellyfin, du plugin, de TorrServer et de Prowlarr ;
- le client utilisé (web, Android, Android TV…) ;
- les étapes pour reproduire ;
- les lignes utiles du journal de Jellyfin (filtrer sur `TorrServer`).

Retirez de vos journaux les clés API, adresses IP publiques et noms de trackers privés.

## Proposer une modification

1. Créez une branche à partir de `main`.
2. Compilez : `dotnet build src -c Release` (.NET 10, Jellyfin 12.4).
3. Testez dans un Jellyfin local (`./deploy.sh` sous Linux avec systemd).
4. Ouvrez une pull request qui explique le problème et la solution.

## Organisation du code

| Fichier | Rôle |
|---|---|
| `src/Clients.cs` | Accès HTTP à TMDB, Prowlarr et TorrServer |
| `src/Resolver.cs`, `src/Ranking.cs` | Recherche et classement des sources |
| `src/LibraryService.cs` | Fichiers `.strm`, catalogue, bibliothèques |
| `src/VersionService.cs` | Menu « Version », préparation des épisodes |
| `src/StreamController.cs` | Relais du flux, quota |
| `src/PrivacyService.cs` | Bibliothèque personnelle (étiquettes bloquées) |
| `src/ScheduleService.cs` | Prochains épisodes, avertissements, agenda `.ics` |
| `src/AniSkipProvider.cs` | « Passer l'intro » des animes |
| `src/Web/inject.js` | Pages « Découvrir » et « À venir », recherche TMDB |
| `src/Configuration/configPage.html` | Page de configuration |

## Style

- Code et commentaires en français, comme le reste du projet.
- Pas de dépendance NuGet supplémentaire sans bonne raison.
- Rien de personnel dans le dépôt (adresses, clés, noms d'utilisateurs).
