using System.Diagnostics;
using System.Text;

namespace Prisme.Core;

/// <summary>Les moteurs embarqués (dossier engines\ à côté de Prisme.exe) et l'exécution de leurs processus.</summary>
public static class Engines
{
    public static string Dir => Path.Combine(AppContext.BaseDirectory, "engines");
    public static string Ffmpeg => Path.Combine(Dir, "ffmpeg.exe");
    public static string YtDlp => Path.Combine(Dir, "yt-dlp.exe");

    /// <summary>LibreOffice téléchargé par Prisme, sinon celui des anciens installeurs, sinon celui installé sur le PC ; null s'il faut le télécharger.</summary>
    public static string Soffice
    {
        get
        {
            foreach (var p in new[]
            {
                Path.Combine(OfficeSetup.Dir, "program", "soffice.exe"),
                Path.Combine(Dir, "office", "program", "soffice.exe"),
                @"C:\Program Files\LibreOffice\program\soffice.exe",
                @"C:\Program Files (x86)\LibreOffice\program\soffice.exe",
            })
                if (File.Exists(p)) return p;
            return null;
        }
    }

    /// <summary>Lance un moteur, transmet chaque ligne de sortie, tue tout l'arbre si on annule.</summary>
    public static async Task<int> Run(string exe, IEnumerable<string> args, Action<string> onOut, Action<string> onErr,
        CancellationToken ct, IDictionary<string, string> env = null)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        if (env != null) foreach (var kv in env) psi.Environment[kv.Key] = kv.Value;

        using var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => { if (e.Data != null) onOut?.Invoke(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) onErr?.Invoke(e.Data); };
        // démarrage hors du fil de l'interface : ffmpeg pèse 100 Mo et l'antivirus peut le réanalyser plusieurs secondes
        // avant qu'il parte, ce qui figeait la fenêtre au chargement d'une vidéo
        await Task.Run(() =>
        {
            p.Start();
            try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { } // le PC reste fluide pendant une conversion
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
        });
        using (ct.Register(() => { try { p.Kill(true); } catch { } }))
            await p.WaitForExitAsync(CancellationToken.None);
        await Task.Run(() => p.WaitForExit()); // vide les dernières lignes
        ct.ThrowIfCancellationRequested();
        return p.ExitCode;
    }
}
