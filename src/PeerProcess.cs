using System.Globalization;
using System.Net;

namespace Jellyfin.Plugin.TorrServer;

/// <summary>
/// Retrouve le programme local qui a ouvert une connexion HTTP (Linux : /proc).
/// Sert à reconnaître les analyses ffprobe lancées automatiquement par Jellyfin sur chaque nouveau .strm :
/// chacune chargerait un torrent pour rien.
/// </summary>
public static class PeerProcess
{
    /// <summary>Vrai si la connexion vient d'un processus ffprobe de cette machine.</summary>
    public static bool IsFfprobe(IPAddress? remote, int remotePort)
    {
        try
        {
            if (!OperatingSystem.IsLinux() || remote is null || remotePort <= 0) return false;
            var probes = Directory.EnumerateDirectories("/proc")
                .Where(d => int.TryParse(Path.GetFileName(d), out _))
                .Where(d => { try { return File.ReadAllText(Path.Combine(d, "comm")).Trim() == "ffprobe"; } catch { return false; } })
                .ToList();
            if (probes.Count == 0) return false;
            var inode = SocketInode(remotePort);
            if (inode is null) return false;
            var target = $"socket:[{inode}]";
            foreach (var p in probes)
            {
                try
                {
                    foreach (var fd in Directory.EnumerateFileSystemEntries(Path.Combine(p, "fd")))
                    {
                        string? link = null;
                        try { link = new FileInfo(fd).LinkTarget; } catch { }
                        if (link == target) return true;
                    }
                }
                catch { /* processus terminé entre-temps */ }
            }
        }
        catch { }
        return false;
    }

    /// <summary>Inode de la socket cliente dont le port local est <paramref name="port"/>.</summary>
    private static string? SocketInode(int port)
    {
        var hex = port.ToString("X4", CultureInfo.InvariantCulture);
        foreach (var table in new[] { "/proc/net/tcp", "/proc/net/tcp6" })
        {
            if (!File.Exists(table)) continue;
            foreach (var line in File.ReadLines(table).Skip(1))
            {
                var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 10) continue;
                var local = f[1];
                if (local.EndsWith(":" + hex, StringComparison.Ordinal) && f[9] != "0") return f[9];
            }
        }
        return null;
    }
}
