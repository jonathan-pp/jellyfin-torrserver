using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.TorrServer;

/// <summary>Sources proposées à l'écran, retrouvées au moment de la lecture.</summary>
public sealed class SourceStore
{
    private readonly ConcurrentDictionary<string, Release> _items = new();
    private bool _loaded;
    private static string FilePath => Path.Combine(Plugin.Instance!.DataFolderPath, "sources.json");

    /// <summary>
    /// Identifiant stable d'une source : Prowlarr génère un lien de téléchargement différent à chaque recherche,
    /// le lien ne peut donc pas servir à la reconnaître. On garde le dernier lien reçu.
    /// </summary>
    public string Put(Release r)
    {
        Load();
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(r.Indexer + "|" + r.Title + "|" + r.Size))).ToLowerInvariant()[..20];
        _items[id] = r;
        return id;
    }

    public Release? Get(string id) { Load(); return _items.GetValueOrDefault(id); }

    private int _savePending;

    /// <summary>Écrit le fichier au plus une fois toutes les 10 s. Une source absente repasse simplement en choix automatique.</summary>
    public void Save()
    {
        if (Interlocked.Exchange(ref _savePending, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            Interlocked.Exchange(ref _savePending, 0);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(_items.ToDictionary(x => x.Key, x => x.Value)));
                File.Move(FilePath + ".tmp", FilePath, true);
            }
            catch { /* best effort */ }
        });
    }

    private void Load()
    {
        if (_loaded) return;
        lock (this)
        {
            if (_loaded) return;
            try
            {
                if (File.Exists(FilePath))
                    foreach (var kv in JsonSerializer.Deserialize<Dictionary<string, Release>>(File.ReadAllText(FilePath)) ?? []) _items.TryAdd(kv.Key, kv.Value);
            }
            catch { }
            _loaded = true;
        }
    }
}
