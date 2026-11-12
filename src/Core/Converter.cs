using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace Prisme.Core;

/// <summary>Toutes les conversions de fichiers. Chaque méthode remplit job.Output ou lève une exception lisible.</summary>
public static class Converter
{
    static readonly SemaphoreSlim OfficeLock = new(1); // un seul moteur de documents à la fois

    public static Task Run(Job job, Settings s)
    {
        var first = job.Inputs[0];
        var kind = Formats.KindOf(first);
        var outDir = s.OutputFor(first);
        var name = Path.GetFileNameWithoutExtension(first);
        var ct = job.Cts.Token;
        if (job.Inputs.Count == 1 && !job.EditOf(first).IsEmpty) name += job.EditOf(first).Trims ? " (extrait)" : " (retouche)";

        var id = job.Target.Id;
        return (kind, id) switch
        {
            (Kind.Image, "pdf") => Task.Run(() => ImagesToPdf(job, outDir, name, ct), ct),
            (Kind.Pdf, "pdf-merge") => Task.Run(() => MergePdf(job, outDir, name), ct),
            (Kind.Audio, _) when id.StartsWith("merge-") => MergeAudio(job, outDir, name, ct),
            (Kind.Pdf, "pdf-split") => Task.Run(() => SplitPdf(job, outDir, name), ct),
            (Kind.Pdf, "png" or "jpg") => PdfToImages(job, first, outDir, name, ct),
            (Kind.Pdf, _) => Office(job, first, outDir, name, ct, id == "pptx" ? "impress_pdf_import" : "writer_pdf_import"),
            (_, "png-pages" or "jpg-pages") => OfficeToImages(job, first, outDir, name, ct),
            _ when Formats.IsOffice(kind) => Office(job, first, outDir, name, ct),
            _ => Ffmpeg(job, first, Formats.UniquePath(outDir, name, job.Target.Ext), s, ct),
        };
    }

    // ---------- audio / vidéo / image : FFmpeg ----------

