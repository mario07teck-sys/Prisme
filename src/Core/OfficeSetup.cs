using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace Prisme.Core;

/// <summary>LibreOffice n'est plus dans l'installeur : il est téléchargé à la première conversion qui en a vraiment besoin.
/// MSI officiel de The Document Foundation, signature vérifiée, extrait sans installation puis allégé
/// (ce que faisait tools\update-engines.ps1 pour l'installeur), dans %LOCALAPPDATA%\Prisme\office.</summary>
public static class OfficeSetup
{
    const string Fallback = "26.8.2"; // si la liste des versions est illisible
    const string Stable = "https://download.documentfoundation.org/libreoffice/stable/";

    /// <summary>Dossier gardé d'une mise à jour de Prisme à l'autre, toujours accessible en écriture.</summary>
    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Prisme", "office");

    /// <summary>Interface : propose le téléchargement (taille en octets) ; false = refusé. Absent (autotest) : téléchargé sans demander.</summary>
    public static Func<Job, long, Task<bool>> Ask;
    /// <summary>Interface : installation terminée (exception null) ou ratée.</summary>
    public static event Action<Job, Exception> Finished;
    static DateTime _declined;

    const string Needed = "Cette conversion a besoin de LibreOffice, qui n'a pas été téléchargé";

    /// <summary>Propose, télécharge et installe LibreOffice pour <paramref name="job"/> ; renvoie soffice.exe.
    /// Appelé sous le verrou des documents : une seule installation à la fois, les suivants trouvent LibreOffice.</summary>
    public static async Task<string> Ensure(Job job, CancellationToken ct)
    {
        // refusé il y a un instant : les documents ajoutés en même temps échouent sans reposer la question
        if (job.Created < _declined) throw new Exception(Needed);
        string url; long size;
        job.Status = "Recherche de LibreOffice…";
        try { (url, size) = await Locate(ct); }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            App.Log("LibreOffice introuvable en ligne : " + ex.Message);
            throw new Exception("LibreOffice (nécessaire pour ce document) ne peut pas être téléchargé pour l'instant : pas de connexion ?");
        }
        bool ok = Ask == null || await Ask(job, size);
        ct.ThrowIfCancellationRequested(); // tâche annulée pendant la question
        if (!ok) { _declined = DateTime.Now; App.Log("LibreOffice : téléchargement refusé"); throw new Exception(Needed); }

