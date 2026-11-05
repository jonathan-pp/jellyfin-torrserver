using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.TorrServer;

public sealed record MediaRef(string Kind, int TmdbId, int Season = 0, int Episode = 0)
{
    public bool IsTv => Kind == "tv";
    public string Key => IsTv ? $"tv:{TmdbId}:{Season}:{Episode}" : $"movie:{TmdbId}";
}

public sealed record Scored(Release Release, int Score);

public static class Ranking
{
    private static readonly string[] VideoExt = [".mkv", ".mp4", ".avi", ".m4v", ".ts", ".m2ts", ".webm", ".mov", ".wmv"];
    public static bool IsVideo(string path) => VideoExt.Contains(System.IO.Path.GetExtension(path).ToLowerInvariant());

    public static string Normalize(string s)
    {
        var d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
        {
            var cat = CharUnicodeInfo.GetUnicodeCategory(c);
            if (cat == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }

    public static bool HasTag(string title, string tag) =>
        tag.Length > 0 && Regex.IsMatch(title, $@"(?i)(?<![a-z0-9]){Regex.Escape(tag)}(?![a-z0-9])");

    public static int Resolution(string title)
    {
        var m = Regex.Match(title, @"(?i)(?<![a-z0-9])(2160p|4k|uhd|1080[pi]|720p|576p|480p)(?![a-z0-9])");
        if (!m.Success) return 0;
        var v = m.Value.ToLowerInvariant();
        return v is "2160p" or "4k" or "uhd" ? 2160 : v.StartsWith("1080") ? 1080 : v == "720p" ? 720 : v == "576p" ? 576 : 480;
    }

    public static bool TitleMatches(string releaseTitle, string wanted)
    {
        var w = Normalize(wanted);
        if (w.Length == 0) return false;
        var r = Normalize(Regex.Replace(releaseTitle, @"^\s*(\[[^\]]*\]|\([^)]*\)|www\.\S+|\S+\.(com|org|net|to)\b)[\s._-]*", "", RegexOptions.IgnoreCase));
        if ((r + " ").StartsWith(w + " ", StringComparison.Ordinal)) return true;
        // tolérance : quelques mots insérés (« Special 1 », « Part 2 »…) entre les mots du titre recherché
        var ww = w.Split(' '); if (ww.Length < 3) return false;
        var rw = r.Split(' '); int j = 0, extra = 0;
        for (var i = 0; i < rw.Length && j < ww.Length; i++)
        {
            if (rw[i] == ww[j]) j++;
            else if (j > 0 && ++extra > 3) return false;
            else if (j == 0) return false;
        }
        return j == ww.Length;
    }

    private static readonly Regex SxE = new(@"(?i)(?<![a-z0-9])s(\d{1,3})[ ._-]?e(\d{1,4})(?:[ ._-]?e?(\d{1,4}))?(?![a-z0-9])");
    private static readonly Regex NxM = new(@"(?i)(?<![a-z0-9])(\d{1,2})x(\d{2,3})(?![a-z0-9])");
    private static readonly Regex SeasonOnly = new(@"(?i)(?<![a-z0-9])(?:s(\d{1,3})(?:[ ._-]?s?(\d{1,3}))?|(?:saison|season)[ ._-]?(\d{1,3}))(?![a-z0-9e])");

    public static bool MatchesEpisode(string text, int season, int episode)
    {
        foreach (Match m in SxE.Matches(text))
            if (int.Parse(m.Groups[1].Value) == season && (int.Parse(m.Groups[2].Value) == episode || (m.Groups[3].Success && int.Parse(m.Groups[3].Value) >= episode && int.Parse(m.Groups[2].Value) <= episode)))
                return true;
        foreach (Match m in NxM.Matches(text))
            if (int.Parse(m.Groups[1].Value) == season && int.Parse(m.Groups[2].Value) == episode) return true;
        return false;
    }

    /// <summary>Numéro absolu dans le nom du fichier : « Episode 183 », « 181-185 » (ignore SxxExx, résolutions, codecs).</summary>
    public static bool MatchesAbsolute(string path, int abs)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (SxE.IsMatch(name) || NxM.IsMatch(name)) return false;
        var lead = Regex.Match(name, @"^\s*(?:e(?:p(?:isode)?)?[ ._-]?)?0*(\d{1,4})(?![0-9a-z])[ ._-]");   // « 1 Titre », « 03 - Titre », « Episode 3 »
        if (lead.Success && int.Parse(lead.Groups[1].Value) == abs) return true;
        name = Regex.Replace(name, @"(?i)(2160|1080|720|576|480)[pi]?|[hx]\.?26[45]|\d+\s*bits?|\d+\.\d(?!\d)|(19|20)\d{2}|\d+\s*(mb|gb|fps|kbps)", " ");
        foreach (Match m in Regex.Matches(name, @"(?<![0-9])(\d{2,4})(?:\s*[-~]\s*(\d{2,4}))?(?![0-9])"))
        {
            var a = int.Parse(m.Groups[1].Value);
            var b = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : a;
            if (abs >= a && abs <= b && b - a < 30) return true;
        }
        return false;
    }

    /// <summary>0 = rejet, 1 = indéterminé, 2 = pack de la saison, 3 = épisode exact.</summary>
    public static int TvFit(string title, int season, int episode)
    {
        if (MatchesEpisode(title, season, episode)) return 3;
        if (SxE.IsMatch(title) || NxM.IsMatch(title)) return 0;           // autre épisode précis
        var any = false;
        foreach (Match m in SeasonOnly.Matches(title))
        {
            any = true;
            int a = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : int.Parse(m.Groups[3].Value);
            int b = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : a;
            if (season >= Math.Min(a, b) && season <= Math.Max(a, b)) return 2;
        }
        return any ? 0 : 1;
    }

    public static IReadOnlyList<Scored> Rank(IEnumerable<Release> releases, MediaRef media, IReadOnlyList<string> titles, int year, ISet<string> excluded, ISet<string>? trusted = null, int runtimeMin = 0, SourcePrefs? prefs = null)
    {
        var cfg = prefs ?? Access.Prefs(null);
        var preferred = cfg.Preferred;
        var avoid = cfg.Avoid;
        var list = new List<Scored>();
        foreach (var r in releases.Where(x => Access.Ok(cfg.Indexers, x.Indexer)).OrderByDescending(x => x.Seeders).DistinctBy(x => x.Link).DistinctBy(x => (Normalize(x.Title), x.Size / 1048576, x.Indexer)))
        {
            if (excluded.Contains(r.Link) || excluded.Contains(r.Title)) continue;
            var sure = trusted is not null && trusted.Contains(r.Link);   // trouvé par identifiant TMDB
            if (!sure && !titles.Any(t => TitleMatches(r.Title, t))) continue;
            var score = 0;
            if (media.IsTv)
            {
                var fit = TvFit(r.Title, media.Season, media.Episode);
                if (fit == 0) continue;
                score += fit switch { 3 => 40, 2 => 15, _ => 0 };
            }
            else if (year > 0 && !sure)
            {
                var years = Regex.Matches(r.Title, @"(?<![0-9])(19|20)\d{2}(?![0-9])").Select(m => int.Parse(m.Value)).ToList();
                if (years.Count > 0 && !years.Any(y => Math.Abs(y - year) <= 1)) continue;   // autre film du même nom
                if (years.Count > 0) score += 10;
            }
            if (preferred.Any(t => HasTag(r.Title, t))) score += 60;
            if (avoid.Any(t => HasTag(r.Title, t))) score -= 300;
            var res = Resolution(r.Title);
            if (res == 0) score += 5;
            else if (res > cfg.MaxResolution) score -= 40;
            else score += res switch { >= 2160 => 55, >= 1080 => 50, >= 720 => 35, >= 576 => 15, _ => 5 };
            if (r.Seeders < cfg.MinSeeders) score -= 120;
            score += (int)Math.Min(50, Math.Log2(r.Seeders + 1) * 8);
            var gb = r.Size / 1073741824.0;
            var max = media.IsTv ? cfg.MaxEpisodeSizeGb : cfg.MaxMovieSizeGb;
            if (media.IsTv && TvFit(r.Title, media.Season, media.Episode) == 2) max *= 12;   // un pack est plus gros
            if (gb > max) score -= 60;
            // gros fichier = débit élevé à tirer d'un torrent : on préfère raisonnable
            if (!media.IsTv)
            {
                score -= (int)Math.Clamp((gb - 4) * 3, 0, 45);
                if (runtimeMin > 30 && r.Size > 0) { var mbps = r.Size * 8.0 / (runtimeMin * 60) / 1e6; score -= (int)Math.Clamp((mbps - 8) * 5, 0, 40); }
            }
            if (r.Size > 0 && gb < (media.IsTv ? 0.05 : 0.3)) score -= 40;
            // H.264 : lu par tous les navigateurs/téléphones sans conversion ; HEVC/AV1/10 bits : le serveur doit convertir (lent, 4 cœurs).
            if (cfg.PreferH264)
            {
                if (HasTag(r.Title, "x264") || HasTag(r.Title, "h264") || HasTag(r.Title, "avc") || HasTag(r.Title, "264")) score += 40;
                else if (HasTag(r.Title, "x265") || HasTag(r.Title, "h265") || HasTag(r.Title, "hevc") || HasTag(r.Title, "265")) score -= 30;
                else if (HasTag(r.Title, "av1")) score -= 45;
                if (HasTag(r.Title, "10bit") || HasTag(r.Title, "hdr") || HasTag(r.Title, "hdr10") || HasTag(r.Title, "dv")) score -= 15;
            }
            if (HasTag(r.Title, "remux") || HasTag(r.Title, "bluray") && gb > 20) score -= 10;
            list.Add(new Scored(r, score));
        }
        return list.OrderByDescending(x => x.Score).ThenByDescending(x => x.Release.Seeders).ToList();
    }
}
