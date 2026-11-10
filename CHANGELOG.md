# Historique des versions

Format inspiré de [Keep a Changelog](https://keepachangelog.com/fr/1.1.0/).

## 0.5.0 - 2026-11-10

### Ajouté
- **Bibliothèque personnelle** : chaque utilisateur ne voit que les titres qu'il a ouverts (listes, recherche, « Reprendre »).
- **Prochains épisodes** : date en tête du résumé de la série, heure exacte des animes via AniList, avertissement à l'écran le jour de la sortie et préparation de l'épisode suivant.
- **Page « À venir »** dans le menu de Jellyfin.
- **Onglet « Calendrier »** dans la configuration et **agenda `.ics`** par utilisateur.
- **Genres pour les animes** et catégorie **« En production »** (Séries et Animes) dans « Découvrir ».
- Épisode à venir visible dans la saison (« pas encore diffusé »).

### Corrigé
- Aucune recherche de source pour un épisode pas encore sorti.
- Les étiquettes internes de confidentialité ne s'affichent plus dans les fiches.
- Logo du plugin manquant dans la liste des extensions.

## 0.4.0 - 2026-11-09

### Ajouté
- Bibliothèque « Animes » séparée et saisons redécoupées (Re:ZERO…).
- « Passer l'intro / le générique » pour les animes (AniSkip).
- Quota mensuel par utilisateur avec avertissement à 80 %.
- Statistiques d'usage, nettoyage automatique des titres non regardés.
- Sources affichées au fur et à mesure, fiches acteur et recommandations TMDB.
- Sous-titres extraits à l'avance, choix des langues par cases, configuration en onglets.

## 0.3.0 - 2026-11-05

### Modifié
- Interface dédiée retirée : tout passe par Jellyfin.
- Passage automatique à la source suivante si une source est morte.
- Épisode suivant préparé à l'avance, versions stables, moins de charge.

## 0.2.0 - 2026-11-05

### Ajouté
- Première version publique : streaming de torrents via TorrServer et Prowlarr dans Jellyfin.
