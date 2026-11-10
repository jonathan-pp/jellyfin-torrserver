using System.Text.Json;

namespace Jellyfin.Plugin.TorrServer;

/// <summary>Données lues par utilisateur et par titre (octets réellement envoyés au lecteur), avec l'historique des mois passés.</summary>
public sealed class UsageService
{
    private readonly object _lock = new();
    private bool _loaded;
    private Store _s = new();
    private DateTime _saved = DateTime.MinValue;
    private static string FilePath => Path.Combine(Plugin.Instance!.DataFolderPath, "usage.json");
    private static string Now => DateTime.UtcNow.ToString("yyyy-MM");

    public sealed class TitleUsage { public long Bytes { get; set; } public HashSet<string> Users { get; set; } = []; }

    public sealed class Store
    {
        public string Month { get; set; } = "";
        public Dictionary<string, long> Bytes { get; set; } = new();
        public Dictionary<string, TitleUsage> Titles { get; set; } = new();
        /// <summary>Utilisateurs déjà prévenus à 80 % ce mois-ci.</summary>
        public HashSet<string> Warned { get; set; } = [];
        /// <summary>Mois passés : mois → utilisateur → octets (12 mois gardés).</summary>
        public Dictionary<string, Dictionary<string, long>> History { get; set; } = new();
    }

    private void Sync()
    {
        if (!_loaded)
        {
            _loaded = true;
            try { if (File.Exists(FilePath) && JsonSerializer.Deserialize<Store>(File.ReadAllText(FilePath)) is { } s) _s = s; }
            catch (Exception) { }
        }
        if (_s.Month != Now)
        {
            if (_s.Month != "" && _s.Bytes.Count > 0) _s.History[_s.Month] = _s.Bytes;
            foreach (var old in _s.History.Keys.OrderByDescending(x => x).Skip(12).ToList()) _s.History.Remove(old);
            _s.Month = Now; _s.Bytes = new(); _s.Titles = new(); _s.Warned = [];
        }
    }

    public long Used(Guid user) { lock (_lock) { Sync(); return _s.Bytes.GetValueOrDefault(user.ToString("N")); } }

    /// <summary>Instantané pour l'affichage.</summary>
    public Store Snapshot()
    {
        lock (_lock)
        {
            Sync();
            return JsonSerializer.Deserialize<Store>(JsonSerializer.Serialize(_s))!;
        }
    }

    /// <summary>Ajoute des octets ; renvoie true la première fois que l'utilisateur dépasse 80 % de son quota ce mois-ci.</summary>
    public bool Add(Guid user, long n, string? title, long quota)
    {
        if (n <= 0) return false;
        lock (_lock)
        {
            Sync();
            var k = user.ToString("N");
            var total = _s.Bytes[k] = _s.Bytes.GetValueOrDefault(k) + n;
            if (title is not null)
            {
                if (!_s.Titles.TryGetValue(title, out var t)) _s.Titles[title] = t = new TitleUsage();
                t.Bytes += n; t.Users.Add(k);
            }
            if (DateTime.UtcNow - _saved > TimeSpan.FromSeconds(15)) Save();
            if (quota > 0 && total >= quota * 0.8 && _s.Warned.Add(k)) { Save(); return true; }
            return false;
        }
    }

    public void Reset(Guid? user)
    {
        lock (_lock)
        {
            Sync();
            if (user is { } u) { _s.Bytes.Remove(u.ToString("N")); _s.Warned.Remove(u.ToString("N")); }
            else { _s.Bytes.Clear(); _s.Warned.Clear(); }
            Save();
        }
    }

    public void Save()
    {
        lock (_lock)
        {
            _saved = DateTime.UtcNow;
            try { File.WriteAllText(FilePath, JsonSerializer.Serialize(_s)); } catch (Exception) { }
        }
    }

    /// <summary>Quota mensuel en octets pour cet utilisateur (0 = illimité).</summary>
    public static long Quota(Guid? user)
    {
        var gb = Access.Profile(user)?.QuotaGb ?? Clients.Config.MonthlyQuotaGb;
        return gb > 0 ? gb * 1_000_000_000L : 0;
    }
}