        try
        {
            App.Log($"LibreOffice : installation depuis {url} ({size / 1048576} Mo)");
            var sw = Stopwatch.StartNew();
            await Task.Run(() => Install(url, size, Dir, (status, p) =>
            {
                job.Status = status;
                job.Indeterminate = p < 0;
                if (p >= 0) job.Progress = Math.Floor(p);
            }, ct), ct);
            App.Log($"LibreOffice : installé dans {Dir} en {sw.Elapsed.TotalSeconds:0} s");
            Finished?.Invoke(job, null);
        }
        catch (Exception ex) { Finished?.Invoke(job, ex); throw; }
        job.Status = "Conversion…"; job.Indeterminate = true;
        return Path.Combine(Dir, "program", "soffice.exe");
    }

    /// <summary>Dernière version stable dont le MSI Windows 64 bits existe : (lien, taille).</summary>
    public static async Task<(string Url, long Size)> Locate(CancellationToken ct)
    {
        var versions = new List<Version>();
        try
        {
            var html = await Updater.Http.GetStringAsync(Stable, ct);
            versions.AddRange(Regex.Matches(html, @"href=""(\d+\.\d+\.\d+)/""").Select(m => Version.Parse(m.Groups[1].Value)).OrderDescending());
        }
        catch (Exception ex) when (!ct.IsCancellationRequested) { App.Log("LibreOffice : liste des versions illisible : " + ex.Message); }
        versions.Add(Version.Parse(Fallback));
        foreach (var v in versions.Distinct())
        {
            var url = $"{Stable}{v}/win/x86_64/LibreOffice_{v}_Win_x86-64.msi";
            using var resp = await Updater.Http.SendAsync(new HttpRequestMessage(HttpMethod.Head, url), ct);
            if (resp.IsSuccessStatusCode && resp.Content.Headers.ContentLength is long len && len > 0) return (url, len);
        }
        throw new Exception("aucun MSI de LibreOffice trouvé");
    }

    /// <summary>Télécharge (avec reprise), vérifie, extrait et allège LibreOffice dans <paramref name="dest"/>.
    /// Tout se fait dans un dossier de travail voisin puis un seul renommage : jamais de LibreOffice à moitié installé.</summary>
    /// <param name="progress">étape et pourcentage (négatif : durée inconnue)</param>
    public static async Task Install(string url, long size, string dest, Action<string, double> progress, CancellationToken ct)
    {
        var work = dest + "-work"; // même disque que dest : on déplace au lieu de copier
        var name = Path.GetFileName(new Uri(url).LocalPath);
        var msi = Path.Combine(work, name);
        string x = Path.Combine(work, "x"), o = Path.Combine(work, "office");
        Directory.CreateDirectory(work);
        foreach (var f in Directory.GetFiles(work).Where(f => !Path.GetFileName(f).StartsWith(name))) File.Delete(f); // .part d'une autre version
        try
        {
            if (!File.Exists(msi))
            {
                var part = msi + ".part"; // reste d'un téléchargement coupé : on reprend où il s'était arrêté
                var mb = size / 1048576.0;
                var r = new Updater.Release(null, url, size, null, name);
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        await Updater.Fetch(r, part, p => progress($"Téléchargement de LibreOffice… {p:0} % ({p * mb / 100:0} / {mb:0} Mo)", p), ct);
                        break;
                    }
                    catch (Exception ex) when (attempt < 5 && !ct.IsCancellationRequested)
                    {
                        App.Log($"LibreOffice : téléchargement interrompu ({ex.Message}), nouvel essai");
                        progress("Connexion interrompue, nouvel essai…", -1);
                        await Task.Delay(TimeSpan.FromSeconds(10), ct);
                    }
                }
                File.Move(part, msi);
            }

            progress("Vérification de la signature…", -1);
            if (!SignedByTdf(msi))
            {
                File.Delete(msi);
                throw new Exception("LibreOffice téléchargé refusé : il n'est pas signé par The Document Foundation");
            }
            ct.ThrowIfCancellationRequested();

            foreach (var d in new[] { x, o }) if (Directory.Exists(d)) Directory.Delete(d, true); // reste d'une installation interrompue
            progress("Installation de LibreOffice…", -1);
            await Extract(msi, x, ct);
            progress("Allègement…", -1);
            Slim(x, o);
            if (!File.Exists(Path.Combine(o, "program", "soffice.exe"))) throw new Exception("LibreOffice extrait incomplet (soffice.exe absent)");
            ct.ThrowIfCancellationRequested();
            if (Directory.Exists(dest)) Directory.Delete(dest, true); // ancien dossier sans soffice.exe
            Directory.Move(o, dest);
            progress("LibreOffice est prêt", 100);
        }
        catch
        {   // le MSI (ou son .part) est gardé pour reprendre ; le reste ne sert plus
            foreach (var d in new[] { x, o }) try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
            throw;
        }
        try { Directory.Delete(work, true); } catch (Exception ex) { App.Log("LibreOffice : nettoyage : " + ex.Message); }
    }

    /// <summary>Extraction administrative du MSI (msiexec /a) : rien n'est installé dans Windows, aucun droit administrateur.</summary>
    static async Task Extract(string msi, string target, CancellationToken ct)
    {
        // chemins normalisés : msiexec refuse les « / » (code 1619)
        using var p = Process.Start(new ProcessStartInfo("msiexec.exe", $"/a \"{Path.GetFullPath(msi)}\" /qn TARGETDIR=\"{Path.GetFullPath(target)}\"")
            { UseShellExecute = false, CreateNoWindow = true });
        using (ct.Register(() => { try { p.Kill(); } catch { } }))
            await p.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        if (p.ExitCode is not (0 or 3010)) throw new Exception($"Extraction de LibreOffice impossible (msiexec : code {p.ExitCode})");
    }

    /// <summary>Garde le français et l'anglais, sans aide, galeries, modèles ni Python : ≈ 550 Mo au lieu de 1,5 Go.</summary>
    static void Slim(string x, string o)
    {
        static void Del(string p) { if (Directory.Exists(p)) Directory.Delete(p, true); else if (File.Exists(p)) File.Delete(p); }
        var prog = Path.Combine(x, "program");
        var res = Path.Combine(prog, "resource");
        if (Directory.Exists(res)) foreach (var e in Directory.GetFileSystemEntries(res).Where(e => Path.GetFileName(e) != "fr")) Del(e);
        foreach (var d in Directory.GetDirectories(prog, "python-core-*")) Del(d);
        foreach (var f in Directory.GetFiles(prog, "python*.exe").Concat(Directory.GetFiles(prog, "python*.dll"))) Del(f);
        var share = Path.Combine(x, "share");
        foreach (var d in new[] { "extensions", "gallery", "template", "autotext", "wizards", "tipoftheday" }) Del(Path.Combine(share, d));
        var config = Path.Combine(share, "config");
        if (Directory.Exists(config)) foreach (var f in Directory.GetFiles(config, "images_*.zip").Where(f => !f.EndsWith("colibre.zip"))) Del(f);
        var reg = Path.Combine(share, "registry", "res");
        if (Directory.Exists(reg)) foreach (var f in Directory.GetFiles(reg).Where(f => !Regex.IsMatch(Path.GetFileName(f), @"_(fr|en-US)\.xcd$"))) Del(f);

        Directory.CreateDirectory(o);
        Directory.Move(prog, Path.Combine(o, "program"));
        Directory.Move(share, Path.Combine(o, "share"));
        if (Directory.Exists(Path.Combine(x, "presets"))) Directory.Move(Path.Combine(x, "presets"), Path.Combine(o, "presets"));
        MoveFiles(Path.Combine(x, "Fonts"), Path.Combine(o, "share", "fonts", "truetype"), true); // Carlito/Caladea = métriques de Calibri/Cambria
        MoveFiles(Path.Combine(x, "System64"), Path.Combine(o, "program"), false);                // runtime VC++ : marche sans rien installer
        foreach (var f in new[] { "LICENSE.html", "NOTICE" })
            if (File.Exists(Path.Combine(x, f))) File.Move(Path.Combine(x, f), Path.Combine(o, f));
    }

    static void MoveFiles(string src, string dst, bool recursive)
    {
        if (!Directory.Exists(src)) return;
        Directory.CreateDirectory(dst);
        foreach (var f in Directory.GetFiles(src)) File.Move(f, Path.Combine(dst, Path.GetFileName(f)), true);
        if (recursive) foreach (var d in Directory.GetDirectories(src)) MoveFiles(d, Path.Combine(dst, Path.GetFileName(d)), true);
    }

    // ---------- signature Authenticode (comme Get-AuthenticodeSignature) ----------

    /// <summary>Signature valide (chaîne de confiance de Windows) et signataire « The Document Foundation ».</summary>
    internal static bool SignedByTdf(string file)
    {
        if (!AuthenticodeValid(file)) return false;
        try
        {
#pragma warning disable SYSLIB0057 // lecture du certificat du signataire principal, déjà validé ci-dessus
            using var signed = X509Certificate.CreateFromSignedFile(file);
            using var cert = new X509Certificate2(signed);
#pragma warning restore SYSLIB0057
            // nom exact du signataire (CN) : « Contains » accepterait un « The Document Foundation Fake Ltd »
            return cert.GetNameInfo(X509NameType.SimpleName, false) == "The Document Foundation";
        }
        catch { return false; }
    }

    static bool AuthenticodeValid(string file)
    {
        var path = Marshal.StringToHGlobalUni(file);
        var info = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        try
        {
            Marshal.StructureToPtr(new TrustFile { cbStruct = (uint)Marshal.SizeOf<TrustFile>(), pcwszFilePath = path }, info, false);
            var data = new TrustData
            {
                cbStruct = (uint)Marshal.SizeOf<TrustData>(),
                dwUIChoice = 2,          // WTD_UI_NONE
                fdwRevocationChecks = 0, // WTD_REVOKE_NONE
                dwUnionChoice = 1,       // WTD_CHOICE_FILE
                pFile = info,
            };
            var action = new Guid("00AAC56B-CD44-11d0-8CC2-00C04FC295EE"); // WINTRUST_ACTION_GENERIC_VERIFY_V2
            return WinVerifyTrust(IntPtr.Zero, ref action, ref data) == 0;
        }
        finally { Marshal.FreeHGlobal(info); Marshal.FreeHGlobal(path); }
    }

    [DllImport("wintrust.dll")] static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref TrustData data);

    [StructLayout(LayoutKind.Sequential)]
    struct TrustFile { public uint cbStruct; public IntPtr pcwszFilePath, hFile, pgKnownSubject; }

    [StructLayout(LayoutKind.Sequential)]
    struct TrustData
    {
        public uint cbStruct; public IntPtr pPolicyCallbackData, pSIPClientData;
        public uint dwUIChoice, fdwRevocationChecks, dwUnionChoice; public IntPtr pFile;
        public uint dwStateAction; public IntPtr hWVTStateData, pwszURLReference;
        public uint dwProvFlags, dwUIContext; public IntPtr pSignatureSettings;
    }
}
