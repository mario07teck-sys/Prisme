using System.Runtime.InteropServices;
using System.Text.Json;

namespace Prisme.Core;

public class Settings
{
    public bool SaveNextToSource { get; set; } = true;
    public string OutputDir { get; set; } = "";
    public string DownloadDir { get; set; } = DefaultDownloadDir();
    public bool StartWithWindows { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool ContextMenu { get; set; } = true;
    public bool WatchClipboard { get; set; } = true;
    public bool Notify { get; set; } = true;
    public bool AutoUpdate { get; set; } = true; // installe seule les nouvelles versions publiées sur GitHub
    public int GifFps { get; set; } = 12;
    public string GifQuality { get; set; } = "normal"; // light, normal, high
    public bool DownloadSubs { get; set; }        // sous-titres de l'auteur rangés dans le MP4 téléchargé
    public bool Gpu { get; set; }                 // encodage vidéo par la carte graphique : plus rapide, fichiers un peu plus lourds
    public DateTime LastEngineUpdate { get; set; }
    public string LastVersion { get; set; } = "";

    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Prisme");
    static string FilePath => Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        try
        {
            var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
            // ancien défaut (Téléchargements tout court, 1.0) → Téléchargements\Prisme
            if (string.IsNullOrWhiteSpace(s.DownloadDir) || !Directory.Exists(s.DownloadDir)
                || string.Equals(Path.TrimEndingDirectorySeparator(s.DownloadDir), KnownDownloads(), StringComparison.OrdinalIgnoreCase))
                s.DownloadDir = DefaultDownloadDir();
            return s;
        }
        catch { return new Settings(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, FilePath, true);
    }

    /// <summary>Dossier où ranger une conversion de <paramref name="input"/>.</summary>
    public string OutputFor(string input) =>
        !SaveNextToSource && Directory.Exists(OutputDir) ? OutputDir : Path.GetDirectoryName(input);

    /// <summary>Téléchargements\Prisme (créé au besoin, pour que « Ouvrir le dossier » marche tout de suite).</summary>
    public static string DefaultDownloadDir()
    {
        var d = Path.Combine(KnownDownloads(), "Prisme");
        try { Directory.CreateDirectory(d); } catch { }
        return d;
    }

    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    /// <summary>Le vrai dossier « Téléchargements », même déplacé par l'utilisateur.</summary>
    public static string KnownDownloads()
    {
        if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out var p) == 0)
        {
            var s = Marshal.PtrToStringUni(p); Marshal.FreeCoTaskMem(p); return s;
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }
}
