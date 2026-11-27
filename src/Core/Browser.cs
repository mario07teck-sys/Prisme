using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Prisme.Core;

/// <summary>Extension « Prisme » de Chrome, Edge et Brave : clic droit sur une vidéo → téléchargée par Prisme.
/// Le navigateur lance Prisme.exe (hôte de « native messaging ») avec un message { url, mode } sur l'entrée standard ;
/// Prisme le passe à l'instance ouverte (ou en démarre une, rangée près de l'horloge) puis répond { ok }.</summary>
public static class Browser
{
    public const string HostName = "fr.marioteck.prisme";
    public const string ExtensionId = "amcefaaoidllnkcacjlfndkmpkacikhl"; // fixé par la clé publique du manifest de l'extension
    static readonly string[] Keys =
    {
        @"Software\Google\Chrome\NativeMessagingHosts", @"Software\Microsoft\Edge\NativeMessagingHosts",
        @"Software\BraveSoftware\Brave-Browser\NativeMessagingHosts", @"Software\Chromium\NativeMessagingHosts",
    };
    static string ManifestPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Prisme", "navigateur.json");

    /// <summary>Dossier de l'extension, livré à côté de Prisme.exe (c'est lui qu'on charge dans le navigateur).</summary>
    public static string ExtensionDir => Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "extension");

    /// <summary>Déclare Prisme.exe comme hôte auprès des navigateurs (HKCU, sans droits admin). Refait à chaque démarrage de la copie installée.</summary>
    public static void Register()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath)!);
        File.WriteAllText(ManifestPath, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["name"] = HostName, ["description"] = "Prisme", ["path"] = Environment.ProcessPath, ["type"] = "stdio",
            ["allowed_origins"] = new[] { $"chrome-extension://{ExtensionId}/" },
        }));
        foreach (var k in Keys)
            using (var key = Registry.CurrentUser.CreateSubKey($@"{k}\{HostName}")) key.SetValue("", ManifestPath);
    }

    public static void Unregister()
    {
        foreach (var k in Keys) Registry.CurrentUser.DeleteSubKeyTree($@"{k}\{HostName}", false);
        try { File.Delete(ManifestPath); } catch { }
    }

    static readonly string[] Modes = { "best", "1080", "720", "mp3" };

    /// <summary>Lancé par le navigateur : lit une demande, la transmet, répond. Ne montre aucune fenêtre.</summary>
    public static int Host(Action<string[]> sendToOpenPrisme, bool prismeOpen)
    {
        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();
        object reply;
        try
        {
            var len = new byte[4];
            stdin.ReadExactly(len);
            int n = BitConverter.ToInt32(len);
            if (n is <= 0 or > 64 * 1024) throw new InvalidDataException("message trop long");
            var body = new byte[n];
            stdin.ReadExactly(body);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("ping", out _)) // la petite fenêtre de l'extension : Prisme est-il là ?
                reply = new { ok = true, version = $"{Updater.Current.Major}.{Updater.Current.Minor}.{Updater.Current.Build}", dir = App.Settings.DownloadDir };
            else
            {
                var url = doc.RootElement.GetProperty("url").GetString()?.Trim();
                var mode = doc.RootElement.TryGetProperty("mode", out var m) ? m.GetString() : "best";
                // la page web choisit le lien : seuls un lien http(s) et un mode connu passent
                if (!Downloader.IsUrl(url) || !Modes.Contains(mode)) throw new InvalidDataException("lien refusé : " + url);
                var args = new[] { "--download", url, mode, "--silent" };
                if (prismeOpen) sendToOpenPrisme(args);
                // par le Shell : le nouveau Prisme n'hérite pas des tuyaux du navigateur (sinon la réponse n'arrive qu'à sa fermeture)
                else Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--download \"{url.Replace("\"", "%22")}\" {mode} --silent") { UseShellExecute = true });
                App.Log($"navigateur : {mode} {url}");
                reply = new { ok = true };
            }
        }
        catch (Exception ex) { App.Log("navigateur : " + ex.Message); reply = new { ok = false, error = ex.Message }; }
        var json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(reply));
        stdout.Write(BitConverter.GetBytes(json.Length));
        stdout.Write(json);
        stdout.Flush();
        return 0;
    }

    /// <summary>L'extension est-elle déjà ajoutée à un profil de Chrome, Edge ou Brave ? (son identifiant figure dans les préférences du profil)</summary>
    public static bool ExtensionInstalled()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var data in new[] { @"Google\Chrome\User Data", @"Microsoft\Edge\User Data", @"BraveSoftware\Brave-Browser\User Data" })
        {
            var root = Path.Combine(local, data);
            if (!Directory.Exists(root)) continue;
            foreach (var profile in Directory.EnumerateDirectories(root).Where(d => Path.GetFileName(d) is "Default" || Path.GetFileName(d).StartsWith("Profile ")))
                foreach (var f in new[] { "Secure Preferences", "Preferences" })
                    try
                    {
                        var p = Path.Combine(profile, f);
                        if (File.Exists(p) && File.ReadAllText(p).Contains(ExtensionId)) return true;
                    }
                    catch (IOException) { } // fichier tenu par le navigateur
        }
        return false;
    }

    /// <summary>Chrome (sinon Edge, sinon Brave) : le chemin de son exe, pour ouvrir sa page des extensions.</summary>
    public static (string Exe, string Name, string Page)? Find()
    {
        foreach (var (exe, name, page) in new[] { ("chrome.exe", "Chrome", "chrome://extensions/"), ("msedge.exe", "Edge", "edge://extensions/"), ("brave.exe", "Brave", "brave://extensions/") })
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
                if (hive.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\App Paths\{exe}")?.GetValue("") is string path && File.Exists(path))
                    return (path, name, page);
        return null;
    }
}
