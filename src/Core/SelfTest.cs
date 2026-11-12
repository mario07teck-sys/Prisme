using System.Text;

namespace Prisme.Core;

/// <summary>Prisme.exe --selftest : génère des fichiers de test et essaie toutes les conversions.
/// Résultat dans %TEMP%\prisme-selftest.txt ; code de sortie = nombre d'échecs.</summary>
public static class SelfTest
{
    public static int Run() => Task.Run(RunAsync).GetAwaiter().GetResult();

    /// <summary>Prisme.exe --selftest &lt;dossier&gt; : convertit chaque vrai fichier du dossier vers tous ses formats,
    /// sorties dans &lt;dossier&gt;\out. Rapport : %TEMP%\prisme-selftest-real.txt (fichier → format, durée, taille ou erreur).</summary>
    public static int RunReal(string dir) => Task.Run(async () =>
    {
        var log = new StringBuilder(); int fails = 0;
        var s = new Settings { SaveNextToSource = false, OutputDir = Path.Combine(dir, "out") };
        foreach (var src in Directory.GetFiles(dir).Where(f => Formats.KindOf(f) != Kind.Unknown).OrderBy(f => f))
            foreach (var t in Formats.TargetsFor(src).Where(t => t.Id != "pdf-merge").ToList())
            {
                var job = new Job { Inputs = new() { src }, Target = t };
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    await Converter.Run(job, s);
                    var size = File.Exists(job.Output) ? new FileInfo(job.Output!).Length : Directory.EnumerateFiles(job.Output!).Sum(f => new FileInfo(f).Length);
                    log.AppendLine($"OK    {Path.GetFileName(src)} → {t.Id} | {sw.Elapsed.TotalSeconds:F0} s | {size / 1024} Ko | {job.Output}");
                }
                catch (Exception e) { fails++; log.AppendLine($"ÉCHEC {Path.GetFileName(src)} → {t.Id} | {sw.Elapsed.TotalSeconds:F0} s | {e.Message}"); }
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "prisme-selftest-real.txt"), log.ToString(), Encoding.UTF8);
            }
        log.AppendLine(fails == 0 ? "\nTOUT EST OK" : $"\n{fails} ÉCHEC(S)");
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "prisme-selftest-real.txt"), log.ToString(), Encoding.UTF8);
        return fails;
    }).GetAwaiter().GetResult();

    /// <summary>Prisme.exe --selftest-office &lt;dossier&gt; : vrai téléchargement de LibreOffice dans &lt;dossier&gt;\office (comme à la première
    /// conversion d'un document, sans toucher à %LOCALAPPDATA%\Prisme), puis un RTF → PDF avec. Rapport : %TEMP%\prisme-selftest-office.txt.</summary>
    public static int RunOffice(string dir) => Task.Run(async () =>
    {
        var log = new StringBuilder(); int fails = 0;
        void Check(bool ok, string what) { log.AppendLine((ok ? "OK    " : "ÉCHEC ") + what); if (!ok) fails++; }
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (url, size) = await OfficeSetup.Locate(CancellationToken.None);
            log.AppendLine($"MSI : {url} ({size / 1048576} Mo)");
            Check(!OfficeSetup.SignedByTdf(Environment.ProcessPath), "signature : un fichier qui n'est pas de The Document Foundation est refusé");
            string last = null;
            var office = Path.Combine(dir, "office");
            await OfficeSetup.Install(url, size, office, (s, _) =>
            {
                if (s != last && !s.StartsWith("Téléchargement")) log.AppendLine($"      {sw.Elapsed:mm\\:ss} {last = s}");
            }, CancellationToken.None);
            var mb = Directory.EnumerateFiles(office, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) / 1048576;
            log.AppendLine($"Installé en {sw.Elapsed.TotalSeconds:0} s, {mb} Mo");
            Check(!Directory.Exists(office + "-work"), "dossier de travail supprimé");
            var rtf = Path.Combine(dir, "lettre.rtf");
            File.WriteAllText(rtf, @"{\rtf1\ansi\f0\fs28 Bonjour Prisme \u233?t\u233? !\par}", Encoding.ASCII);
            await Engines.Run(Path.Combine(office, "program", "soffice.exe"), new[] { $"-env:UserInstallation={new Uri(Path.Combine(dir, "profil")).AbsoluteUri}",
                "--headless", "--norestore", "--convert-to", "pdf", "--outdir", dir, rtf }, null, null, CancellationToken.None);
            Check(File.Exists(Path.Combine(dir, "lettre.pdf")), "RTF → PDF avec le LibreOffice téléchargé");
        }
        catch (Exception e) { Check(false, "installation : " + e.Message); }
        log.AppendLine(fails == 0 ? "\nTOUT EST OK" : $"\n{fails} ÉCHEC(S)");
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "prisme-selftest-office.txt"), log.ToString(), Encoding.UTF8);
        return fails;
    }).GetAwaiter().GetResult();

    static async Task<int> RunAsync()
    {
        var log = new StringBuilder();
        int fails = 0;
        void Check(bool ok, string what) { log.AppendLine((ok ? "OK    " : "ÉCHEC ") + what); if (!ok) fails++; }

        // --- logique pure ---
        Check(Formats.KindOf("a.MP3") == Kind.Audio && Formats.KindOf("a.pdf") == Kind.Pdf && Formats.KindOf("a.xyz") == Kind.Unknown, "KindOf");
        Check(!Formats.TargetsFor("a.mp3").Any(t => t.Id == "mp3"), "pas de mp3 → mp3");
        Check(Formats.TargetsFor("a.jpeg").All(t => t.Id != "jpg"), "jpeg ≡ jpg");
        Check(Formats.TargetsFor(new[] { "a.mp3", "b.png" }).Count == 0, "audio + image : aucun format commun");
        Check(Formats.TargetsFor(new[] { "a.mp4", "b.mkv" }).Any(t => t.Id == "mp3"), "vidéos : formats communs");
        var dir = Path.Combine(Path.GetTempPath(), "prisme-selftest");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "x.txt"), "");
        Check(Formats.UniquePath(dir, "x", "txt").EndsWith("x (2).txt"), "UniquePath");
        Check(Downloader.IsVideoLink("https://www.youtube.com/watch?v=abc") && Downloader.IsVideoLink("https://x.com/a/status/1")
              && !Downloader.IsVideoLink("https://example.com/x") && !Downloader.IsUrl("bonjour"), "détection des liens");

        // --- fichiers de test ---
        async Task Gen(string name, params string[] a)
        {
            var args = new List<string> { "-hide_banner", "-nostdin", "-y" }; args.AddRange(a); args.Add(Path.Combine(dir, name));
            await Engines.Run(Engines.Ffmpeg, args, null, null, CancellationToken.None);
        }
        await Gen("son.wav", "-f", "lavfi", "-i", "sine=frequency=440:duration=3");
        await Gen("film.mp4", "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=25:duration=3", "-f", "lavfi", "-i", "sine=duration=3",
            "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest");
        await Gen("transparent.png", "-f", "lavfi", "-i", "color=c=red@0.5:s=320x200,format=rgba", "-frames:v", "1");
        await Gen("photo.jpg", "-f", "lavfi", "-i", "testsrc2=size=800x600", "-frames:v", "1");
        // cas limites relevés en relecture : son 5.1 « side », vidéo aux dimensions impaires
        await Gen("surround.ac3", "-f", "lavfi", "-i", "sine=duration=2", "-af", "pan=5.1(side)|FL=c0|FR=c0|FC=c0|LFE=c0|SL=c0|SR=c0", "-c:a", "ac3");
        await Gen("impair.mkv", "-f", "lavfi", "-i", "testsrc2=size=641x361:rate=25:duration=2", "-f", "lavfi", "-i", "sine=duration=2",
            "-af", "pan=5.1(side)|FL=c0|FR=c0|FC=c0|LFE=c0|SL=c0|SR=c0", "-c:v", "libx264", "-pix_fmt", "yuv444p", "-c:a", "ac3", "-shortest");
        await Gen("anim.gif", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=10:duration=2");
        File.WriteAllText(Path.Combine(dir, "lettre.rtf"), @"{\rtf1\ansi\f0\fs28 Bonjour Prisme \u233?t\u233? !\par}", Encoding.ASCII);
        File.WriteAllText(Path.Combine(dir, "notes.md"), "# Titre\n\nDu **gras**, une liste :\n\n- un\n- deux\n", Encoding.UTF8);
        File.WriteAllText(Path.Combine(dir, "tableau.csv"), "Nom,Âge\nÉlodie,31\nZoé,27\n", Encoding.UTF8);
        File.WriteAllText(Path.Combine(dir, "dessin.svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"200\" height=\"100\"><rect width=\"200\" height=\"100\" fill=\"#B565FF\"/></svg>", Encoding.UTF8);
        File.WriteAllText(Path.Combine(dir, "film.srt"), "1\n00:00:01,000 --> 00:00:02,500\nBonjour à tous\n\n2\n00:00:03,000 --> 00:00:04,000\nÇa marche !\n", Encoding.UTF8);

        var s = new Settings { SaveNextToSource = true };
        async Task Try(string label, List<string> inputs, string id)
        {
            var job = new Job { Inputs = inputs, Target = Formats.Find(inputs[0], id) };
            try
            {
                if (job.Target == null) throw new Exception("format introuvable");
                await Converter.Run(job, s);
                bool ok = job.Output != null && (File.Exists(job.Output) ? new FileInfo(job.Output).Length > 0 : Directory.EnumerateFiles(job.Output).Any());
                Check(ok, $"{label} → {id}");
            }
            catch (Exception e) { Check(false, $"{label} → {id} : {e.Message}"); }
        }

        // chaque fichier de test vers chacun des formats proposés
        // deux conversions simultanées ne doivent jamais viser le même fichier
        var reserved = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => Formats.UniquePath(dir, "course", "mp3"))));
        Check(reserved.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 8, "réservation des noms en parallèle");

        var sources = new[] { "son.wav", "surround.ac3", "film.mp4", "impair.mkv", "transparent.png", "photo.jpg", "anim.gif", "lettre.rtf", "notes.md", "tableau.csv", "dessin.svg", "film.srt" };
        foreach (var src in sources)
        {
            var p = Path.Combine(dir, src);
            if (!File.Exists(p)) { Check(false, $"génération de {src}"); continue; }
            foreach (var t in Formats.TargetsFor(p).ToList()) await Try(src, new() { p }, t.Id);
        }
        await Try("2 images", new() { Path.Combine(dir, "photo.jpg"), Path.Combine(dir, "transparent.png") }, "pdf");
        var pdfs = Directory.GetFiles(dir, "*.pdf").Where(f => !f.Contains(" - page"))
            .OrderByDescending(f => Path.GetFileName(f).StartsWith("lettre")).ToList(); // un PDF avec du vrai texte en premier
        if (pdfs.Count > 1)
        {
            foreach (var t in Formats.TargetsFor(pdfs[0]).Where(t => t.Id != "pdf-merge").ToList()) await Try("pdf", new() { pdfs[0] }, t.Id);
            await Try("pdf ×2", pdfs.Take(2).ToList(), "pdf-merge");
        }
        else Check(false, "PDF de test");
        // présentations : le PowerPoint produit par « PDF → PowerPoint » sert d'échantillon
        var pptx = Directory.GetFiles(dir, "*.pptx").FirstOrDefault();
        if (pptx != null) foreach (var t in Formats.TargetsFor(pptx).ToList()) await Try("pptx", new() { pptx }, t.Id);
        else Check(false, "PowerPoint de test");

        // le JPG d'une image transparente doit être aplati sur du blanc, pas du noir
        var jpg = Path.Combine(dir, "transparent.jpg");
        if (File.Exists(jpg))
        {
            var tmp = Path.Combine(dir, "px.raw");
            await Engines.Run(Engines.Ffmpeg, new[] { "-hide_banner", "-nostdin", "-y", "-i", jpg, "-vf", "crop=1:1:0:0,format=rgb24", "-f", "rawvideo", tmp }, null, null, CancellationToken.None);
            var b = File.Exists(tmp) ? File.ReadAllBytes(tmp) : Array.Empty<byte>();
            Check(b.Length == 3 && b[0] > 240 && b[1] > 100 && b[2] > 100, $"transparence → fond blanc (pixel {string.Join(",", b)})");
        }

        // défauts trouvés sur de vrais fichiers (1.1) : contenu vérifié, pas seulement la présence
        async Task<string> Probe(string f)
        {
            var sb = new StringBuilder();
            await Engines.Run(Engines.Ffmpeg, new[] { "-hide_banner", "-nostdin", "-i", Path.Combine(dir, f) }, null, l => sb.AppendLine(l), CancellationToken.None);
            return sb.ToString();
        }
        Check((await Probe("son.mp4")).Contains("Duration: 00:00:03"), "audio → mp4 : pas d'image en trop après la fin du son");
        Check((await Probe("anim.webp")).Contains("webp_anim") || (await Probe("anim.webp")).Contains("ANIM"), "gif animé → webp : animation conservée");
        Check(File.Exists(Path.Combine(dir, "lettre (2).txt")) && File.ReadAllText(Path.Combine(dir, "lettre (2).txt")).Contains("Bonjour"), "pdf → txt : texte retrouvé");
        await Gen("soustitre.mkv", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=25:duration=3", "-i", Path.Combine(dir, "film.srt"), "-map", "0", "-map", "1", "-c:v", "libx264", "-c:s", "srt");
        foreach (var id in new[] { "mp4", "webm" }) await Try("soustitre.mkv", new() { Path.Combine(dir, "soustitre.mkv") }, id);
        // extrait de 1,5 s à 3,5 s : « Ça marche ! » (3 s → 4 s) doit tomber à 1,5 s → 2,5 s
        if (await TryEdit("soustitre.mkv", "same", new Edit(1.5, 3.5, 0, 0, 1, 1, 0, 0)) is { } subCut)
        {
            var srt = new StringBuilder();
            await Engines.Run(Engines.Ffmpeg, new[] { "-hide_banner", "-v", "error", "-i", subCut, "-map", "0:s", "-f", "srt", "-" }, l => srt.AppendLine(l), null, CancellationToken.None);
            Check(srt.ToString().Contains("00:00:01,500 --> 00:00:02,500"), "extrait MKV : sous-titres recalés");
        }
        if (await TryEdit("soustitre.mkv", "mp4", new Edit(1.5, 3.5, 0, 0, 1, 1, 0, 0)) is { } subCut2)
            Check(Math.Abs(Dur(await Probe(subCut2)) - 1.5) < 0.25, "extrait MKV → MP4 (sans sous-titres décalés), 1,5 s : la vidéo s'arrête à 3 s");
        // film au son DTS 5.1 → « MKV pour TV » : son en Dolby Digital, image copiée sans réencodage
        await Gen("dts.mkv", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=25:duration=2", "-f", "lavfi", "-i", "sine=duration=2",
            "-af", "pan=5.1(side)|FL=c0|FR=c0|FC=c0|LFE=c0|SL=c0|SR=c0", "-c:v", "libx264", "-c:a", "dca", "-strict", "-2", "-shortest");
        await Try("dts.mkv", new() { Path.Combine(dir, "dts.mkv") }, "mkv-tv");
        var tv = await Probe("dts (2).mkv");
        Check((await Probe("dts.mkv")).Contains("Audio: dts") && tv.Contains("Audio: ac3") && tv.Contains("5.1") && tv.Contains("Video: h264"), "DTS → MKV pour TV : son Dolby 5.1");
        Check((await Probe("soustitre.mp4")).Contains("Subtitle") && (await Probe("soustitre.webm")).Contains("Subtitle"), "mkv → mp4/webm : sous-titres conservés");

        // texte d'un docx : paragraphe qui porte un cadre (texte des deux gardé), saut de ligne manuel
        var docx = Path.Combine(dir, "cadre.docx");
        using (var z = System.IO.Compression.ZipFile.Open(docx, System.IO.Compression.ZipArchiveMode.Create))
        using (var st = new StreamWriter(z.CreateEntry("word/document.xml").Open()))
            st.Write("""<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>Avant</w:t><w:br/><w:t>après</w:t></w:r><w:r><w:txbxContent><w:p><w:r><w:t>Cadre</w:t></w:r></w:p></w:txbxContent></w:r></w:p></w:body></w:document>""");
        var dt = Converter.DocxText(docx);
        Check(dt.Contains("Avant\r\naprès") && dt.Contains("Cadre"), $"texte d'un docx : cadre et saut de ligne ({dt.Trim().Replace("\r\n", " | ")})");

        // taille visée : 2 min de vidéo bruitée (dure à compresser) doivent tenir dans 10 Mo sans gaspiller la place
        await Gen("long.mp4", "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=25:duration=120,noise=alls=30:allf=t", "-f", "lavfi", "-i", "sine=duration=120",
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "28", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest");
        await Try("long.mp4", new() { Path.Combine(dir, "long.mp4") }, "mp4-10mo");
        var fit = Path.Combine(dir, "long (2).mp4");
        if (File.Exists(fit))
        {
            long size = new FileInfo(fit).Length; var pr = await Probe(Path.GetFileName(fit));
            Check(size <= 10L * 1000 * 1000 && size > 6L * 1000 * 1000 && pr.Contains("Audio:") && Math.Abs(Dur(pr) - 120) < 1,
                  $"vidéo de 2 min → ≤ 10 Mo : {size / 1000 / 1000.0:0.0} Mo, son conservé");
        }
        await Gen("trop-long.mp4", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=5:duration=900", "-c:v", "libx264", "-preset", "ultrafast");
        var tooLong = new Job { Inputs = new() { Path.Combine(dir, "trop-long.mp4") }, Target = Formats.Find("a.mp4", "mp4-10mo") };
        try { await Converter.Run(tooLong, s); Check(false, "15 min → 10 Mo : refus attendu"); }
        catch (Exception ex) { Check(ex.Message.Contains("trop longue") && !File.Exists(Path.Combine(dir, "trop-long (2).mp4")), "15 min → 10 Mo : refusé proprement, sans fichier vide"); }

        // --- mises à jour : lecture d'une release GitHub ---
        static Updater.Release Rel(string json, string engines = null) => Updater.Parse(System.Text.Json.JsonDocument.Parse(json).RootElement, engines);
        const string both = """{"tag_name":"v9.4.1","assets":[{"name":"Prisme-Setup-9.4.1.exe","browser_download_url":"https://github.com/a/b/Prisme-Setup-9.4.1.exe","size":300},{"name":"Prisme-Update-9.4.1-abc123.exe","browser_download_url":"https://github.com/a/b/Prisme-Update-9.4.1-abc123.exe","size":30}]}""";
        Check(Rel(both, "abc123")?.Name == "Prisme-Update-9.4.1-abc123.exe" && Rel(both, "fff999")?.Name == "Prisme-Setup-9.4.1.exe" && Rel(both)?.Size == 300,
              "mise à jour : légère si mêmes moteurs, complète sinon");
        var good = Rel("""{"tag_name":"v9.4.1","assets":[{"name":"notes.txt","browser_download_url":"https://github.com/x/y/notes.txt","size":1},{"name":"Prisme-Setup-9.4.1.exe","browser_download_url":"https://github.com/mario07teck-sys/Prisme/releases/download/v9.4.1/Prisme-Setup-9.4.1.exe","size":123,"digest":"sha256:ABCD"}]}""");
        Check(good is { Size: 123, Sha256: "abcd" } && good.Version == new Version(9, 4, 1) && Updater.IsNewer(good), "mise à jour : release lue");
        Check(Rel("""{"tag_name":"v9.9.9","assets":[{"name":"Prisme-Setup-9.9.9.exe","browser_download_url":"https://evil.example/Prisme-Setup.exe","size":1}]}""") == null,
              "mise à jour : installeur hors de github.com refusé");
        Check(!Updater.IsNewer(Rel("""{"tag_name":"v1.0.0","assets":[{"name":"Prisme-Setup-1.0.0.exe","browser_download_url":"https://github.com/a/b/Prisme-Setup-1.0.0.exe","size":1}]}""")),
              "mise à jour : version plus ancienne ignorée");

        // --- onglet Retoucher : extrait, recadrage, taille, assemblage (contenu vérifié) ---
        Check(Edit.TryParseTime("1:30.5", out var t1) && Math.Abs(t1 - 90.5) < 1e-9 && Edit.TryParseTime("90", out var t2) && t2 == 90
              && Edit.TryParseTime("1:02:03", out var t3) && t3 == 3723 && !Edit.TryParseTime("1::2", out _) && Edit.Time(90.5) == "1:30.5", "lecture des temps");
        Check(Formats.Find("a.mp3", "same")?.Id == "mp3" && Formats.Find("a.heic", "same")?.Id == "jpg" && Formats.Find("a.aac", "same")?.Id == "m4a"
              && Formats.Find("a.jpeg", "same")?.Id == "jpg" && Formats.Find("a.pdf", "same") == null, "« même format »");
        async Task<string> TryEdit(string src, string id, Edit e)
        {
            var p = Path.Combine(dir, src);
            var job = new Job { Inputs = new() { p }, Target = Formats.Find(p, id), Edits = new Dictionary<string, Edit> { [p] = e } };
            try { await Converter.Run(job, s); return job.Output; }
            catch (Exception ex) { Check(false, $"{src} retouché → {id} : {ex.Message}"); return null; }
        }
        static double Dur(string probe) => System.Text.RegularExpressions.Regex.Match(probe, @"Duration: (\d+):(\d\d):(\d\d\.\d+)") is { Success: true } m
            ? int.Parse(m.Groups[1].Value) * 3600 + int.Parse(m.Groups[2].Value) * 60 + double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture) : -1;
        var cut = new Edit(0.5, 2, 0, 0, 1, 1, 0, 0); // impair.mkv ne dure que 2 s
        foreach (var (src, id) in new[] { ("son.wav", "same"), ("son.wav", "mp3"), ("son.wav", "m4r"), ("film.mp4", "same"), ("impair.mkv", "same"), ("film.mp4", "gif") })
            if (await TryEdit(src, id, cut) is { } o)
            {
                var d = Dur(await Probe(o));
                Check(Math.Abs(d - 1.5) < 0.25, $"extrait 0,5 s → 2 s de {src} en {id} : {d:0.00} s ({Path.GetFileName(o)})");
            }
        (s.GifFps, s.GifQuality) = (8, "light");
        if (await TryEdit("film.mp4", "gif", cut) is { } gif8)
        {
            var pr = await Probe(gif8);
            var fps = System.Text.RegularExpressions.Regex.Match(pr, @"([\d.]+) fps").Groups[1].Value; // GIF : délais en centièmes, 8 i/s ≈ 8,33
            Check(double.TryParse(fps, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) && f is > 7.5 and < 8.6
                  && pr.Contains("360x20"), $"GIF réglé à 8 images/s, léger (360 px) : {fps} fps");
        }
        (s.GifFps, s.GifQuality) = (12, "normal");
        if (await TryEdit("film.mp4", "mp4-anim", cut) is { } wa)
        {
            var pr = await Probe(wa);
            Check(!pr.Contains("Audio:") && Math.Abs(Dur(pr) - 1.5) < 0.25, "extrait → Animation WhatsApp : muet, 1,5 s");
        }
        if (await TryEdit("son.wav", "same", new Edit(2, 0, 0, 0, 1, 1, 0, 0)) is { } tail)
            Check(Math.Abs(Dur(await Probe(tail)) - 1) < 0.25, "extrait de 2 s jusqu'à la fin");
        // rotation, vitesse, volume (1.2.6) : film.mp4 fait 640×360 et dure 3 s
        if (await TryEdit("film.mp4", "same", Edit.None with { Rotate = 90 }) is { } rot)
            Check((await Probe(rot)).Contains("360x640"), "rotation de 90° d'une vidéo (360x640 attendu)");
        if (await TryEdit("impair.mkv", "same", Edit.None with { Rotate = 270, Volume = 0.5 }) is { } rotMkv)
            Check((await Probe(rotMkv)).Contains("360x640"), "rotation d'un MKV (copie impossible, réencodé) : 360x640 attendu");
        if (await TryEdit("film.mp4", "same", Edit.None with { Speed = 2 }) is { } fast)
        {
            var pr = await Probe(fast);
            Check(Math.Abs(Dur(pr) - 1.5) < 0.25 && pr.Contains("Audio:"), $"vitesse ×2 : 3 s deviennent 1,5 s, son gardé ({Dur(pr):0.00} s)");
        }
        if (await TryEdit("film.mp4", "gif", cut with { Speed = 2, Rotate = 90 }) is { } fastGif)
            Check(Math.Abs(Dur(await Probe(fastGif)) - 0.75) < 0.2, "extrait de 1,5 s en ×2 vers GIF : 0,75 s");
        if (await TryEdit("son.wav", "mp3", Edit.None with { Speed = 0.5, Volume = 2 }) is { } slow)
            Check(Math.Abs(Dur(await Probe(slow)) - 6) < 0.3, "son ralenti ×0,5 et plus fort : 3 s deviennent 6 s");
        // carte graphique : commande réécrite, et conversion réussie qu'il y ait une carte compatible ou non (retour au processeur)
        var gpuArgs = Converter.FfmpegArgs("a.mkv", "mp4-hevc", "b.mp4", Kind.Video);
        Check(Converter.UseGpu(gpuArgs, "qsv") && gpuArgs.Contains("hevc_qsv") && gpuArgs.Contains("-global_quality") && !gpuArgs.Contains("-crf") && !gpuArgs.Contains("-preset")
              && !Converter.UseGpu(Converter.FfmpegArgs("a.mkv", "mp4-10mo", "b.mp4", Kind.Video), "qsv"), "carte graphique : commande réécrite, taille visée laissée au processeur");
        s.Gpu = true;
        if (await TryEdit("film.mp4", "mp4-hevc", Edit.None) is { } gpu)
        {
            var pr = await Probe(gpu);
            Check(pr.Contains("Video: hevc") && Math.Abs(Dur(pr) - 3) < 0.25, "conversion avec le réglage « carte graphique »");
        }
        s.Gpu = false;
        Check(Downloader.IsPlaylist("https://www.youtube.com/playlist?list=PLx") && Downloader.IsPlaylist("https://soundcloud.com/a/sets/b")
              && !Downloader.IsPlaylist("https://www.youtube.com/watch?v=abc&list=PLx"), "détection des playlists");
        // photo 800×600 : moitié droite (400×600), ramenée à 200×300
        if (await TryEdit("photo.jpg", "same", new Edit(0, 0, 0.5, 0, 0.5, 1, 200, 300)) is { } crop)
            Check((await Probe(crop)).Contains("200x300"), "recadrage + taille d'une photo (200x300 attendu)");
        if (await TryEdit("photo.jpg", "png", new Edit(0, 0, 0.25, 0.25, 0.5, 0.5, 0, 0)) is { } crop2)
            Check((await Probe(crop2)).Contains("400x300"), "recadrage seul, vers PNG (400x300 attendu)");
        if (await TryEdit("transparent.png", "webp", new Edit(0, 0, 0, 0, 1, 1, 160, 100)) is { } small)
            Check((await Probe(small)).Contains("160x100"), "redimensionnement seul (160x100 attendu)");
        // un son stéréo de 3 s puis du 5.1 de 2 s : formats différents, mis bout à bout
        var mixFiles = new List<string> { Path.Combine(dir, "son.wav"), Path.Combine(dir, "surround.ac3") };
        var mix = new Job { Inputs = mixFiles, Target = Formats.Find(mixFiles[0], "merge-mp3") };
        try
        {
            await Converter.Run(mix, s);
            Check(Math.Abs(Dur(await Probe(mix.Output)) - 5) < 0.3, $"assemblage de 2 sons différents en MP3 : {Dur(await Probe(mix.Output)):0.00} s (5 attendues)");
        }
        catch (Exception ex) { Check(false, "assemblage : " + ex.Message); }

        // PDF scanné (une image de texte, rien de sélectionnable) → txt par l'OCR de Windows
        using (var bmp = new System.Drawing.Bitmap(1700, 400))
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        using (var font = new System.Drawing.Font("Arial", 60))
        {
            g.Clear(System.Drawing.Color.White);
            g.DrawString("Facture du garagiste", font, System.Drawing.Brushes.Black, 40, 140);
            bmp.Save(Path.Combine(dir, "scan.png"), System.Drawing.Imaging.ImageFormat.Png);
        }
        await Try("scan.png", new() { Path.Combine(dir, "scan.png") }, "pdf");
        await Try("scan.pdf", new() { Path.Combine(dir, "scan.pdf") }, "txt");
        var ocr = Path.Combine(dir, "scan.txt");
        Check(File.Exists(ocr) && File.ReadAllText(ocr).Contains("garagiste", StringComparison.OrdinalIgnoreCase), "pdf scanné → txt : texte lu par l'OCR");

        log.AppendLine(fails == 0 ? "\nTOUT EST OK" : $"\n{fails} ÉCHEC(S)");
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "prisme-selftest.txt"), log.ToString(), Encoding.UTF8);
        return fails;
    }
}
