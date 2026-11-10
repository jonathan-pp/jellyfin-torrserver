using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TorrServer;

/// <summary>Contenu reçu du plugin « File Transformation ».</summary>
public sealed class PatchPayload
{
    public string? Contents { get; set; }
}

/// <summary>
/// Ajoute la page Découvrir et la recherche TMDB dans l'interface web de Jellyfin via le plugin « File Transformation »,
/// sans modifier les fichiers de Jellyfin.
/// </summary>
public sealed class WebInjector(ILogger<WebInjector> log) : IHostedService
{
    private static readonly Guid TransformId = Guid.Parse("8f3c2a71-6d0e-4b8a-9b6f-1c2d3e4f5a6b");
    public const string ScriptTag = "<script src=\"../TorrServerPlugin/app/inject.js\" defer></script>";

    public async Task StartAsync(CancellationToken ct)
    {
        // « File Transformation » peut être chargé après nous : on réessaie quelques secondes.
        for (var i = 0; i < 30 && !ct.IsCancellationRequested; i++)
        {
            if (TryRegister()) return;
            await Task.Delay(1000, ct);
        }
        log.LogWarning("Plugin « File Transformation » absent : la page Découvrir et la recherche TMDB ne seront pas ajoutées à Jellyfin.");
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;

    private bool TryRegister()
    {
        var asm = AssemblyLoadContext.All.SelectMany(x => x.Assemblies).FirstOrDefault(x => x.FullName?.Contains(".FileTransformation", StringComparison.Ordinal) ?? false);
        var iface = asm?.GetType("Jellyfin.Plugin.FileTransformation.PluginInterface");
        var register = iface?.GetMethod("RegisterTransformation");
        if (register is null) return false;
        var jobjectType = register.GetParameters()[0].ParameterType;          // Newtonsoft JObject du contexte du plugin
        var parse = jobjectType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, [typeof(string)]);
        if (parse is null) return false;
        var json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["id"] = TransformId.ToString(),
            ["fileNamePattern"] = @"(^|/)index\.html$",
            ["callbackAssembly"] = typeof(WebInjector).Assembly.FullName!,
            ["callbackClass"] = typeof(WebInjector).FullName!,
            ["callbackMethod"] = nameof(Transform),
        });
        register.Invoke(null, [parse.Invoke(null, [json])]);
        log.LogInformation("Page Découvrir ajoutée à l'interface web de Jellyfin (via File Transformation).");
        return true;
    }

    /// <summary>Appelé par File Transformation pour chaque index.html servi.</summary>
    public static string Transform(PatchPayload payload)
    {
        var html = payload.Contents ?? "";
        if (html.Contains("TorrServerPlugin/app/inject.js", StringComparison.Ordinal)) return html;
        // Sécurité : on ne touche qu'à une vraie page HTML.
        var i = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return i < 0 ? html : html.Insert(i, ScriptTag);
    }
}