    /// <summary>Arguments FFmpeg pour une conversion simple (public pour l'autotest).</summary>
    public static List<string> FfmpegArgs(string input, string targetId, string output, Kind kind, Settings s = null)
    {
        int gifFps = Math.Clamp(s?.GifFps ?? 12, 1, 50);
        var (gifBox, gifPal, gifDither) = (s?.GifQuality) switch
        {   // taille maxi (une vidéo verticale aussi), couleurs, finesse du tramage
            "light" => (360, "max_colors=128:", 5),
            "high" => (720, "", 3),
            _ => (540, "", 5),
        };
        var a = new List<string> { "-hide_banner", "-nostdin", "-y", "-i", input };
        const string white = "[0:v]split[a][b];[a]format=rgb24,drawbox=c=white:t=fill[bg];[bg][b]overlay=format=auto,format=yuvj444p";
        string[] x = (kind, targetId) switch
        {
            (Kind.Audio, "mp4") => new[] { "-f", "lavfi", "-i", "color=c=black:s=1280x720:r=2", "-map", "1:v", "-map", "0:a",
                "-c:v", "libx264", "-tune", "stillimage", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "256k", "-shortest", "-movflags", "+faststart" },
            (Kind.Subtitle, _) => Array.Empty<string>(), // FFmpeg déduit le format de sous-titres de l'extension
            (Kind.Image, "mp4") => new[] { "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2", "-c:v", "libx264", "-crf", "20", "-pix_fmt", "yuv420p", "-movflags", "+faststart", "-an" },
            (Kind.Image, "webm") => new[] { "-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0", "-pix_fmt", "yuv420p", "-an" },
            (_, "mp3") => new[] { "-vn", "-map_metadata", "0", "-c:a", "libmp3lame", "-q:a", "0" },
            (_, "alac") => new[] { "-vn", "-map_metadata", "0", "-af", "aformat=channel_layouts=7.1|5.1|stereo|mono", "-c:a", "alac" },
            (_, "aiff") => new[] { "-vn", "-c:a", "pcm_s16be" },
            (_, "wma") => new[] { "-vn", "-ac", "2", "-c:a", "wmav2", "-b:a", "192k" }, // WMA et Opus : stéréo max ici, on réduit le 5.1
            (_, "ac3") => new[] { "-vn", "-c:a", "ac3", "-b:a", "448k" },
            (_, "m4r") => new[] { "-vn", "-t", "40", "-c:a", "aac", "-b:a", "256k", "-f", "ipod" },
            (_, "wav") => new[] { "-vn", "-c:a", "pcm_s16le" },
            (_, "flac") => new[] { "-vn", "-c:a", "flac" },
            (_, "m4a") => new[] { "-vn", "-map_metadata", "0", "-c:a", "aac", "-b:a", "256k" },
            (_, "ogg") => new[] { "-vn", "-c:a", "libvorbis", "-q:a", "6" },
            (_, "opus") => new[] { "-vn", "-ac", "2", "-c:a", "libopus", "-b:a", "160k" },
            (Kind.Video, "mp4") => new[] { "-map", "0:V:0", "-map", "0:a?", "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2", "-c:v", "libx264", "-crf", "20", "-preset", "medium",
                "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", "-movflags", "+faststart" },
            (Kind.Video, "mp4-small") => new[] { "-map", "0:V:0", "-map", "0:a?", "-vf", "scale='min(1920,iw)':-2", "-c:v", "libx264",
                "-crf", "28", "-preset", "slow", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart" },
            (Kind.Video, "mp4-hevc") => new[] { "-map", "0:V:0", "-map", "0:a?", "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2", "-c:v", "libx265", "-crf", "26", "-preset", "medium",
                "-tag:v", "hvc1", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart" },
            // taille visée : débit et définition calculés dans Ffmpeg() d'après la durée, encodage en deux passes
            (Kind.Video, "mp4-20mo" or "mp4-10mo") => new[] { "-map", "0:V:0", "-map", "0:a?", "-c:v", "libx264", "-preset", "medium",
                "-pix_fmt", "yuv420p", "-c:a", "aac", "-movflags", "+faststart" },
            (Kind.Video, "wmv") => new[] { "-sn", "-c:v", "wmv2", "-b:v", "6M", "-ac", "2", "-c:a", "wmav2", "-b:a", "192k" },
            (Kind.Video, "mpg") => new[] { "-sn", "-c:v", "mpeg2video", "-q:v", "3", "-c:a", "mp2", "-b:a", "224k" },
            (Kind.Video, "webp") => new[] { "-vf", "fps=15,scale='min(640,iw)':-1", "-c:v", "libwebp", "-quality", "75", "-loop", "0", "-an" },
            (Kind.Video, "mkv") => new[] { "-map", "0:v", "-map", "0:a?", "-c", "copy" },
            // film dont la TV ne lit pas le son (DTS, TrueHD) : image copiée telle quelle, son refait en Dolby Digital, 5.1 au plus
            // shortcut: toutes les pistes son sont refaites, même celles déjà lisibles ; ne refaire que les DTS/TrueHD si la perte gêne
            (Kind.Video, "mkv-tv") => new[] { "-map", "0:v", "-map", "0:a?", "-c", "copy", "-c:a", "ac3", "-af", "aformat=channel_layouts=5.1|stereo|mono" },
            (Kind.Video, "webm") => new[] { "-map", "0:V:0", "-map", "0:a?", "-c:v", "libvpx-vp9", "-crf", "32", "-b:v", "0",
                "-row-mt", "1", "-deadline", "good", "-cpu-used", "4", "-ac", "2", "-c:a", "libopus", "-b:a", "128k" },
            (Kind.Video, "mov") => new[] { "-map", "0:V:0", "-map", "0:a?", "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2", "-c:v", "libx264", "-crf", "20", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k" },
            // AVI/MPG/WMV n'ont pas de piste « par défaut » : sans -map, FFmpeg garde la vraie vidéo (pas la pochette) et la piste audio par défaut
            (Kind.Video, "avi") => new[] { "-sn", "-c:v", "mpeg4", "-q:v", "3", "-c:a", "libmp3lame", "-q:a", "2" },
            // palette des zones qui bougent + tramage régulier : ~5× plus léger qu'avant (1.2.1) à l'œil égal
            (Kind.Video, "gif") => new[] { "-filter_complex", $"fps={gifFps},scale='min({gifBox},iw)':'min({gifBox},ih)':force_original_aspect_ratio=decrease:flags=lanczos,split[a][b];" +
                $"[a]palettegen={gifPal}stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale={gifDither}:diff_mode=rectangle", "-loop", "0" },
            // WhatsApp PC envoie un .gif comme une photo fixe : une petite vidéo muette passe, elle
            (Kind.Video or Kind.Image, "mp4-anim") => new[] { "-map", "0:V:0", "-an", "-sn", "-vf",
                "scale='min(720,iw)':'min(720,ih)':force_original_aspect_ratio=decrease,scale=trunc(iw/2)*2:trunc(ih/2)*2",
                "-c:v", "libx264", "-crf", "26", "-preset", "slow", "-pix_fmt", "yuv420p", "-movflags", "+faststart" },
            (Kind.Image, "png") => new[] { "-frames:v", "1", "-update", "1" },
            (Kind.Image, "jpg") => new[] { "-filter_complex", white, "-frames:v", "1", "-update", "1", "-q:v", "2" },
            (Kind.Image, "bmp") => new[] { "-filter_complex", white.Replace(",format=yuvj444p", ",format=bgr24"), "-frames:v", "1", "-update", "1" },
            (Kind.Image, "webp") when Formats.ExtOf(input) == "gif" => new[] { "-c:v", "libwebp", "-quality", "80", "-loop", "0", "-an" }, // garde l'animation
            (Kind.Image, "webp") => new[] { "-frames:v", "1", "-c:v", "libwebp", "-quality", "90" },
            (Kind.Image, "avif") => new[] { "-filter_complex", white.Replace(",format=yuvj444p", ",format=yuv420p"), "-frames:v", "1",
                "-c:v", "libaom-av1", "-still-picture", "1", "-crf", "30", "-cpu-used", "6" },
            (Kind.Image, "gif") => new[] { "-filter_complex", "[0:v]split[a][b];[a]palettegen[p];[b][p]paletteuse", "-frames:v", "1", "-update", "1" },
            (Kind.Image, "tiff") => new[] { "-frames:v", "1", "-update", "1" },
            // filter_complex et non -vf : les HEIC (photos d'iPhone) passent déjà par un graphe complexe (mosaïque de tuiles)
            (Kind.Image, "ico") => new[] { "-filter_complex", "[0:v]scale=256:256:force_original_aspect_ratio=decrease", "-frames:v", "1", "-update", "1" },
            _ => throw new NotSupportedException($"Conversion {kind} → {targetId} non prise en charge"),
        };
        a.AddRange(x);
        // sous-titres texte conservés ; s'ils sont en image (DVD/Blu-ray), Ffmpeg() refait sans eux
        if (kind == Kind.Video && SubCodec(targetId) is { } sc) a.AddRange(sc.Length > 0 ? new[] { "-map", "0:s?", "-c:s", sc } : new[] { "-map", "0:s?" });
        a.AddRange(new[] { "-progress", "pipe:1", "-nostats", output });
        return a;
    }

    static string SubCodec(string target) => target switch
    {
        "mp4" or "mp4-small" or "mp4-hevc" or "mov" => "mov_text",
        "webm" => "webvtt",
        "mkv" or "mkv-tv" => "", // copiés tels quels avec le reste
        _ => null,
    };

    static readonly Regex DurationRx = new(@"Duration: (\d+):(\d\d):(\d\d(?:\.\d+)?)", RegexOptions.Compiled);

    // annulé ou échoué n'importe où (même pendant la lecture de la durée) : pas de fichier vide laissé dans le dossier
    static async Task Ffmpeg(Job job, string input, string output, Settings settings, CancellationToken ct)
    {
        try { await FfmpegCore(job, input, output, settings, ct); }
        catch { TryDelete(output); throw; }
    }

    static async Task FfmpegCore(Job job, string input, string output, Settings settings, CancellationToken ct)
    {
        double total = 0; string lastErr = "";
        var k = Formats.KindOf(input);
        var edit = job.EditOf(input);
        job.Indeterminate = k == Kind.Subtitle || (k == Kind.Image && job.Target.Id is not ("mp4" or "webm" or "mp4-anim"));
        var temps = new List<string>();
        try
        {
            if (k == Kind.Image) input = await PrepareImage(input, edit, job.Target.Id != "png", temps, ct);
        }
        catch { TryDelete(output); temps.ForEach(TryDelete); throw; }
        var args = FfmpegArgs(input, job.Target.Id, output, k, settings);
        double len = 0, sp = 1; // sp : vitesse choisie, la sortie dure total / sp
        bool av = k is Kind.Audio or Kind.Video;
        void DropSubs()
        {
            int s0 = args.IndexOf("0:s?"); if (s0 < 0) return;
            args.RemoveRange(s0 - 1, s0 + 1 < args.Count && args[s0 + 1] == "-c:s" ? 4 : 2);
            args.InsertRange(args.IndexOf("-progress"), new[] { "-sn" });
        }
        if (av && !edit.IsEmpty)
        {
            int c = args.IndexOf("copy");
            if (c > 0 && args[c - 1] == "-c") // MKV en copie : une copie ne se coupe qu'aux images clés, ignore la coupe du son et n'accepte aucun filtre
            {
                args.RemoveRange(c - 1, 2);
                int v = args.IndexOf("0:v"); if (v > 0) args[v] = "0:V"; // réencodée, une pochette deviendrait une 2e vidéo cassée
                args.InsertRange(c - 1, new[] { "-c:v", "libx264", "-crf", "18", "-preset", "medium", "-c:a", "aac", "-b:a", "384k", "-c:s", "copy" });
            }
        }
        if (av && edit.Filters)
        {
            var (vf, af) = edit.MediaFilters();
            if (vf.Length > 0 && k == Kind.Video && !args.Contains("-vn")) AddFilter(args, "-vf", vf);
            if (af.Length > 0 && !args.Contains("-an") && job.Target.Ext is not ("gif" or "webp")) AddFilter(args, "-af", af);
            sp = edit.Speed;
            if (sp != 1) DropSubs(); // shortcut: sous-titres retirés d'une vidéo accélérée ou ralentie, ils ne suivraient plus ; les recaler si on en a besoin
        }
        if (edit.Trims && av)
        {
            var full = await Duration(input, ct);
            var end = edit.End > 0 && (full <= 0 || edit.End < full) ? edit.End : full;
            len = end - edit.Start;
            if (len <= 0) { TryDelete(output); throw new Exception("L'extrait choisi est vide : le début est après la fin du fichier"); }
            if (job.Target.Id == "m4r") len = Math.Min(len, 40 * sp);
            args.InsertRange(args.IndexOf("-i"), new[] { "-ss", Inv(edit.Start) }); // avant -i : saut direct, précis quand on réencode
            args.InsertRange(args.IndexOf("-progress"), new[] { "-t", Inv(len / sp) }); // après celui de la sonnerie (40 s) : c'est le dernier qui compte
            if (args.Contains("0:s?") && job.Target.Ext == "mkv") args.InsertRange(args.IndexOf("-progress"), new[] { "-avoid_negative_ts", "disabled" }); // sinon tout glisse d'une demi-seconde
            else DropSubs(); // shortcut: sous-titres retirés d'un extrait MP4/MOV/WebM (FFmpeg les décale) ; les extraire et les recaler à part si on en a besoin
            total = len;
        }
        if (k == Kind.Audio && job.Target.Id == "mp4" && (len > 0 ? len : await Duration(input, ct)) is > 0 and var d)
            args.InsertRange(args.IndexOf("-movflags"), new[] { "-t", Inv(d / sp) }); // -shortest seul laisse ~35 s d'image en trop
        double pBase = 0, pPart = 1; // deux passes : la première remplit la barre jusqu'à 50 %
        void OnOut(string line)
        {
            if (line.StartsWith("out_time_us=") && total > 0 && long.TryParse(line[12..], out var us))
                job.Progress = pBase + pPart * us / 1e6 / (total / sp) * 100;
        }
        void OnErr(string line)
        {
            var m = DurationRx.Match(line);
            if (m.Success && total == 0)
                total = int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60
                        + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
            if (line.Trim().Length > 0) lastErr = line.Trim();
        }
        int code;
        try
        {
            if (job.Target.Id is "mp4-20mo" or "mp4-10mo")
            {
                int mo = job.Target.Id == "mp4-20mo" ? 20 : 10;
                if (total <= 0) total = await Duration(input, ct);
                if (total <= 0) throw new Exception("Durée de la vidéo inconnue : impossible de viser une taille");
                double budget = mo * 8000 * 0.92 / (total / sp); // kbit/s, marge pour l'enveloppe MP4 (et les « Mo » qui sont des Mio)
                int ak = budget > 1000 ? 128 : 64;
                int vk = (int)Math.Min(8000, budget - ak); // une vidéo courte n'a pas besoin de remplir toute la place
                if (vk < 150) throw new Exception($"Vidéo trop longue pour tenir dans {mo} Mo : garde un extrait (onglet Retoucher) ou choisis « MP4 léger »");
                int side = vk >= 2500 ? 1080 : vk >= 1200 ? 720 : vk >= 600 ? 480 : 360;
                // le petit côté est plafonné : une vidéo verticale garde sa netteté ; jamais agrandie, dimensions paires
                AddFilter(args, "-vf", $"scale='if(gte(iw,ih),-2,trunc(min({side},iw)/2)*2)':'if(gte(iw,ih),trunc(min({side},ih)/2)*2,-2)'", first: false);
                args.InsertRange(args.IndexOf("-movflags"), new[] { "-b:v", $"{vk}k", "-maxrate", $"{vk * 3 / 2}k", "-bufsize", $"{vk * 2}k", "-b:a", $"{ak}k" });
                var passLog = Path.Combine(Path.GetTempPath(), $"prisme-pass-{Guid.NewGuid():N}");
                temps.AddRange(new[] { passLog + "-0.log", passLog + "-0.log.mbtree" });
                var pass1 = args.Take(args.Count - 1).Concat(new[] { "-pass", "1", "-passlogfile", passLog, "-an", "-f", "null", "NUL" }).ToList();
                pPart = 0.5;
                if (await Engines.Run(Engines.Ffmpeg, pass1, OnOut, OnErr, ct) != 0) throw new Exception(Friendly(lastErr));
                pBase = 50;
                args.InsertRange(args.IndexOf("-progress"), new[] { "-pass", "2", "-passlogfile", passLog });
            }
            List<string> cpu = null; // réglage « carte graphique » : même commande avec son encodeur, et retour au processeur si elle échoue
            if (settings?.Gpu == true && k is Kind.Video or Kind.Image && await GpuEncoder() is { } enc)
            {
                cpu = args.ToList();
                if (!UseGpu(args, enc)) cpu = null;
            }
            code = await Engines.Run(Engines.Ffmpeg, args, OnOut, OnErr, ct);
            if (code != 0 && cpu != null)
            {
                args = cpu; job.Progress = 0;
                code = await Engines.Run(Engines.Ffmpeg, args, OnOut, OnErr, ct);
            }
        }
        catch { TryDelete(output); throw; }
        finally { temps.ForEach(TryDelete); }
        int s = args.IndexOf("0:s?");
        if (code != 0 && s > 0 && !ct.IsCancellationRequested)
        {   // sous-titres incompatibles avec la cible : on refait sans
            args.RemoveRange(s - 1, args[s + 1] == "-c:s" ? 4 : 2);
            args.InsertRange(args.IndexOf("-progress"), new[] { "-sn" });
            job.Progress = 0;
            try { code = await Engines.Run(Engines.Ffmpeg, args, OnOut, OnErr, ct); }
            catch { TryDelete(output); throw; }
        }
        if (code != 0 || !File.Exists(output) || new FileInfo(output).Length == 0)
        {
            TryDelete(output);
            throw new Exception(Friendly(lastErr));
        }
        job.Output = output;
    }

    static string Inv(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>Ajoute un filtre à la chaîne -vf / -af existante (ou en tête du graphe du GIF, qui commence sans étiquette), sinon crée l'option.</summary>
    static void AddFilter(List<string> a, string opt, string f, bool first = true)
    {
        int i = a.IndexOf(opt);
        if (i < 0 && opt == "-vf" && (i = a.IndexOf("-filter_complex")) >= 0) { a[i + 1] = f + "," + a[i + 1]; return; }
        if (i >= 0) a[i + 1] = first ? f + "," + a[i + 1] : a[i + 1] + "," + f;
        else a.InsertRange(a.IndexOf("-progress"), new[] { opt, f });
    }

    static Task<string> _gpuEnc;
    /// <summary>Encodeur de la carte graphique qui marche vraiment sur ce PC (essai d'une image), ou null. Cherché une seule fois.</summary>
    static Task<string> GpuEncoder() => _gpuEnc ??= Task.Run(async () =>
    {
        foreach (var e in new[] { "nvenc", "qsv", "amf" }) // NVIDIA, Intel, AMD
            try
            {
                if (await Engines.Run(Engines.Ffmpeg, new[] { "-hide_banner", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=25:duration=0.2",
                        "-c:v", "h264_" + e, "-f", "null", "NUL" }, null, null, CancellationToken.None) == 0) return e;
            }
            catch { }
        return null;
    });

    /// <summary>Remplace libx264 / libx265 et leur réglage de qualité par l'encodeur de la carte graphique. Faux s'il n'y a rien à remplacer
    /// (taille visée en deux passes, formats sans H.264) : la commande reste telle quelle.</summary>
    internal static bool UseGpu(List<string> a, string enc)
    {
        bool hevc = a.Contains("libx265");
        int c = a.IndexOf(hevc ? "libx265" : "libx264"), q = a.IndexOf("-crf");
        if (c < 0 || q < 0) return false;
        a[c] = (hevc ? "hevc_" : "h264_") + enc;
        var crf = a[q + 1];
        a.RemoveRange(q, 2);
        a.InsertRange(q, enc switch
        {
            "qsv" => new[] { "-global_quality", crf },
            "nvenc" => new[] { "-rc", "vbr", "-cq", crf, "-b:v", "0" },
            _ => new[] { "-rc", "cqp", "-qp_i", crf, "-qp_p", crf, "-qp_b", crf },
        });
        int p = a.IndexOf("-preset"); if (p > 0) a.RemoveRange(p, 2); // les préréglages de libx264 n'existent pas ici
        return true;
    }

    /// <summary>Image prête pour la conversion : photo HEIC recollée en PNG (si <paramref name="decodeTiles"/> ou retouche),
    /// puis recadrage et taille appliqués. Les fichiers temporaires sont ajoutés à <paramref name="temps"/>.</summary>
    // shortcut: une retouche ne garde que la première image d'un GIF animé ; passer le filtre à la conversion si on veut l'animation
    internal static async Task<string> PrepareImage(string input, Edit edit, bool decodeTiles, List<string> temps, CancellationToken ct)
    {
        async Task<string> Step(string src, params string[] filter)
        {
            var tmp = Path.Combine(Path.GetTempPath(), $"prisme-{Guid.NewGuid():N}.png");
            temps.Add(tmp);
            var a = new List<string> { "-hide_banner", "-nostdin", "-y", "-i", src };
            a.AddRange(filter);
            a.AddRange(new[] { "-frames:v", "1", "-update", "1", tmp });
            await Engines.Run(Engines.Ffmpeg, a, null, null, ct);
            if (!File.Exists(tmp) || new FileInfo(tmp).Length == 0) throw new Exception("Fichier illisible ou endommagé");
            return tmp;
        }
        // photos d'iPhone en mosaïque de tuiles : seul un décodage simple (vers PNG) recolle la photo entière, et aucun filtre n'y passe
        if (Formats.ExtOf(input) is "heic" or "heif" or "avif" && (decodeTiles || !edit.IsEmpty)) input = await Step(input);
        var vf = edit.ImageFilter();
        if (vf.Length > 0) input = await Step(input, "-vf", vf);
        return input;
    }

    /// <summary>Durée en secondes lue par FFmpeg (0 si inconnue).</summary>
    internal static async Task<double> Duration(string input, CancellationToken ct)
    {
        double d = 0;
        await Engines.Run(Engines.Ffmpeg, new[] { "-hide_banner", "-nostdin", "-i", input }, null, line =>
        {
            var m = DurationRx.Match(line);
            if (m.Success && d == 0) d = int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        }, ct);
        return d;
    }

    static string Friendly(string err) =>
        err.Contains("Invalid data found") ? "Fichier illisible ou endommagé" :
        err.Contains("does not contain any stream") || err.Contains("matches no streams") ? "Ce fichier ne contient pas de piste exploitable" :
        string.IsNullOrEmpty(err) ? "La conversion a échoué" : err;

    static void TryDelete(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }

    // ---------- images → un seul PDF ----------

    static async Task ImagesToPdf(Job job, string outDir, string name, CancellationToken ct)
    {
        var output = Formats.UniquePath(outDir, name, "pdf");
        var temps = new List<string>();
        try
        {
            using var doc = new PdfDocument();
            doc.Info.Creator = "Prisme";
            for (int i = 0; i < job.Inputs.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var src = await PrepareImage(job.Inputs[i], job.EditOf(job.Inputs[i]), false, temps, ct);
                {   // tout passe par FFmpeg : il redresse les photos selon leur orientation EXIF (PDFsharp l'ignore)
                    bool photo = Formats.ExtOf(src) is "jpg" or "jpeg" or "jfif";
                    var tmp = Path.Combine(Path.GetTempPath(), $"prisme-{Guid.NewGuid():N}.{(photo ? "jpg" : "png")}");
                    temps.Add(tmp);
                    var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-i", src, "-frames:v", "1", "-update", "1" };
                    if (photo) args.AddRange(new[] { "-q:v", "2" });
                    args.Add(tmp);
                    var code = await Engines.Run(Engines.Ffmpeg, args, null, null, ct);
                    if (code != 0 || !File.Exists(tmp)) throw new Exception($"Image illisible : {Path.GetFileName(src)}");
                    src = tmp;
                }
                using var img = XImage.FromFile(src);
                var page = doc.AddPage();
                page.Size = PdfSharp.PageSize.A4;
                bool landscape = img.PixelWidth > img.PixelHeight;
                page.Orientation = landscape ? PdfSharp.PageOrientation.Landscape : PdfSharp.PageOrientation.Portrait;
                double pw = page.Width.Point, ph = page.Height.Point, m = 24;
                double scale = Math.Min((pw - 2 * m) / img.PixelWidth, (ph - 2 * m) / img.PixelHeight);
                double w = img.PixelWidth * scale, h = img.PixelHeight * scale;
                using (var g = XGraphics.FromPdfPage(page)) g.DrawImage(img, (pw - w) / 2, (ph - h) / 2, w, h);
                job.Progress = (i + 1) * 100.0 / job.Inputs.Count;
            }
            doc.Save(output);
            job.Output = output;
        }
        catch { TryDelete(output); throw; }
        finally { temps.ForEach(TryDelete); }
    }

    // ---------- sons : assemblage à la suite ----------

    static async Task MergeAudio(Job job, string outDir, string name, CancellationToken ct)
    {
        var output = Formats.UniquePath(outDir, $"{name} (assemblage)", job.Target.Ext);
        string lastErr = "";
        try
        {
            double total = 0;
            foreach (var f in job.Inputs) total += await Duration(f, ct);
            var a = new List<string> { "-hide_banner", "-nostdin", "-y" };
            foreach (var f in job.Inputs) a.AddRange(new[] { "-i", f });
            // chaque morceau ramené au même format (fréquence, stéréo) : sinon concat refuse des sons différents
            var graph = string.Concat(job.Inputs.Select((_, i) => $"[{i}:a:0]aresample=44100,aformat=sample_fmts=fltp:channel_layouts=stereo[a{i}];"))
                        + string.Concat(job.Inputs.Select((_, i) => $"[a{i}]")) + $"concat=n={job.Inputs.Count}:v=0:a=1[out]";
            a.AddRange(new[] { "-filter_complex", graph, "-map", "[out]", "-map_metadata", "-1" });
            a.AddRange(job.Target.Ext switch
            {
                "mp3" => new[] { "-c:a", "libmp3lame", "-q:a", "0" },
                "m4a" => new[] { "-c:a", "aac", "-b:a", "256k", "-movflags", "+faststart" },
                "flac" => new[] { "-c:a", "flac", "-sample_fmt", "s16" },
                _ => new[] { "-c:a", "pcm_s16le" },
            });
            a.AddRange(new[] { "-progress", "pipe:1", "-nostats", output });
            var code = await Engines.Run(Engines.Ffmpeg, a, line =>
            {
                if (line.StartsWith("out_time_us=") && total > 0 && long.TryParse(line[12..], out var us)) job.Progress = us / 1e6 / total * 100;
            }, line => { if (line.Trim().Length > 0) lastErr = line.Trim(); }, ct);
            if (code != 0 || new FileInfo(output).Length == 0) throw new Exception(Friendly(lastErr));
            job.Output = output;
        }
        catch { TryDelete(output); throw; }
    }

    // ---------- PDF : fusion ----------

    static void MergePdf(Job job, string outDir, string name)
    {
        var output = Formats.UniquePath(outDir, job.Inputs.Count > 1 ? $"{name} (fusion)" : $"{name} (copie)", "pdf");
        try
        {
            using var outDoc = new PdfDocument();
            for (int i = 0; i < job.Inputs.Count; i++)
            {
                job.Cts.Token.ThrowIfCancellationRequested();
                PdfDocument src;
                try { src = PdfReader.Open(job.Inputs[i], PdfDocumentOpenMode.Import); }
                catch (Exception e) { throw new Exception($"PDF illisible ou protégé : {Path.GetFileName(job.Inputs[i])} ({e.Message})"); }
                using (src) foreach (var p in src.Pages) outDoc.AddPage(p);
                job.Progress = (i + 1) * 100.0 / job.Inputs.Count;
            }
            outDoc.Save(output);
            job.Output = output;
        }
        catch { TryDelete(output); throw; }
    }

    // ---------- PDF : séparer les pages ----------

    static void SplitPdf(Job job, string outDir, string name)
    {
        PdfDocument src;
        try { src = PdfReader.Open(job.Inputs[0], PdfDocumentOpenMode.Import); }
        catch (Exception e) { throw new Exception($"PDF illisible ou protégé ({e.Message})"); }
        using (src)
        {
            var folder = Formats.UniqueDir(outDir, $"{name} - pages");
            int n = src.PageCount, w = n.ToString().Length;
            try
            {
                for (int i = 0; i < n; i++)
                {
                    job.Cts.Token.ThrowIfCancellationRequested();
                    using var one = new PdfDocument();
                    one.AddPage(src.Pages[i]);
                    one.Save(Path.Combine(folder, $"{name} - page {(i + 1).ToString().PadLeft(w, '0')}.pdf"));
                    job.Progress = (i + 1) * 100.0 / n;
                }
            }
            catch { try { Directory.Delete(folder, true); } catch { } throw; }
            job.Output = folder;
        }
    }

    // ---------- PDF → images (moteur PDF intégré à Windows) ----------

    static async Task PdfToImages(Job job, string input, string outDir, string name, CancellationToken ct)
    {
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(Path.GetFullPath(input));
        Windows.Data.Pdf.PdfDocument pdf;
        try { pdf = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file); }
        catch { throw new Exception("PDF illisible ou protégé par mot de passe"); }

        var ext = job.Target.Ext;
        uint n = pdf.PageCount;
        string folder = null, path = null;
        if (n > 1) folder = Formats.UniqueDir(outDir, $"{name} - pages");
        try
        {
            for (uint i = 0; i < n; i++)
            {
                ct.ThrowIfCancellationRequested();
                using var page = pdf.GetPage(i);
                var opts = new Windows.Data.Pdf.PdfPageRenderOptions
                {   // ~240 dpi, plafonné pour les très grands formats
                    DestinationWidth = (uint)Math.Min(page.Size.Width * 2.5, 5000),
                    BitmapEncoderId = ext == "jpg" ? Windows.Graphics.Imaging.BitmapEncoder.JpegEncoderId : Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId,
                };
                path = n > 1 ? Path.Combine(folder, $"page {(i + 1).ToString().PadLeft(n.ToString().Length, '0')}.{ext}") : Formats.UniquePath(outDir, name, ext);
                await using (var fs = File.Create(path))
                    await page.RenderToStreamAsync(fs.AsRandomAccessStream(), opts);
                job.Progress = (i + 1) * 100.0 / n;
                job.Output = folder ?? path;
            }
        }
        catch { if (folder != null) try { Directory.Delete(folder, true); } catch { } else TryDelete(path); throw; }
    }

    // ---------- documents bureautiques (Office s'il est là pour le PDF, sinon LibreOffice, téléchargé la première fois) ----------

    /// <summary>Filtre d'export LibreOffice pour chaque format cible.</summary>
    static string LoFilter(string id, Kind src) => id switch
    {
        "docx" => "docx:MS Word 2007 XML",
        "txt" => "txt:Text (encoded):UTF8",
        "xlsx" => "xlsx:Calc MS Excel 2007 XML",
        "csv" => "csv:Text - txt - csv (StarCalc):44,34,76,1",
        "pptx" => "pptx:Impress MS PowerPoint 2007 XML",
        "html" when src == Kind.Text => "html:XHTML Writer File:UTF8",
        _ => id,
    };

    static async Task Office(Job job, string input, string outDir, string name, CancellationToken ct, string infilter = null)
    {
        input = Path.GetFullPath(input);
        var output = Formats.UniquePath(outDir, name, job.Target.Ext);
        job.Indeterminate = true;
        bool locked = false;
        try
        {
            await OfficeLock.WaitAsync(ct); locked = true; // annulé dans la file : la sortie réservée est supprimée aussi
            job.Status = "Conversion…";
            // PDF : rendu d'Office ; PDF → Word : Word remet le texte en paragraphes, LibreOffice le laisse dans des cadres figés
            bool msOffice = job.Target.Id == "pdf" || (Formats.KindOf(input) == Kind.Pdf && job.Target.Id == "docx");
            bool viaOffice = msOffice && await Task.Run(() => TryMsOffice(input, output));
            ct.ThrowIfCancellationRequested(); // Word ne s'interrompt pas : l'annulation est appliquée à son retour
            if (viaOffice && new FileInfo(output).Length > 0) { job.Output = output; return; }
            if (Formats.KindOf(input) == Kind.Pdf && job.Target.Id == "txt")
            {   // l'import PDF place le texte dans des cadres, ignorés par l'export texte : on passe par Word
                var docx = Path.Combine(Path.GetTempPath(), $"prisme-{Guid.NewGuid():N}.docx");
                try
                {
                    await LibreOffice(job, input, docx, LoFilter("docx", Kind.Pdf), infilter, ct);
                    // l'export texte de LibreOffice ignore aussi les cadres du docx : on lit le texte directement dans le XML
                    var text = DocxText(docx);
                    // shortcut: un PDF mi-texte mi-scan ne passe pas par l'OCR, à revoir si ça arrive vraiment
                    if (!text.Any(char.IsLetterOrDigit)) text = await OcrPdf(job, input, ct);
                    if (!text.Any(char.IsLetterOrDigit)) throw new Exception("Aucun texte trouvé dans ce PDF, même en lisant les images");
                    File.WriteAllText(output, text, new System.Text.UTF8Encoding(true));
                }
                finally { TryDelete(docx); }
            }
            else output = await LibreOffice(job, input, output, LoFilter(job.Target.Id, Formats.KindOf(input)), infilter, ct);
            job.Output = output;
        }
        catch { TryDelete(output); throw; }
        finally { if (locked) OfficeLock.Release(); }
    }

    /// <summary>PDF scanné → texte, avec la reconnaissance de caractères intégrée à Windows (rien à installer).</summary>
    static async Task<string> OcrPdf(Job job, string input, CancellationToken ct)
    {
        var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new Exception("La reconnaissance de texte de Windows n'est pas disponible pour la langue de ce PC");
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(input);
        var pdf = await Windows.Data.Pdf.PdfDocument.LoadFromFileAsync(file);
        var sb = new System.Text.StringBuilder();
        for (uint i = 0; i < pdf.PageCount; i++)
        {
            ct.ThrowIfCancellationRequested();
            job.Status = $"Lecture du scan… page {i + 1}/{pdf.PageCount}";
            using var page = pdf.GetPage(i);
            // 300 dpi (Size est en 1/96 de pouce), sans dépasser la taille maximale acceptée par l'OCR
            var scale = Math.Min(300 / 96.0, Windows.Media.Ocr.OcrEngine.MaxImageDimension / Math.Max(page.Size.Width, page.Size.Height));
            using var ms = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(ms, new Windows.Data.Pdf.PdfPageRenderOptions
                { DestinationWidth = (uint)(page.Size.Width * scale), DestinationHeight = (uint)(page.Size.Height * scale) });
            var dec = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(ms);
            using var bmp = await dec.GetSoftwareBitmapAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied);
            foreach (var line in (await engine.RecognizeAsync(bmp)).Lines) sb.AppendLine(line.Text);
            sb.AppendLine();
        }
        return sb.ToString().Trim() + "\r\n";
    }

    /// <summary>Texte d'un .docx, paragraphe par paragraphe, cadres compris (sans les doublons de compatibilité VML).</summary>
    internal static string DocxText(string docx)
    {
        using var zip = System.IO.Compression.ZipFile.OpenRead(docx);
        var entry = zip.GetEntry("word/document.xml");
        if (entry == null) return "";
        System.Xml.Linq.XDocument x;
        using (var st = entry.Open()) x = System.Xml.Linq.XDocument.Load(st);
        System.Xml.Linq.XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        System.Xml.Linq.XNamespace mc = "http://schemas.openxmlformats.org/markup-compatibility/2006";
        // chaque paragraphe garde son propre texte ; celui d'un cadre qu'il contient sort à part, juste après
        var lines = x.Descendants(w + "p")
            .Where(p => !p.Ancestors(mc + "Fallback").Any())
            .Select(p => (p, own: p.Descendants().Where(e => (e.Name == w + "t" || e.Name == w + "tab" || e.Name == w + "br" || e.Name == w + "cr")
                                                          && e.Ancestors(w + "p").First() == p).ToList()))
            .Where(q => q.own.Count > 0 || !q.p.Descendants(w + "p").Any())
            .Select(q => string.Concat(q.own.Select(e => e.Name == w + "tab" ? "\t" : e.Name == w + "t" ? e.Value : "\r\n")).TrimEnd());
        return Regex.Replace(string.Join("\r\n", lines), @"(\r\n){3,}", "\r\n\r\n").Trim() + "\r\n";
    }

    /// <summary>Document → PDF temporaire → une image par page.</summary>
    static async Task OfficeToImages(Job job, string input, string outDir, string name, CancellationToken ct)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"prisme-{Guid.NewGuid():N}.pdf");
        try
        {
            job.Indeterminate = true;
            await OfficeLock.WaitAsync(ct);
            try
            {
                job.Status = "Conversion…";
                if (!(await Task.Run(() => TryMsOffice(Path.GetFullPath(input), tmp)) && File.Exists(tmp) && new FileInfo(tmp).Length > 0))
                    await LibreOffice(job, Path.GetFullPath(input), tmp, "pdf", null, ct);
            }
            finally { OfficeLock.Release(); }
            job.Indeterminate = false;
            await PdfToImages(job, tmp, outDir, name, ct);
        }
        finally { TryDelete(tmp); }
    }

