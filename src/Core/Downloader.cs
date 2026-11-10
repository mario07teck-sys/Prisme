using System.Globalization;
using System.Text.RegularExpressions;

namespace Prisme.Core;

/// <summary>Téléchargement de vidéos/sons depuis un lien (YouTube, X, TikTok, Instagram…) via yt-dlp.</summary>
public static class Downloader
{
    static readonly Regex Pct = new(@"^\[download\]\s+([\d.]+)%", RegexOptions.Compiled);
    static readonly Regex UrlRx = new(@"^https?://\S+$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly string[] Sites =
    {
        "youtube.com", "youtu.be", "x.com", "twitter.com", "tiktok.com", "instagram.com", "facebook.com", "fb.watch",
        "vimeo.com", "dailymotion.com", "twitch.tv", "reddit.com", "soundcloud.com", "bsky.app", "threads.net", "bilibili.com",
    };

    public static bool IsUrl(string s) => !string.IsNullOrWhiteSpace(s) && UrlRx.IsMatch(s.Trim());

    /// <summary>Lien d'un site vidéo connu (pour la détection du presse-papiers).</summary>
    public static bool IsVideoLink(string s)
    {
        if (!IsUrl(s) || !Uri.TryCreate(s.Trim(), UriKind.Absolute, out var u)) return false;
        var h = u.Host.ToLowerInvariant();
        return Sites.Any(x => h == x || h.EndsWith("." + x));
    }

    static readonly Regex PlaylistRx = new(@"youtube\.com/playlist\?|/sets/|/album/", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Lien d'une liste entière (playlist YouTube, album SoundCloud ou Bandcamp) : chaque élément devient un téléchargement.
    /// Une vidéo ouverte depuis une playlist (watch?v=…&amp;list=…) reste une seule vidéo.</summary>
    public static bool IsPlaylist(string url) => PlaylistRx.IsMatch(url);

    /// <summary>Les liens des éléments d'une liste, dans l'ordre.</summary>
    public static async Task<List<string>> Expand(string url, CancellationToken ct)
    {
        var urls = new List<string>(); string lastErr = "";
        await Engines.Run(Engines.YtDlp, new[] { "--flat-playlist", "--print", "url", "--encoding", "utf-8", url },
            l => { if (IsUrl(l)) urls.Add(l.Trim()); }, l => { if (l.StartsWith("ERROR:")) lastErr = l; }, ct, Utf8Env);
        if (urls.Count == 0) throw new Exception(lastErr.Length > 0 ? Friendly(lastErr) : "Liste vide ou privée");
        return urls;
    }

    public static List<string> Args(string url, string mode, string outDir, string tempDir, bool subs = false)
    {
        var a = new List<string>
        {
            "--newline", "--progress", "--no-playlist", "--no-mtime", "--encoding", "utf-8",
            // morceaux et fichiers .part dans un dossier temporaire : seul le fichier fini arrive dans outDir
            "--ffmpeg-location", Engines.Dir, "-P", outDir, "-P", "temp:" + tempDir, "-o", "%(title).150B.%(ext)s",
            "--print", "before_dl:T|%(title)s", "--print", "after_move:F|%(filepath)s", "--no-simulate",
            "--embed-metadata",
        };
        if (mode == "mp3")
            a.AddRange(new[] { "-f", "ba/b", "-x", "--audio-format", "mp3", "--audio-quality", "0", "--embed-thumbnail" });
        else
        {   // MP4 lisible partout : on préfère le H.264 à résolution égale
            var res = mode is "1080" or "720" ? $"res:{mode}," : "res,";
            a.AddRange(new[] { "-f", "bv*+ba/b", "-S", res + "fps,vcodec:h264,acodec:m4a", "--merge-output-format", "mp4" });
            // sous-titres écrits par l'auteur (pas les automatiques), dans la langue du PC et en anglais, rangés dans le MP4
            if (subs) a.AddRange(new[] { "--embed-subs", "--sub-langs", $"{CultureInfo.CurrentUICulture.TwoLetterISOLanguageName}.*,en.*" });
        }
        a.Add(url);
        return a;
    }

    public static async Task Run(Job job, Settings s)
    {
        var outDir = s.DownloadDir;
        Directory.CreateDirectory(outDir);
        await UpdateEngine(s); // au plus une fois par jour, même si Prisme reste ouvert des semaines
        var temp = Path.Combine(Path.GetTempPath(), "prisme-dl-" + Guid.NewGuid().ToString("N"));
        try
        {
            // yt-dlp travaille dans un dossier vide : sinon il réutiliserait (puis supprimerait, après -x)
            // un fichier du même nom déjà présent dans outDir
            try { await RunIn(job, Path.Combine(temp, "out"), temp, s.DownloadSubs); }
            catch (UnknownError) when (!job.Cts.IsCancellationRequested)
            {   // erreur inconnue = souvent un site qui a changé : si une version plus récente de yt-dlp s'installe, un seul nouvel essai
                if (!await UpdateEngine(s, force: true)) throw;
                job.Output = null; job.Status = "Nouvel essai…";
                await RunIn(job, Path.Combine(temp, "out"), temp, s.DownloadSubs);
            }
            var dest = Formats.UniquePath(outDir, Path.GetFileNameWithoutExtension(job.Output!), Path.GetExtension(job.Output!).TrimStart('.'));
            File.Move(job.Output!, dest, true);
            job.Output = dest;
        }
        finally { try { Directory.Delete(temp, true); } catch { } } // annulé ou échoué : pas de .part qui traînent
    }

    static async Task RunIn(Job job, string outDir, string temp, bool subs)
    {
        var vimeo = Regex.Match(job.Url, @"^https?://(?:www\.)?vimeo\.com/(\d+)(?:/(\w+))?", RegexOptions.IgnoreCase);
        try { await YtDlp(job, Args(job.Url, job.DownloadMode, outDir, temp, subs)); }
        catch when (!job.Cts.IsCancellationRequested && vimeo.Success)
        {
            // vimeo.com exige souvent un compte ; le lecteur intégrable public, non
            var h = vimeo.Groups[2].Success ? "?h=" + vimeo.Groups[2].Value : "";
            job.Output = null;
            await YtDlp(job, Args($"https://player.vimeo.com/video/{vimeo.Groups[1].Value}{h}", job.DownloadMode, outDir, temp, subs));
        }
        catch (Exception first) when (!job.Cts.IsCancellationRequested && IsX(job.Url))
        {
            // X cache souvent les vidéos aux visiteurs non connectés : on passe par l'API publique fxtwitter
            var x = await XVideo(job.Url, job.DownloadMode, job.Cts.Token) ?? throw first;
            job.Title = x.Name; job.Output = null;
            var a = Args(x.Url, job.DownloadMode, outDir, temp);
            a[a.IndexOf("%(title).150B.%(ext)s")] = x.Name + ".%(ext)s";
            a[a.IndexOf("before_dl:T|%(title)s")] = "before_dl:T|" + x.Name;
            await YtDlp(job, a);
        }
    }

    static bool IsX(string url) => Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u)
        && u.Host.ToLowerInvariant() is var h && (h is "x.com" or "twitter.com" or "fixupx.com" || h.EndsWith(".x.com") || h.EndsWith(".twitter.com"));

    static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20), DefaultRequestHeaders = { { "User-Agent", "Prisme/1.0" } } };

    /// <summary>Lien MP4 direct d'une vidéo de tweet (null si le tweet n'en a pas).</summary>
    internal static async Task<(string Url, string Name)?> XVideo(string url, string mode, CancellationToken ct)
    {
        var m = Regex.Match(url, @"/status(?:es)?/(\d+)(?:/video/(\d+))?");
        if (!m.Success) return null;
        using var doc = System.Text.Json.JsonDocument.Parse(await Http.GetStringAsync($"https://api.fxtwitter.com/status/{m.Groups[1].Value}", ct));
        var tweet = doc.RootElement.GetProperty("tweet");
        if (!tweet.TryGetProperty("media", out var media) || !media.TryGetProperty("videos", out var videos) || videos.GetArrayLength() == 0) return null;
        int idx = m.Groups[2].Success ? Math.Clamp(int.Parse(m.Groups[2].Value) - 1, 0, videos.GetArrayLength() - 1) : 0;
        var v = videos[idx];
        int max = mode is "1080" ? 1080 : mode is "720" ? 720 : int.MaxValue;
        // variantes MP4 classées par débit ; à défaut, l'URL principale de la vidéo
        var mp4 = v.TryGetProperty("formats", out var f)
            ? f.EnumerateArray().Where(x => x.GetProperty("container").GetString() == "mp4" && x.TryGetProperty("bitrate", out _))
               .Select(x => (Url: x.GetProperty("url").GetString()!, Br: x.GetProperty("bitrate").GetInt64(),
                             H: int.TryParse(Regex.Match(x.GetProperty("url").GetString()!, @"/\d+x(\d+)/").Groups[1].Value, out var h) ? h : 0))
               .OrderByDescending(x => x.H <= max).ThenByDescending(x => x.Br).Select(x => x.Url).FirstOrDefault()
            : null;
        mp4 ??= v.GetProperty("url").GetString();
        if (mp4 == null) return null;
        var author = tweet.GetProperty("author").GetProperty("screen_name").GetString();
        var text = Regex.Replace(tweet.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "", @"https?://\S+|\s+", " ").Trim();
        var name = string.Join("_", $"{author} - {(text.Length > 0 ? text[..Math.Min(80, text.Length)] : m.Groups[1].Value)}".Split(Path.GetInvalidFileNameChars())).Trim(' ', '.');
        return (mp4, name.Replace("%", "%%"));
    }

    static readonly Dictionary<string, string> Utf8Env = new() { ["PYTHONIOENCODING"] = "utf-8", ["PYTHONUTF8"] = "1" };

    /// <summary>Échec que Prisme ne sait pas expliquer : yt-dlp est peut-être périmé pour ce site.</summary>
    sealed class UnknownError(string message) : Exception(message);

    static async Task YtDlp(Job job, List<string> args)
    {
        string lastErr = "";
        var env = Utf8Env;
        var code = await Engines.Run(Engines.YtDlp, args, line =>
        {
            if (line.StartsWith("T|")) { job.Title = line[2..]; job.Status = "Téléchargement…"; return; }
            if (line.StartsWith("F|")) { job.Output = line[2..]; return; }
            var m = Pct.Match(line);
            if (m.Success) { job.Indeterminate = false; job.Progress = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture); return; }
            if (line.StartsWith("[Merger]") || line.StartsWith("[VideoConvertor]")) { job.Status = "Assemblage…"; job.Indeterminate = true; }
            else if (line.StartsWith("[ExtractAudio]")) { job.Status = "Conversion en MP3…"; job.Indeterminate = true; }
            else if (line.StartsWith("ERROR:")) lastErr = line;
        }, line => { if (line.StartsWith("ERROR:")) lastErr = line; }, job.Cts.Token, env);

        if (code != 0 || job.Output == null || !File.Exists(job.Output))
        {
            var msg = Friendly(lastErr);
            throw lastErr.Length == 0 || msg == lastErr.Replace("ERROR: ", "") ? new UnknownError(msg) : new Exception(msg);
        }
    }

    static string Friendly(string err)
    {
        var e = err.Replace("ERROR: ", "");
        if (e.Contains("getaddrinfo") || e.Contains("timed out") || e.Contains("Failed to resolve") || e.Contains("No route to host") || e.Contains("Connection refused"))
            return "Pas de connexion Internet ?";
        if (e.Contains("No video could be found")) return "Aucune vidéo dans ce post";
        if (e.Contains("Unsupported URL")) return "Ce site n'est pas pris en charge";
        if (e.Contains("Private video")) return "Vidéo privée";
        if (e.Contains("Sign in to confirm your age") || e.Contains("age-restricted") || e.Contains("age restricted"))
            return "Vidéo réservée aux adultes : connexion au compte nécessaire";
        if (e.Contains("Sign in") || e.Contains("login required") || e.Contains("log in")) return "Cette vidéo demande d'être connecté";
        if (e.Contains("not available") || e.Contains("unavailable")) return "Vidéo indisponible";
        if (e.Contains("DRM")) return "Vidéo protégée (DRM) : téléchargement impossible";
        return string.IsNullOrWhiteSpace(e) ? "Le téléchargement a échoué" : e;
    }

    static readonly SemaphoreSlim UpdateLock = new(1); // une seule mise à jour à la fois ; les téléchargements attendent qu'elle finisse

    /// <summary>Met yt-dlp à jour (les sites changent souvent). Silencieux, au plus une fois par jour sauf <paramref name="force"/>.
    /// Renvoie vrai si une version plus récente vient d'être installée.</summary>
    public static async Task<bool> UpdateEngine(Settings s, bool force = false)
    {
        await UpdateLock.WaitAsync();
        try
        {
            if (!force && (DateTime.Now - s.LastEngineUpdate).TotalHours < 24) return false;
            bool upToDate = false;
            // hors ligne, yt-dlp sort avec un code d'erreur : rien n'est noté, on réessaiera au prochain téléchargement
            if (await Engines.Run(Engines.YtDlp, new[] { "-U" }, l => upToDate |= l.Contains("is up to date"), null, CancellationToken.None) != 0) return false;
            s.LastEngineUpdate = DateTime.Now; s.Save();
            return !upToDate;
        }
        catch { return false; }
        finally { UpdateLock.Release(); }
    }
}
