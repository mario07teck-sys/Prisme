using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Prisme.Core;

/// <summary>Intégration Windows par utilisateur (sans droits admin) : menu clic droit et démarrage automatique.</summary>
public static class Shell
{
    const string Classes = @"Software\Classes";
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    static string Exe => Environment.ProcessPath;

    /// <summary>Vrai pour la copie installée, quel que soit le dossier choisi (l'installeur y laisse son désinstalleur), faux pour une build de dev.</summary>
    public static bool IsInstalledCopy => File.Exists(Path.Combine(Path.GetDirectoryName(Exe)!, "unins000.exe"));

    static IEnumerable<string> AllExts => Formats.AllExts;

    /// <summary>« Convertir avec Prisme ▸ » sur chaque type de fichier pris en charge.</summary>
    public static void RegisterContextMenu()
    {
        UnregisterContextMenu();
        foreach (var ext in AllExts)
        {
            var sub = $"Prisme.menu.{ext}";
            using (var k = Registry.CurrentUser.CreateSubKey($@"{Classes}\SystemFileAssociations\.{ext}\shell\Prisme"))
            {
                k.SetValue("MUIVerb", "Convertir avec Prisme");
                k.SetValue("Icon", $"\"{Exe}\",0");
                k.SetValue("ExtendedSubCommandsKey", sub);
            }
            int i = 0;
            foreach (var t in Formats.TargetsFor("x." + ext))
                AddItem(sub, $"{i++:00}{t.Id}", string.IsNullOrEmpty(t.Hint) ? t.Label : $"{t.Label}  ·  {t.Hint}", $"--to {t.Id}");
            AddItem(sub, "99open", "Ouvrir dans Prisme…", "--add", separatorBefore: true);
        }
        SaveStamp(true);
        Notify();
    }

    static void AddItem(string sub, string key, string label, string args, bool separatorBefore = false)
    {
        using var k = Registry.CurrentUser.CreateSubKey($@"{Classes}\{sub}\shell\{key}");
        k.SetValue("MUIVerb", label);
        k.SetValue("MultiSelectModel", "Player"); // pas de limite à 15 fichiers sélectionnés
        if (separatorBefore) k.SetValue("CommandFlags", 0x20, RegistryValueKind.DWord);
        using var c = k.CreateSubKey("command");
        c.SetValue("", $"\"{Exe}\" {args} \"%1\"");
    }

    public static void UnregisterContextMenu()
    {
        using var classes = Registry.CurrentUser.OpenSubKey(Classes, true);
        if (classes == null) return;
        foreach (var name in classes.GetSubKeyNames().Where(n => n.StartsWith("Prisme.menu.")))
            classes.DeleteSubKeyTree(name, false);
        // toutes les extensions, y compris celles qu'une ancienne version gérait et plus celle-ci
        using (var sfa = classes.OpenSubKey("SystemFileAssociations", true))
            if (sfa != null)
                foreach (var ext in sfa.GetSubKeyNames())
                {
                    using var verb = sfa.OpenSubKey($@"{ext}\shell\Prisme");
                    if (verb?.GetValue("ExtendedSubCommandsKey") is string s && s.StartsWith("Prisme.menu."))
                        sfa.DeleteSubKeyTree($@"{ext}\shell\Prisme", false);
                }
        SaveStamp(false);
        Notify();
    }

    /// <summary>Empreinte du menu : chemin de l'exe + catalogue des formats. Si elle change, on réenregistre.</summary>
    static string Stamp => Exe + "|" + string.Join(",", AllExts.Select(e => e + ":" + string.Join("+", Formats.TargetsFor("x." + e).Select(t => t.Id))));
    const string StampKey = @"Software\Prisme";

    public static bool ContextMenuRegistered()
    {
        using var k = Registry.CurrentUser.OpenSubKey(StampKey);
        return k?.GetValue("MenuStamp") as string == Stamp;
    }

    static void SaveStamp(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(StampKey);
        if (on) k.SetValue("MenuStamp", Stamp); else k.DeleteValue("MenuStamp", false);
    }

    public static void SetAutostart(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue("Prisme", $"\"{Exe}\" --tray"); else k.DeleteValue("Prisme", false);
    }

    public static bool Autostart()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue("Prisme") as string == $"\"{Exe}\" --tray"; // une entrée vers un ancien exe compte comme absente
    }

    [DllImport("shell32.dll")] static extern void SHChangeNotify(int e, uint f, IntPtr a, IntPtr b);
    static void Notify() => SHChangeNotify(0x08000000, 0, IntPtr.Zero, IntPtr.Zero); // SHCNE_ASSOCCHANGED
}