    /// <summary>Convertit avec LibreOffice vers <paramref name="output"/> et renvoie le chemin final
    /// (un dossier « nom » avec la page et ses images quand l'export HTML produit plusieurs fichiers).</summary>
    static async Task<string> LibreOffice(Job job, string input, string output, string filter, string infilter, CancellationToken ct)
    {
        var soffice = Engines.Soffice ?? await OfficeSetup.Ensure(job, ct); // première fois : proposé, téléchargé puis installé
        var tmp = Path.Combine(Path.GetTempPath(), "prisme-doc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            // copie sous un nom simple : évite les soucis d'accents et de fichiers ouverts ailleurs
            var src = Path.Combine(tmp, "in", "document" + Path.GetExtension(input).ToLowerInvariant());
            Directory.CreateDirectory(Path.GetDirectoryName(src));
            File.Copy(input, src);
            var profile = new Uri(Path.Combine(Settings.Dir, "office-profile")).AbsoluteUri;
            var args = new List<string> { $"-env:UserInstallation={profile}", "--headless", "--invisible", "--norestore", "--nolockcheck", "--nodefault" };
            if (infilter != null) args.Add("--infilter=" + infilter);
            args.AddRange(new[] { "--convert-to", filter, "--outdir", Path.Combine(tmp, "out"), src });
            string lastErr = "";
            await Engines.Run(soffice, args, null, l => { if (l.Trim().Length > 0) lastErr = l; }, ct);
            var outDir = Path.Combine(tmp, "out");
            var files = Directory.Exists(outDir) ? Directory.GetFiles(outDir) : Array.Empty<string>();
            var main = files.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f) == "document");
            if (main == null || new FileInfo(main).Length == 0)
                throw new Exception(lastErr.Contains("no export filter") ? "Cette conversion n'est pas possible pour ce document" : "Document illisible ou protégé par mot de passe");
            if (files.Length == 1) { File.Move(main, output, true); return output; }
            // page web + images : tout dans un dossier, la page garde le nom du document
            TryDelete(output);
            var folder = Formats.UniqueDir(Path.GetDirectoryName(output), Path.GetFileNameWithoutExtension(output));
            foreach (var f in files.Where(f => f != main)) File.Move(f, Path.Combine(folder, Path.GetFileName(f)));
            var final = Path.Combine(folder, Path.GetFileName(output));
            File.Move(main, final);
            return final;
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    /// <summary>Word / Excel / PowerPoint par automation, s'ils sont installés : meilleur rendu PDF possible.
    /// Un PDF en entrée est ouvert par Word et enregistré en .docx.</summary>
    static bool TryMsOffice(string input, string output)
    {
        var k = Formats.KindOf(input);
        if (k == Kind.Drawing || Formats.ExtOf(input) is "md" or "pages" or "numbers" or "key") return false;
        string app = k == Kind.Sheet ? "Excel.Application" : k == Kind.Slides ? "PowerPoint.Application" : "Word.Application";
        var type = Type.GetTypeFromProgID(app);
        if (type == null) return false;
        string exe = k == Kind.Sheet ? "EXCEL" : k == Kind.Slides ? "POWERPNT" : "WINWORD";
        var before = System.Diagnostics.Process.GetProcessesByName(exe).Select(p => p.Id).ToHashSet();

        bool ok = false;
        // Office écrit dans un fichier temporaire : s'il se débloque après le délai, il n'écrase pas le résultat de LibreOffice
        var tmp = Path.Combine(Path.GetTempPath(), $"prisme-office-{Guid.NewGuid():N}{Path.GetExtension(output)}");
        var t = new Thread(() =>
        {
            dynamic o = null;
            try
            {
                o = Activator.CreateInstance(type);
                if (app == "Word.Application")
                {
                    o.DisplayAlerts = 0;
                    var d = o.Documents.Open(input, false, true, false); // ConfirmConversions = false : pas de question pour un PDF
                    if (k == Kind.Pdf) d.SaveAs2(tmp, 16); // wdFormatDocumentDefault (.docx)
                    else d.ExportAsFixedFormat(tmp, 17);
                    d.Close(false);
                }
                else if (app == "Excel.Application")
                {
                    o.DisplayAlerts = false;
                    var w = o.Workbooks.Open(input, 0, true);
                    w.ExportAsFixedFormat(0, tmp); w.Close(false);
                }
                else
                {
                    var p = o.Presentations.Open(input, -1, 0, 0); // lecture seule, sans fenêtre
                    p.SaveAs(tmp, 32); p.Close();
                }
                ok = true;
            }
            catch { ok = false; }
            finally
            {
                try { o?.Quit(); } catch { }
                if (o != null) try { Marshal.FinalReleaseComObject(o); } catch { }
            }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.IsBackground = true; // un Office bloqué ne doit pas garder Prisme en vie après sa fermeture
        t.Start();
        if (!t.Join(TimeSpan.FromMinutes(3)))
        {   // Office bloqué (boîte de dialogue invisible) : on passe à LibreOffice, et l'Office lancé pour l'occasion est fermé de force.
            // Seulement s'il est né pendant la conversion et n'a aucune fenêtre : jamais celui que l'utilisateur a ouvert.
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(exe))
                try { if (!before.Contains(p.Id) && p.MainWindowHandle == IntPtr.Zero) p.Kill(); } catch { }
            return false;
        }
        if (!ok || !File.Exists(tmp)) { TryDelete(tmp); return false; }
        File.Move(tmp, output, true);
        return true;
    }
}