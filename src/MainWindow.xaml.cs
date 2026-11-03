using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;
using Prisme.Core;

namespace Prisme;

public record FileItem(string Path, string Name, Geometry Icon)
{
    public bool Editable => Formats.KindOf(Path) is Kind.Audio or Kind.Video or Kind.Image;
    public string EditTip => Formats.KindOf(Path) == Kind.Image ? "Recadrer, redimensionner (onglet Retoucher)" : "Couper un extrait (onglet Retoucher)";
}

public partial class MainWindow : Window
{
    readonly ObservableCollection<FileItem> _files = new();
    Settings S => App.Settings;
    string _lastClip;
    bool _loading;

    public MainWindow()
    {
        InitializeComponent();
        FilesList.ItemsSource = _files;
        ConvList.ItemsSource = App.Conversions;
        DlList.ItemsSource = App.Downloads;
        _files.CollectionChanged += (_, _) => { if (!_batch) RefreshFiles(); };
        App.Conversions.CollectionChanged += (_, _) => ConvHeader.Visibility = Vis(App.Conversions.Count > 0);
        App.Downloads.CollectionChanged += (_, _) => DlHeader.Visibility = Vis(App.Downloads.Count > 0);

        var v = typeof(App).Assembly.GetName().Version;
        VersionText.Text = $"Version {v.Major}.{v.Minor}.{v.Build}";
        AboutText.Text = $"Prisme {v.Major}.{v.Minor}.{v.Build} · gratuit, sans pub · © 2026 Marius";
        LoadSettings();
        InitEditor();
        App.Current.UpdateChanged += () => Dispatcher.BeginInvoke(RefreshUpdate);
        RefreshUpdate();

        DragEnter += OnDragOver; DragOver += OnDragOver;
        DragLeave += (_, _) => DropOverlay.Visibility = Visibility.Collapsed;
        Drop += OnDrop;
        Activated += (_, _) => CheckClipboard();
        StateChanged += (_, _) =>
        {   // fenêtre sans bordure : on compense le débordement en plein écran
            Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            MaxIcon.Data = (Geometry)FindResource(WindowState == WindowState.Maximized ? "IcRestore" : "IcMax");
        };
        SourceInitialized += (_, _) =>
        {   // coins arrondis et ombre Windows 11
            var h = new WindowInteropHelper(this).Handle;
            int round = 2; DwmSetWindowAttribute(h, 33, ref round, 4);
            int dark = 1; DwmSetWindowAttribute(h, 20, ref dark, 4);
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && !UrlBox.IsKeyboardFocused) PasteAnything();
        };
    }

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
    static Visibility Vis(bool b) => b ? Visibility.Visible : Visibility.Collapsed;

    // ---------- navigation ----------

    public void GoTo(string page)
    {
        var rb = page switch { "download" => NavDownload, "settings" => NavSettings, "edit" => NavEdit, _ => NavConvert };
        if (rb.IsChecked == true) return;
        rb.IsChecked = true;
    }

    void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageConvert == null) return;
        var tag = (string)((FrameworkElement)sender).Tag;
        FrameworkElement show = tag switch { "download" => PageDownload, "settings" => PageSettings, "edit" => PageEdit, _ => PageConvert };
        foreach (var p in new FrameworkElement[] { PageConvert, PageEdit, PageDownload, PageSettings }) p.Visibility = Vis(p == show);
        if (show != PageEdit) EdPause();
        ((Storyboard)FindResource("PageIn")).Begin(show);
        if (show == PageSettings) RefreshEngineVersion();
        if (show == PageDownload) { CheckClipboard(); UrlBox.Focus(); }
    }

    // ---------- fichiers à convertir ----------

    public void AddFiles(IEnumerable<string> paths)
    {
        GoTo("convert");
        var known = new HashSet<string>(_files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        var add = Expand(paths).Where(p => Formats.KindOf(p) != Kind.Unknown && known.Add(p)).Take(MaxFiles - _files.Count).ToList();
        if (add.Count == 0) return;
        _batch = true;
        foreach (var p in add) _files.Add(new FileItem(p, System.IO.Path.GetFileName(p), IconFor(p)));
        _batch = false;
        RefreshFiles(); // une seule mise à jour de l'écran, même pour 2 000 fichiers
    }

    const int MaxFiles = 2000;
    bool _batch;

    static IEnumerable<string> Expand(IEnumerable<string> paths)
    {
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var p in paths)
        {
            if (File.Exists(p)) yield return p;
            else if (Directory.Exists(p))
                foreach (var f in Directory.EnumerateFiles(p, "*", opts)) yield return f;
        }
    }

    Geometry IconFor(string p) => (Geometry)FindResource(Formats.KindOf(p) switch
    {
        Kind.Audio => "IcAudio", Kind.Video => "IcVideo", Kind.Image or Kind.Drawing => "IcImage", Kind.Pdf => "IcPdf", _ => "IcDoc",
    });

    void RefreshFiles()
    {
        bool any = _files.Count > 0;
        DropEmpty.Visibility = Vis(!any); DropFilled.Visibility = Vis(any);
        TargetsPanel.Visibility = Vis(any);
        DropZone.Cursor = any ? Cursors.Arrow : Cursors.Hand;
        FilesCount.Text = _files.Count == 1 ? "1 fichier" : $"{_files.Count} fichiers";
        var groups = _files.GroupBy(f => Formats.KindOf(f.Path)).OrderBy(g => g.Key).ToList();
        GroupsList.ItemsSource = groups.Select(g =>
        {
            var paths = g.Select(f => f.Path).ToList();
            var label = g.Key switch
            {
                Kind.Audio => "SON", Kind.Video => "VIDÉO", Kind.Image => "IMAGE", Kind.Pdf => "PDF", Kind.Sheet => "TABLEUR",
                Kind.Slides => "PRÉSENTATION", Kind.Drawing => "DESSIN", Kind.Subtitle => "SOUS-TITRE", _ => "DOCUMENT",
            };
            var n = paths.Count > 1 ? $"{label}S ({paths.Count})" : label;
            var targets = Formats.TargetsFor(paths);
            if (g.Key == Kind.Audio && paths.Count > 1) targets.Add(new Target("assemble", "Assembler", "", "En un seul fichier, à la suite"));
            return new TargetGroup(groups.Count > 1 ? $"CONVERTIR {n} EN" : "CONVERTIR EN", paths, targets);
        }).ToList();
    }

    public record TargetGroup(string Header, List<string> Paths, List<Target> Targets);

    void DropZone_Click(object sender, RoutedEventArgs e)
    {
        if (sender == DropZone && _files.Count > 0) return; // clic dans la liste : on ne rouvre pas le sélecteur
        var dlg = new OpenFileDialog
        {
            Multiselect = true, Title = "Choisir les fichiers à convertir",
            Filter = "Fichiers pris en charge|" + string.Join(";", Formats.AllExts.Select(x => "*." + x)) + "|Tous les fichiers|*.*",
        };
        if (dlg.ShowDialog(this) == true) AddFiles(dlg.FileNames);
    }

    void RemoveFile_Click(object sender, RoutedEventArgs e) => _files.Remove((FileItem)((FrameworkElement)sender).DataContext);

    void EditFile_Click(object sender, RoutedEventArgs e) => OpenInEditor(((FileItem)((FrameworkElement)sender).DataContext).Path);
    void ClearFiles_Click(object sender, RoutedEventArgs e) => _files.Clear();

    void Target_Click(object sender, RoutedEventArgs e)
    {
        var t = (Target)((FrameworkElement)sender).DataContext;
        var g = (TargetGroup)((FrameworkElement)sender).Tag;
        if (t.Id == "assemble") OpenMix(g.Paths); // l'ordre se règle dans l'onglet Retoucher
        else App.Current.StartConversions(g.Paths, t.Id);
        var done = new HashSet<string>(g.Paths);
        _batch = true;
        foreach (var f in _files.Where(f => done.Contains(f.Path)).ToList()) _files.Remove(f); // les autres types restent proposés
        _batch = false;
        RefreshFiles();
    }

    // ---------- glisser-déposer ----------

    void OnDragOver(object sender, DragEventArgs e)
    {
        bool files = e.Data.GetDataPresent(DataFormats.FileDrop);
        bool text = !files && e.Data.GetDataPresent(DataFormats.UnicodeText) && Downloader.IsUrl((string)e.Data.GetData(DataFormats.UnicodeText));
        e.Effects = files || text ? DragDropEffects.Copy : DragDropEffects.None;
        DropOverlayText.Text = text ? "Lâche pour télécharger" : PageEdit.Visibility == Visibility.Visible ? "Lâche pour retoucher" : "Lâche pour ajouter";
        DropOverlay.Visibility = Vis(files || text);
        e.Handled = true;
    }

    void OnDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) { if (PageEdit.Visibility == Visibility.Visible) EdLoad(paths, append: true); else AddFiles(paths); }
        else if (e.Data.GetData(DataFormats.UnicodeText) is string s && Downloader.IsUrl(s)) ShowDownload(s.Trim());
    }

    void PasteAnything()
    {
        if (Clipboard.ContainsFileDropList())
        {
            var paths = Clipboard.GetFileDropList().Cast<string>().ToList();
            if (PageEdit.Visibility == Visibility.Visible) EdLoad(paths, append: true); else AddFiles(paths);
        }
        else if (Clipboard.ContainsText() && Downloader.IsUrl(Clipboard.GetText())) ShowDownload(Clipboard.GetText().Trim());
    }

    // ---------- téléchargement ----------

    public void ShowDownload(string url)
    {
        GoTo("download");
        if (url != null) { UrlBox.Text = url; UrlBox.CaretIndex = url.Length; }
        UrlBox.Focus();
    }

    void CheckClipboard()
    {
        if (!S.WatchClipboard || PageDownload == null) return;
        try
        {
            if (!Clipboard.ContainsText()) return;
            var t = Clipboard.GetText().Trim();
            if (t == _lastClip || !Downloader.IsVideoLink(t)) return;
            _lastClip = t;
            if (UrlBox.Text.Trim().Length == 0) { UrlBox.Text = t; if (_files.Count == 0 && App.Conversions.Count == 0) GoTo("download"); }
        }
        catch { /* presse-papiers occupé par une autre appli */ }
    }

    void Paste_Click(object sender, RoutedEventArgs e)
    {
        try { if (Clipboard.ContainsText()) { UrlBox.Text = Clipboard.GetText().Trim(); UrlBox.CaretIndex = UrlBox.Text.Length; } } catch { }
        UrlBox.Focus();
    }

    void UrlBox_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) Download_Click(sender, e); }

    void Download_Click(object sender, RoutedEventArgs e)
    {
        var url = UrlBox.Text.Trim();
        if (!Downloader.IsUrl(url))
        {
            UrlError.Text = url.Length == 0 ? "Colle d'abord un lien." : "Ce n'est pas un lien valide (il doit commencer par https://).";
            UrlError.Visibility = Visibility.Visible; return;
        }
        UrlError.Visibility = Visibility.Collapsed;
        var mode = DlBest.Parent is Panel p ? p.Children.OfType<RadioButton>().First(r => r.IsChecked == true).Tag as string : "best";
        App.Current.StartDownload(url, mode);
        _lastClip = url;
        UrlBox.Clear();
    }

    void ChangeDlDir_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "Dossier des téléchargements", InitialDirectory = S.DownloadDir };
        if (d.ShowDialog(this) != true) return;
        S.DownloadDir = d.FolderName; S.Save(); ShowDirs();
    }
    void ResetDlDir_Click(object sender, RoutedEventArgs e) { S.DownloadDir = Settings.DefaultDownloadDir(); S.Save(); ShowDirs(); }
    void OpenDlDir_Click(object sender, RoutedEventArgs e) => App.Reveal(S.DownloadDir);

    // ---------- file d'attente ----------

    void OpenJob_Click(object sender, RoutedEventArgs e) => App.Reveal(((Job)((FrameworkElement)sender).DataContext).Output);
    void CancelJob_Click(object sender, RoutedEventArgs e) => ((Job)((FrameworkElement)sender).DataContext).Cts.Cancel();
    void RemoveJob_Click(object sender, RoutedEventArgs e)
    {
        var j = (Job)((FrameworkElement)sender).DataContext;
        App.Conversions.Remove(j); App.Downloads.Remove(j);
    }
    void ClearDone_Click(object sender, RoutedEventArgs e)
    {
        var list = (string)((FrameworkElement)sender).Tag == "dl" ? App.Downloads : App.Conversions;
        foreach (var j in list.Where(j => !j.IsActive).ToList()) list.Remove(j);
    }

    // ---------- réglages ----------

    void LoadSettings()
    {
        _loading = true;
        SetAutostart.IsChecked = Shell.Autostart();
        SetTray.IsChecked = S.CloseToTray;
        SetMenu.IsChecked = S.ContextMenu;
        SetClip.IsChecked = S.WatchClipboard;
        SetNotify.IsChecked = S.Notify;
        SetAutoUpdate.IsChecked = S.AutoUpdate;
        OutNext.IsChecked = S.SaveNextToSource || !Directory.Exists(S.OutputDir);
        OutFixed.IsChecked = !OutNext.IsChecked;
        ShowDirs();
        _loading = false;
    }

    void ShowDirs()
    {
        DlDirText.Text = DlDirText2.Text = S.DownloadDir;
        OutDirText.Text = Directory.Exists(S.OutputDir) ? S.OutputDir : "Aucun dossier choisi";
        OutDirRow.Visibility = Vis(OutFixed.IsChecked == true);
    }

    void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        try
        {
            if (sender == SetAutostart) { S.StartWithWindows = SetAutostart.IsChecked == true; Shell.SetAutostart(S.StartWithWindows); }
            if (sender == SetMenu)
            {
                S.ContextMenu = SetMenu.IsChecked == true;
                if (S.ContextMenu) Shell.RegisterContextMenu(); else Shell.UnregisterContextMenu();
            }
            S.CloseToTray = SetTray.IsChecked == true;
            S.WatchClipboard = SetClip.IsChecked == true;
            S.Notify = SetNotify.IsChecked == true;
            S.AutoUpdate = SetAutoUpdate.IsChecked == true;
            S.SaveNextToSource = OutNext.IsChecked == true;
            S.Save();
            ShowDirs();
        }
        catch (Exception ex) { App.Log(ex.ToString()); MessageBox.Show(this, ex.Message, "Prisme", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    void OutFixed_Click(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(S.OutputDir)) ChangeOutDir_Click(sender, e);
        if (!Directory.Exists(S.OutputDir)) { OutNext.IsChecked = true; }
        Setting_Changed(sender, e);
    }

    void ChangeOutDir_Click(object sender, RoutedEventArgs e)
    {
        var d = new OpenFolderDialog { Title = "Dossier des fichiers convertis" };
        if (Directory.Exists(S.OutputDir)) d.InitialDirectory = S.OutputDir;
        if (d.ShowDialog(this) != true) return;
        S.OutputDir = d.FolderName; S.SaveNextToSource = false; OutFixed.IsChecked = true; S.Save(); ShowDirs();
    }

    async void RefreshEngineVersion()
    {
        var lines = new List<string>();
        try { await Engines.Run(Engines.YtDlp, new[] { "--version" }, l => lines.Add(l), null, CancellationToken.None); } catch { }
        YtVersion.Text = lines.Count > 0 ? $"Version {lines[0]} · mise à jour automatique chaque jour" : "Introuvable : réinstalle Prisme";
    }

    async void UpdateEngine_Click(object sender, RoutedEventArgs e)
    {
        UpdateBtn.IsEnabled = false; UpdateBtn.Content = "Mise à jour…";
        S.LastEngineUpdate = default;
        await Downloader.UpdateEngine(S);
        RefreshEngineVersion();
        UpdateBtn.Content = "Mettre à jour"; UpdateBtn.IsEnabled = true;
    }

    // ---------- mises à jour ----------

    void RefreshUpdate()
    {
        var app = App.Current;
        UpdateCard.Visibility = Vis(app.UpdateReady);
        UpdateCardTitle.Text = $"Prisme {app.UpdateVersion} est prête";
        UpdateCheckBtn.Content = app.UpdateReady ? "Installer maintenant" : "Vérifier maintenant";
        UpdateStatusText.Text = !Shell.IsInstalledCopy ? "Copie de développement : les mises à jour ne concernent que la version installée."
            : app.UpdateStatus.Length > 0 ? app.UpdateStatus : $"Version {Updater.Current} · prochaine vérification dans quelques secondes.";
    }

    void UpdateNow_Click(object sender, RoutedEventArgs e) => App.Current.InstallUpdateNow();

    async void UpdateCheck_Click(object sender, RoutedEventArgs e)
    {
        if (!Shell.IsInstalledCopy) return;
        if (App.Current.UpdateReady) { App.Current.InstallUpdateNow(); return; }
        UpdateCheckBtn.IsEnabled = false;
        await App.Current.CheckUpdatesNow();
        UpdateCheckBtn.IsEnabled = true;
    }

    // ---------- fenêtre ----------

    void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void Max_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        EdPause();
        if (App.Current.Exiting) return;
        e.Cancel = true;
        if (S.CloseToTray) App.Current.HideToTray(); else App.Current.Quit();
    }
}

/// <summary>Booléen → visibilité ; paramètre « not » pour inverser.</summary>
public class BoolVis : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c) => (v is true) ^ (p as string == "not") ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
