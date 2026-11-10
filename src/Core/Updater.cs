using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Prisme.Core;

/// <summary>Mises à jour depuis les « releases » GitHub du dépôt public : vérifier, télécharger l'installeur, l'installer en silence.</summary>
public static class Updater
{
    public const string Repo = "mario07teck-sys/Prisme";
    public const string Page = "https://github.com/" + Repo + "/releases/latest";

    public record Release(Version Version, string Url, long Size, string Sha256, string Name);

    /// <summary>Empreinte des moteurs installés (engines.id, écrit par build.ps1) : si elle est identique, la mise à jour légère sans moteurs suffit.</summary>
    public static string EnginesId { get; } = ReadEnginesId();
    static string ReadEnginesId()
    {
        try { var id = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "engines.id")).Trim(); return id.Length > 0 ? id : null; }
        catch { return null; }
    }

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
        return Parse(doc.RootElement, EnginesId);
    }

    /// <summary>Choisit la mise à jour légère « Prisme-Update-x.y.z-&lt;enginesId&gt;.exe » si les moteurs installés sont les mêmes, sinon l'installeur complet.</summary>
    internal static Release Parse(JsonElement rel, string enginesId)
    {
        var tag = rel.TryGetProperty("tag_name", out var t) ? t.GetString() : "";
        if (!Version.TryParse(Regex.Match(tag ?? "", @"\d+\.\d+(\.\d+)?").Value, out var v)) return null;
        if (!rel.TryGetProperty("assets", out var assets)) return null;
        Release full = null;
        foreach (var a in assets.EnumerateArray())
        {
            var name = Path.GetFileName(a.GetProperty("name").GetString() ?? "");
            var url = a.GetProperty("browser_download_url").GetString() ?? "";
            var light = enginesId != null && name.StartsWith("Prisme-Update-", StringComparison.OrdinalIgnoreCase)
                        && name.EndsWith($"-{enginesId}.exe", StringComparison.OrdinalIgnoreCase);
            var setup = name.StartsWith("Prisme-Setup", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
            // seul un installeur servi par GitHub lui-même est accepté
            if (!(light || setup) || !Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != "https" || u.Host != "github.com") continue;
            var digest = a.TryGetProperty("digest", out var dg) && dg.ValueKind == JsonValueKind.String ? dg.GetString() : null;
            var sha = digest != null && digest.StartsWith("sha256:") ? digest[7..].ToLowerInvariant() : null;
            var r = new Release(Norm(v), url, a.GetProperty("size").GetInt64(), sha, name);
            if (light) return r;
            full ??= r;
        }
        return full;
    }

    public static bool IsNewer(Release r) => r != null && r.Version > Current;

    /// <summary>Télécharge l'installeur et vérifie taille et empreinte. Connexion lente ou coupée : le morceau déjà reçu (.part) est gardé
    /// et le téléchargement reprend là où il s'était arrêté, ici ou à la prochaine vérification.</summary>
    public static async Task<string> Download(Release r, Action<double> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Dir);
        var path = Path.Combine(Dir, r.Name);
        if (File.Exists(path) && Verify(path, r)) return path;
        var part = path + ".part";
        for (int attempt = 1; ; attempt++)
        {
            try { await Fetch(r, part, progress, ct); break; }
            catch (Exception) when (attempt < 10 && !ct.IsCancellationRequested) { await Task.Delay(TimeSpan.FromSeconds(30), ct); }
        }
        if (!Verify(part, r)) { try { File.Delete(part); } catch { } throw new Exception("Installeur téléchargé incomplet ou modifié"); }
        File.Move(part, path, true);
        foreach (var old in Directory.GetFiles(Dir).Where(f => !f.Equals(path, StringComparison.OrdinalIgnoreCase)))
            try { File.Delete(old); } catch { }
        return path;
    }

    static async Task Fetch(Release r, string part, Action<double> progress, CancellationToken ct)
    {
        long done = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (done > r.Size) { File.Delete(part); done = 0; }
        if (done == r.Size) return;
        using var req = new HttpRequestMessage(HttpMethod.Get, r.Url);
        if (done > 0) req.Headers.Range = new RangeHeaderValue(done, null);
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        // serveur qui renvoie tout le fichier au lieu de la suite : on repart de zéro
        if (resp.StatusCode != HttpStatusCode.PartialContent || resp.Content.Headers.ContentRange?.From != done) done = 0;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(part, done > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write);
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var buf = new byte[1 << 16]; int n;
        while (true)
        {
            stall.CancelAfter(TimeSpan.FromMinutes(2)); // plus rien ne vient depuis 2 min : on coupe et on reprend
            if ((n = await src.ReadAsync(buf, stall.Token)) == 0) break;
            await dst.WriteAsync(buf.AsMemory(0, n), ct);
            done += n; progress?.Invoke(r.Size > 0 ? done * 100.0 / r.Size : 0);
        }
        if (done < r.Size) throw new IOException("Téléchargement interrompu"); // connexion fermée trop tôt : on reprendra
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
