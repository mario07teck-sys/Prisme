using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Prisme.Core;

/// <summary>Mises à jour depuis les « releases » GitHub du dépôt public : vérifier, télécharger l'installeur, l'installer en silence.</summary>
public static class Updater
{
    public const string Repo = "mario07teck-sys/Prisme";
    public const string Page = "https://github.com/" + Repo + "/releases/latest";

    public record Release(Version Version, string Url, long Size, string Sha256);

    static readonly HttpClient Http = CreateClient();
    static HttpClient CreateClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Prisme/" + Current);
        return h;
    }

    public static Version Current => Norm(typeof(Updater).Assembly.GetName().Version);
    static Version Norm(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));
    static string Dir => Path.Combine(Settings.Dir, "updates");

    /// <summary>Dernière version publiée, ou null (pas de réseau, pas de release, pas d'installeur joint).</summary>
    public static async Task<Release> Latest(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repo}/releases/latest");
        req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await Http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        return Parse(doc.RootElement);
    }

    internal static Release Parse(JsonElement rel)
    {
        var tag = rel.TryGetProperty("tag_name", out var t) ? t.GetString() : "";
        if (!Version.TryParse(Regex.Match(tag ?? "", @"\d+\.\d+(\.\d+)?").Value, out var v)) return null;
        if (!rel.TryGetProperty("assets", out var assets)) return null;
        foreach (var a in assets.EnumerateArray())
        {
            var name = a.GetProperty("name").GetString() ?? "";
            var url = a.GetProperty("browser_download_url").GetString() ?? "";
            // seul un installeur servi par GitHub lui-même est accepté
            if (!name.StartsWith("Prisme-Setup", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                || !Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https" || u.Host != "github.com") continue;
            var digest = a.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String ? dg.GetString() : null;
            var sha = digest != null && digest.StartsWith("sha256:") ? digest[7..].ToLowerInvariant() : null;
            return new Release(Norm(v), url, a.GetProperty("size").GetInt64(), sha);
        }
        return null;
    }

    public static bool IsNewer(Release r) => r != null && r.Version > Current;

    /// <summary>Télécharge l'installeur (reprend celui déjà téléchargé s'il est complet) et vérifie taille et empreinte.</summary>
    public static async Task<string> Download(Release r, Action<double> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Dir);
        var path = Path.Combine(Dir, $"Prisme-Setup-{r.Version}.exe");
        if (File.Exists(path) && Verify(path, r)) return path;
        var part = path + ".part";
        try
        {
            using (var resp = await Http.GetAsync(r.Url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(part);
                var buf = new byte[1 << 16]; long done = 0; int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n; progress?.Invoke(r.Size > 0 ? done * 100.0 / r.Size : 0);
                }
            }
            if (!Verify(part, r)) throw new Exception("Installeur téléchargé incomplet ou modifié");
            File.Move(part, path, true);
        }
        finally { try { File.Delete(part); } catch { } }
        foreach (var old in Directory.GetFiles(Dir, "Prisme-Setup-*.exe").Where(f => !f.Equals(path, StringComparison.OrdinalIgnoreCase)))
            try { File.Delete(old); } catch { }
        return path;
    }

    static bool Verify(string file, Release r)
    {
        if (new FileInfo(file).Length != r.Size) return false;
        if (r.Sha256 == null) return true; // anciennes releases sans empreinte publiée : la taille et l'HTTPS de GitHub suffisent
        using var fs = File.OpenRead(file);
        return Convert.ToHexString(SHA256.HashData(fs)).Equals(r.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Lance l'installeur en silence ; il ferme Prisme, met à jour, puis le relance (« --show » ou « --tray », ou rien avec « none »).</summary>
    public static void Install(string setup, string relaunch)
    {
        Process.Start(new ProcessStartInfo(setup, $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /relaunch={relaunch}") { UseShellExecute = false });
    }

    /// <summary>Supprime les installeurs déjà appliqués (même version ou plus ancienne).</summary>
    public static void Cleanup()
    {
        try
        {
            if (!Directory.Exists(Dir)) return;
            foreach (var f in Directory.GetFiles(Dir))
                if (!Version.TryParse(Regex.Match(Path.GetFileName(f), @"\d+\.\d+\.\d+").Value, out var v) || v <= Current) File.Delete(f);
        }
        catch { }
    }
}
