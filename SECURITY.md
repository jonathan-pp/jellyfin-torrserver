# Sécurité

## Signaler une faille

Merci de **ne pas** ouvrir d'issue publique pour une faille de sécurité. Utilisez plutôt le [signalement privé de GitHub](https://github.com/jonathan-pp/jellyfin-torrserver/security/advisories/new).

Une réponse est donnée dès que possible. Seule la dernière version publiée reçoit des correctifs.

## Bonnes pratiques

- **TorrServer n'a pas d'authentification par défaut** : gardez-le sur le réseau local ou derrière un VPN, jamais exposé sur Internet.
- Les adresses de lecture des fichiers `.strm` contiennent une clé secrète générée automatiquement. Ne partagez pas ces fichiers.
- Le lien d'agenda `.ics` d'un utilisateur contient une clé personnelle : ne le donnez qu'à la personne concernée.
- Les clés TMDB et Prowlarr restent dans la configuration du plugin, sur votre serveur.
