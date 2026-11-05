using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.TorrServer;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths paths, IXmlSerializer serializer) : base(paths, serializer) => Instance = this;
    public static Plugin? Instance { get; private set; }
    public override string Name => "TorrServer";
    public override string Description => "Films et séries en streaming via Prowlarr + TorrServer, sans rien télécharger sur le disque.";
    public override Guid Id => Guid.Parse("655fbf1b-525b-4f82-bdeb-f27dd0bdd606");

    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name = Name,
            DisplayName = "TorrServer",
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
            EnableInMainMenu = true,
            MenuSection = "server",
            MenuIcon = "cloud_download",
        },
    ];
}

public sealed class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Adresse de Jellyfin telle que les lecteurs la voient (écrite dans les fichiers .strm).</summary>
    public string JellyfinInternalUrl { get; set; } = "http://127.0.0.1:8096";
    public string TmdbReadAccessToken { get; set; } = "";
    public string TmdbApiKey { get; set; } = "";
    public string ProwlarrUrl { get; set; } = "http://127.0.0.1:9696";
    public string ProwlarrApiKey { get; set; } = "";
    public string TorrServerUrl { get; set; } = "http://127.0.0.1:8090";
    public string TorrServerUsername { get; set; } = "";
    public string TorrServerPassword { get; set; } = "";

    /// <summary>Dossier racine de la bibliothèque virtuelle (vide = dossier de données de Jellyfin).</summary>
    public string LibraryRoot { get; set; } = "";
    public string MetadataLanguage { get; set; } = "fr-FR";

    /// <summary>Mots-clés de langue préférés dans le titre des sources, séparés par des virgules.</summary>
    public string PreferredTags { get; set; } = "multi,vff,vf,vf2,vfq,vfi,truefrench,french";
    /// <summary>Mots-clés à éviter (mauvaise qualité).</summary>
    public string AvoidTags { get; set; } = "cam,hdcam,camrip,ts,hdts,telesync,tc,telecine,scr,screener,workprint,r5";
    /// <summary>Préfère les sources H.264 (lecture directe) aux sources HEVC / AV1 / 10 bits (conversion par le serveur).</summary>
    public bool PreferH264 { get; set; } = true;
    public int MaxResolution { get; set; } = 1080;
    public int MinSeeders { get; set; } = 2;
    public int MaxMovieSizeGb { get; set; } = 30;
    public int MaxEpisodeSizeGb { get; set; } = 8;
    public int CandidateTimeoutSeconds { get; set; } = 20;
    public int MaxCandidates { get; set; } = 6;
    /// <summary>Nombre de sources affichées par titre dans la chaîne TMDB.</summary>
    public int MaxResults { get; set; } = 25;

    /// <summary>Remplit seul la bibliothèque avec les titres populaires et les mieux notés de TMDB.</summary>
    public bool AutoCatalog { get; set; } = false;
    /// <summary>Pages TMDB (20 titres chacune) lues par liste : 5 ≈ 100 titres par liste.</summary>
    public int AutoPages { get; set; } = 5;
    /// <summary>Plafond d'épisodes par série du catalogue automatique (saisons entières, en partant de la 1re). 0 = illimité.</summary>
    public int AutoMaxEpisodes { get; set; } = 100;

    /// <summary>Secret placé dans les URL de lecture (généré automatiquement).</summary>
    public string StreamKey { get; set; } = "";

    /// <summary>Trackers autorisés par utilisateur (JSON : {"idUtilisateur": ["C411", "YTS"]}). Absent = tous.</summary>
    public string UserIndexers { get; set; } = "";

    /// <summary>Profils par utilisateur (JSON : {"idUtilisateur": UserProfile}).</summary>
    public string UserProfiles { get; set; } = "";
}
