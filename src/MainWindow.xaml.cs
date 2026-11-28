using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Prisme.Core;

namespace Prisme;

public record FileItem(string Path, string Name, Geometry Icon)
{
    public bool Editable => Formats.KindOf(Path) is Kind.Audio or Kind.Video or Kind.Image;
    public string EditTip => Formats.KindOf(Path) == Kind.Image ? "Tourner, recadrer, redimensionner (onglet Découpe)" : "Couper un extrait (onglet Découpe)";
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
        IsVisibleChanged += (_, _) => { if (IsVisible) OfferExtension(); };
        ConvList.ItemsSource = App.Conversions;
        DlList.ItemsSource = App.Downloads;
        _files.CollectionChanged += (_, _) => { if (!_batch) RefreshFiles(); };
        App.Conversions.CollectionChanged += (_, _) => ConvHeader.Visibility = Vis(App.Conversions.Count > 0);
        App.Downloads.CollectionChanged += (_, _) => DlHeader.Visibility = Vis(App.Downloads.Count > 0);

        var v = typeof(App).Assembly.GetName().Version;
        VersionText.Text = $"Version {v.Major}.{v.Minor}.{v.Build}";
        AboutText.Text = $"Prisme {v.Major}.{v.Minor}.{v.Build} · gratuit, sans pub · © 2026 Mario Teck";
        LoadSettings();
        InitEditor();
        InitSide();
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
        if (show == PageDownload) { CheckClipboard(); UrlBox.Focus(); RefreshExtension(); }
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
            // plusieurs sons : les mettre bout à bout, dans l'ordre où ils ont été ajoutés
            if (g.Key == Kind.Audio && paths.Count > 1) targets.AddRange(Formats.MergeTargets.Select(t => t with { Family = "ASSEMBLER À LA SUITE" }));
            return new TargetGroup(groups.Count > 1 ? $"CONVERTIR {n} EN" : "CONVERTIR EN", paths, Formats.Tabs(g.Key, targets));
        }).ToList();
    }

    /// <summary>Un bloc de formats à l'écran : ses onglets (familles) et celui qui est ouvert.</summary>
    public class TargetGroup : INotifyPropertyChanged
    {
        public TargetGroup(string header, List<string> paths, List<(string Name, List<Target> Targets)> tabs, bool edit = false)
        {
            Header = header; Paths = paths; Edit = edit;
            Families = tabs.Select((t, i) => new TargetFamily(t.Name, t.Targets, i == 0)).ToList();
            _selected = Families.FirstOrDefault();
        }
        public string Header { get; }
        public List<string> Paths { get; }
        public List<TargetFamily> Families { get; }
        public bool Edit { get; }                                   // bloc « ENREGISTRER EN » de l'onglet Découpe
        public bool HasTabs => Families.Count > 1;
        public string Id { get; } = Guid.NewGuid().ToString("N");   // les onglets d'un bloc s'excluent entre eux, pas ceux du voisin
        TargetFamily _selected;
        public TargetFamily Selected { get => _selected; set { _selected = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
        public event PropertyChangedEventHandler PropertyChanged;
    }
    public record TargetFamily(string Name, List<Target> Targets, bool First = false);

    // ---------- menu de gauche : entier, ou réduit aux icônes et déroulé au survol ----------

    const double SideWide = 232, SideRail = 82;
    bool _sideOpen = true, _sideClosing, _sideMute; // _sideMute : on vient de le réduire, il attend que la souris en sorte pour se redérouler
    readonly System.Windows.Threading.DispatcherTimer _sideTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };

    /// <summary>Le menu réduit est déroulé par-dessus la page (la vidéo, fenêtre à part, passerait devant lui).</summary>
    bool SideCovers => S.NavCollapsed && (_sideOpen || _sideClosing);

    void InitSide()
    {
        // petit délai : frôler le bord ne déroule pas le menu, et il ne se referme pas dès que la souris dépasse d'un cheveu
        _sideTimer.Tick += (_, _) =>
        {
            _sideTimer.Stop();
            bool over = Side.IsMouseOver;
            if (!over) _sideMute = false;
            if (S.NavCollapsed) SideUnroll(over && !_sideMute);
        };
        ApplySide(animate: false);
    }

    void ApplySide(bool animate)
    {
        SideCol.Width = new GridLength(S.NavCollapsed ? SideRail : SideWide);
        SidePinIcon.Data = (Geometry)FindResource(S.NavCollapsed ? "IcMenuOut" : "IcMenuIn");
        SidePin.ToolTip = S.NavCollapsed ? "Garder le menu ouvert" : "Réduire le menu";
        SideUnroll(!S.NavCollapsed, animate);
        FitVideo();
    }

    void SideUnroll(bool open, bool animate = true)
    {
        if (!animate)
        {
            _sideOpen = open; _sideClosing = false;
            Side.BeginAnimation(WidthProperty, null); Side.Width = open ? SideWide : SideRail;
            SideLabels(open);
            return;
        }
        if (open == _sideOpen) return;
        _sideOpen = open; _sideClosing = !open;
        if (open) SideLabels(true);
        var a = new DoubleAnimation(open ? SideWide : SideRail, TimeSpan.FromMilliseconds(170)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        if (!open) a.Completed += (_, _) => { if (_sideOpen) return; _sideClosing = false; SideLabels(false); FitVideo(); };
        Side.BeginAnimation(WidthProperty, a);
        FitVideo();
    }

    /// <summary>Réduit : seuls le logo et les icônes restent (les textes dépasseraient de la bande).</summary>
    void SideLabels(bool show)
    {
        SideInner.Width = (show ? SideWide : SideRail) - 33;
        foreach (var e in new UIElement[] { SideTitle, SidePin, SideBottom }
                     .Concat(new[] { NavConvert, NavEdit, NavDownload, NavSettings }.Select(n => ((Panel)n.Content).Children[1])))
            e.Visibility = Vis(show);
    }

    void Side_Hover(object sender, MouseEventArgs e) { if (S.NavCollapsed) { _sideTimer.Stop(); _sideTimer.Start(); } }

    void SidePin_Click(object sender, RoutedEventArgs e)
    {
        S.NavCollapsed = !S.NavCollapsed; _sideMute = S.NavCollapsed;
        try { S.Save(); } catch (Exception ex) { App.Log(ex.ToString()); }
        ApplySide(animate: true);
    }

    void Family_Checked(object sender, RoutedEventArgs e)
    {
        var rb = (FrameworkElement)sender;
        if (rb.Tag is TargetGroup g && rb.DataContext is TargetFamily f) g.Selected = f;
    }

    /// <summary>Pour vérifier l'écran sans le voir : « Prisme.exe --capture fichier.png [bas] » dessine la fenêtre dans
    /// une image. « bas » : pages défilées jusqu'en bas.</summary>
    public void Capture(string path, bool bottom)
    {
        if (bottom) foreach (var sv in Descendants<ScrollViewer>(this)) sv.ScrollToEnd();
        Dispatcher.BeginInvoke(() =>
        {
            var root = (FrameworkElement)Content; var dpi = VisualTreeHelper.GetDpi(this);
            var bmp = new RenderTargetBitmap((int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
                96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
            var v = new DrawingVisual();   // le fond est celui de la fenêtre, pas du contenu : on le peint dessous
            using (var dc = v.RenderOpen())
            {
                var r = new Rect(0, 0, root.ActualWidth, root.ActualHeight);
                dc.DrawRectangle(Background, null, r); dc.DrawRectangle(new VisualBrush(root) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = r, Stretch = Stretch.None }, null, r);
            }
            bmp.Render(v);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bmp));
            using var f = File.Create(path); png.Save(f);
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    static IEnumerable<T> Descendants<T>(DependencyObject o) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(o); i++)
        {
            var c = VisualTreeHelper.GetChild(o, i);
            if (c is T t) yield return t;
            foreach (var d in Descendants<T>(c)) yield return d;
        }
    }

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
        if (g.Edit) { EdSave(t); return; }
        App.Current.StartConversions(g.Paths, t.Id);
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
        DropOverlayText.Text = text ? "Lâche pour télécharger" : PageEdit.Visibility == Visibility.Visible ? "Lâche pour découper" : "Lâche pour ajouter";
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
    void RetryJob_Click(object sender, RoutedEventArgs e) => App.Current.Retry((Job)((FrameworkElement)sender).DataContext);
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
        SetSubs.IsChecked = S.DownloadSubs;
        SetGpu.IsChecked = S.Gpu;
        foreach (var rb in GifFps.Children.OfType<RadioButton>()) rb.IsChecked = (string)rb.Tag == S.GifFps.ToString();
        foreach (var rb in GifQuality.Children.OfType<RadioButton>()) rb.IsChecked = (string)rb.Tag == S.GifQuality;
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
            S.DownloadSubs = SetSubs.IsChecked == true;
            S.Gpu = SetGpu.IsChecked == true;
            S.SaveNextToSource = OutNext.IsChecked == true;
            S.Save();
            ShowDirs();
        }
        catch (Exception ex) { App.Log(ex.ToString()); MessageBox.Show(this, ex.Message, "Prisme", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    void Gif_Click(object sender, RoutedEventArgs e)
    {
        var tag = (string)((FrameworkElement)sender).Tag;
        if (int.TryParse(tag, out var fps)) S.GifFps = fps; else S.GifQuality = tag;
        try { S.Save(); } catch (Exception ex) { App.Log(ex.ToString()); }
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

    // ---------- extension du navigateur (clic droit sur une vidéo → Prisme) ----------

    readonly System.Windows.Threading.DispatcherTimer _extTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    bool _extDone;

    async void RefreshExtension()
    {
        bool ok = await Task.Run(Browser.ExtensionInstalled);
        ExtCardIcon.Data = (Geometry)FindResource(ok ? "IcCheck" : "IcBrowser");
        ExtTitle.Text = ok ? "Clic droit dans le navigateur : prêt" : "Clic droit dans Chrome";
        ExtText.Text = ok
            ? "Clic droit sur une vidéo → « Télécharger avec Prisme ». Elle arrive dans le dossier ci-dessus ; les autres téléchargements du navigateur ne changent pas."
            : "Avec l'extension Prisme (Chrome, Edge ou Brave) : clic droit sur une vidéo → « Télécharger avec Prisme ».";
        ExtBtnText.Text = ok ? "Revoir comment faire" : "Ajouter au navigateur";
    }

    /// <summary>Une seule fois, à l'ouverture : propose l'extension si un navigateur compatible est là et qu'elle n'y est pas.</summary>
    async void OfferExtension()
    {
        if (S.ExtOffered || !Shell.IsInstalledCopy || Browser.Find() == null) return;
        S.ExtOffered = true; S.Save();
        if (await Task.Run(Browser.ExtensionInstalled)) return;
        await Task.Delay(800); // laisse la fenêtre s'afficher
        GoTo("download");
        ExtIcon.Data = (Geometry)FindResource("IcBrowser");
        ExtOvTitle.Text = "Nouveau : télécharger depuis Chrome";
        ExtOvText.Text = "Ajoute Prisme à ton navigateur : clic droit sur une vidéo → « Télécharger avec Prisme », elle arrive dans " + S.DownloadDir + ". Deux gestes, une seule fois. Tu pourras aussi le faire plus tard depuis la page Télécharger.";
        ExtSteps.Visibility = ExtWait.Visibility = ExtButtons.Visibility = Visibility.Collapsed;
        ExtOffer.Visibility = Visibility.Visible;
        ExtOverlay.Visibility = Visibility.Visible;
        ExtOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.18)));
        ((Storyboard)FindResource("PageIn")).Begin(ExtCard);
    }

    void AddExtension_Click(object sender, RoutedEventArgs e)
    {
        ExtOffer.Visibility = Visibility.Collapsed;
        var b = Browser.Find();
        ExtIcon.Data = (Geometry)FindResource("IcBrowser");
        ExtSteps.Visibility = ExtWait.Visibility = ExtButtons.Visibility = Vis(b != null);
        if (b == null)
        {
            ExtOvTitle.Text = "Aucun navigateur compatible trouvé";
            ExtOvText.Text = "L'extension marche avec Google Chrome, Microsoft Edge et Brave. Installe l'un d'eux, puis reviens ici.";
            ExtButtons.Visibility = Visibility.Visible;
        }
        else
        {
            ExtOvTitle.Text = $"Ajouter Prisme à {b.Value.Name}";
            ExtOvText.Text = $"Prisme vient d'ouvrir {b.Value.Name} et le dossier de l'extension. Deux gestes, une seule fois :";
            if (!Browser.ExtensionInstalled()) OpenExtensionPlaces(); // déjà ajoutée : on montre seulement les étapes
        }
        _extDone = false;
        ExtOverlay.Visibility = Visibility.Visible;
        ExtOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.18)));
        ((Storyboard)FindResource("PageIn")).Begin(ExtCard);
        if (b == null) return;
        _extTimer.Tick -= ExtTick; _extTimer.Tick += ExtTick;
        _extTimer.Start();
    }

    static async void OpenExtensionPlaces()
    {
        var b = Browser.Find();
        if (b == null) return;
        try
        {
            // le navigateur refuse d'ouvrir sa page chrome://extensions à la demande d'un programme : on copie l'adresse, à coller
            Clipboard.SetText(b.Value.Page);
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{Browser.ExtensionDir}\"");
            await Task.Delay(2500); // l'Explorateur met 1 à 2 s à paraître : le navigateur, ouvert après, reste devant et a le clavier
            System.Diagnostics.Process.Start(b.Value.Exe, "--new-window");
        }
        catch (Exception ex) { App.Log("extension : " + ex); }
    }

    void ExtReopen_Click(object sender, RoutedEventArgs e) => OpenExtensionPlaces();

    async void ExtTick(object sender, EventArgs e)
    {
        if (_extDone || !await Task.Run(Browser.ExtensionInstalled) || _extDone || ExtOverlay.Visibility != Visibility.Visible) return;
        _extDone = true; _extTimer.Stop();
        ExtIcon.Data = (Geometry)FindResource("IcCheck");
        var pop = new DoubleAnimation(0.3, 1, TimeSpan.FromSeconds(0.45)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 } };
        ExtIcon.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        ExtIcon.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        ExtOvTitle.Text = "L'extension est ajoutée";
        ExtOvText.Text = "Clic droit sur une vidéo → « Télécharger avec Prisme ». Les vidéos arrivent dans " + S.DownloadDir + ".";
        ExtSteps.Visibility = ExtWait.Visibility = Visibility.Collapsed;
        RefreshExtension();
        await Task.Delay(2600);
        ExtClose_Click(null, null);
    }

    void ExtClose_Click(object sender, RoutedEventArgs e)
    {
        _extTimer.Stop();
        if (ExtOverlay.Visibility != Visibility.Visible) return;
        var fade = new DoubleAnimation(0, TimeSpan.FromSeconds(0.18));
        fade.Completed += (_, _) => ExtOverlay.Visibility = Visibility.Collapsed;
        ExtOverlay.BeginAnimation(OpacityProperty, fade);
    }

    // ---------- LibreOffice à télécharger (premier document qui en a besoin) ----------

    Job _officeJob;
    TaskCompletionSource<bool> _officeAsk;

    /// <summary>Propose de télécharger LibreOffice ; true = accepté (la fenêtre montre ensuite l'installation).</summary>
    public Task<bool> AskOffice(Job job, long size)
    {
        _officeJob = job;
        _officeAsk = new TaskCompletionSource<bool>();
        OfficeOverlay.DataContext = job;
        OfficeIcon.Data = (Geometry)FindResource("IcDoc");
        OfficeTitle.Text = "Télécharger LibreOffice ?";
        OfficeText.Text = $"Pour convertir ce document, Prisme a besoin de LibreOffice (≈ {size / 1048576.0:0} Mo à télécharger, une seule fois). "
                        + "Prisme l'installe tout seul, puis la conversion reprend.";
        OfficeProgress.Visibility = Visibility.Collapsed;
        OfficeRunButtons.Visibility = Visibility.Collapsed;
        OfficeAskButtons.Visibility = Visibility.Visible;
        // tâche annulée depuis la liste : la question ou l'installation disparaît avec elle
        job.Cts.Token.Register(() => Dispatcher.BeginInvoke(() => { if (_officeJob != job) return; _officeAsk.TrySetResult(false); HideOffice(); }));
        ShowOffice();
        OfficeDownloadBtn.Focus();
        return _officeAsk.Task;
    }

    void OfficeDownload_Click(object sender, RoutedEventArgs e)
    {
        if (_officeAsk?.TrySetResult(true) != true) return; // double clic, ou clic pendant le fondu après « Annuler »
        OfficeTitle.Text = "Installation de LibreOffice";
        OfficeText.Text = "Tu peux continuer à utiliser Prisme : la conversion reprendra toute seule.";
        OfficeAskButtons.Visibility = Visibility.Collapsed;
        OfficeProgress.Visibility = OfficeRunButtons.Visibility = Visibility.Visible;
        OfficeProgress.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.25)));
    }

    void OfficeCancel_Click(object sender, RoutedEventArgs e)
    {
        if (_officeAsk?.TrySetResult(false) == true) HideOffice(); // à la question : la conversion échoue avec un message
        else _officeJob?.Cts.Cancel();                             // pendant l'installation : tout ce qui a été extrait est effacé
    }

    void OfficeHide_Click(object sender, RoutedEventArgs e) => HideOffice(); // la tâche garde sa progression dans la liste

    /// <summary>Installation finie : coche animée puis fermeture ; ratée : la tâche affiche l'erreur.</summary>
    public async void OfficeFinished(Job job, Exception ex)
    {
        if (_officeJob != job || OfficeOverlay.Visibility != Visibility.Visible || ex != null) { if (_officeJob == job) HideOffice(); return; }
        OfficeIcon.Data = (Geometry)FindResource("IcCheck");
        var pop = new DoubleAnimation(0.3, 1, TimeSpan.FromSeconds(0.45)) { EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 } };
        OfficeIcon.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
        OfficeIcon.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
        OfficeTitle.Text = "LibreOffice est prêt";
        OfficeText.Text = "La conversion reprend.";
        OfficeProgress.Visibility = OfficeRunButtons.Visibility = Visibility.Collapsed;
        await Task.Delay(1400);
        if (_officeJob == job) HideOffice();
    }

    void ShowOffice()
    {
        OfficeOverlay.Visibility = Visibility.Visible;
        OfficeOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromSeconds(0.18)));
        ((Storyboard)FindResource("PageIn")).Begin(OfficeCard);
    }

    void HideOffice()
    {
        if (OfficeOverlay.Visibility != Visibility.Visible) return;
        _officeJob = null; _officeAsk = null;
        var fade = new DoubleAnimation(0, TimeSpan.FromSeconds(0.18));
        fade.Completed += (_, _) => { if (_officeJob == null) OfficeOverlay.Visibility = Visibility.Collapsed; };
        OfficeOverlay.BeginAnimation(OpacityProperty, fade);
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
        // Jamais Hide() ni Close() pendant la fermeture : WPF lève une exception, et Prisme restait vivant,
        // sans fenêtre ni icône près de l'horloge, sourd à tout nouveau lancement. On agit juste après.
        Dispatcher.BeginInvoke(() => { if (S.CloseToTray) App.Current.HideToTray(); else App.Current.Quit(); });
    }
}

/// <summary>Booléen → visibilité ; paramètre « not » pour inverser.</summary>
public class BoolVis : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c) => (v is true) ^ (p as string == "not") ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object v, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
