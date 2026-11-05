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

    public string Put(Release r)
    {
        Load();
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(r.Link))).ToLowerInvariant()[..20];
        if (_items.TryAdd(id, r) && _items.Count % 50 == 0) Save();
        return id;
    }

    public Release? Get(string id) { Load(); return _items.GetValueOrDefault(id); }

    public void Save()
    {
        try
        {
            var keep = _items.TakeLast(5000).ToDictionary(x => x.Key, x => x.Value);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(keep));
        }
        catch { /* best effort */ }
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
