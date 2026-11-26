using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using Microsoft.Win32;
using Prisme.Core;
using VlcPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace Prisme;

public record MixItem(string Path, string Name, string Length, string Number);

/// <summary>Onglet Retoucher : extrait d'un son ou d'une vidéo (aperçu VLC), recadrage et taille d'une image,
/// assemblage de plusieurs sons. Le travail lui-même passe par la file de conversion habituelle.</summary>
public partial class MainWindow
{
    string _edPath;
    double _edDur, _edA, _edB, _edPos, _edPending = -1; // secondes ; _edPending = saut à faire dès que VLC est prêt
    int _edSkip, _edRetry; double _edTarget;
    bool _edSel, _edArm;  // _edSel : « Écouter l'extrait » s'arrête à la fin ; _edArm : pause dès la première image
    int _edDrag;          // frise : 1 début, 2 fin, 3 tête de lecture
    readonly DispatcherTimer _edTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    static LibVLC _vlc;
    VlcPlayer _mp;

    // VLC fige la fenêtre s'il travaille sur le fil de l'interface : son démarrage (des dizaines de modules, plusieurs
    // secondes la première fois) et chaque Stop/changement de vidéo (arrêt synchrone de la précédente). Il démarre donc
    // en arrière-plan dès l'ouverture de Prisme, et ces appels passent par une file à part, exécutée dans l'ordre.
    static Task<LibVLC> _vlcInit;
    Task _vlcQueue = Task.CompletedTask;

    /// <summary>Lance la préparation de VLC sans attendre (appelé au démarrage, quand Prisme est au calme).</summary>
    public static void PrepareVlc() => _vlcInit ??= Task.Run(() =>
    {
        LibVLCSharp.Shared.Core.Initialize();
        return new LibVLC("--quiet", "--no-video-title-show");
    });

    /// <summary>Exécute un appel bloquant de VLC hors du fil de l'interface, après les précédents.</summary>
    Task Vlc(Action a)
    {
        _vlcQueue = _vlcQueue.ContinueWith(_ => { try { a(); } catch (Exception e) { App.Log("VLC : " + e); } }, TaskScheduler.Default);
        return _vlcQueue;
    }

    int _imW, _imH;                 // image d'origine, en pixels
    Rect _crop = new(0, 0, 1, 1);   // en fractions de l'image
    Rect _fit;                      // où l'image est dessinée à l'écran
    double? _ratio;                 // largeur / hauteur en pixels, null = libre
    double _sx = 1, _sy = 1;        // taille de sortie / taille recadrée
    int _cropMode; Point _from; Rect _cropFrom; bool _sizeSync;

    readonly List<string> _mixPaths = new();
    readonly Dictionary<string, double> _mixDur = new(StringComparer.OrdinalIgnoreCase);

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)] static extern int StrCmpLogicalW(string a, string b);

    void InitEditor()
    {
        EdList.ItemsSource = App.Conversions;
        App.Conversions.CollectionChanged += (_, _) => EdHeader.Visibility = Vis(App.Conversions.Count > 0);
        _edTimer.Tick += EdTick;
        MixTargets.ItemsSource = Formats.MergeTargets;
        // raccourcis habituels d'un logiciel de montage : Espace = lecture, I / O = début / fin ici, flèches = 1 s (5 s avec Maj)
        PreviewKeyDown += (_, e) =>
        {
            if (PageEdit.Visibility != Visibility.Visible || EdMedia.Visibility != Visibility.Visible || EdPanel.Visibility != Visibility.Visible
                || Keyboard.FocusedElement is TextBox || Keyboard.Modifiers is not (ModifierKeys.None or ModifierKeys.Shift)) return;
            double pas = Keyboard.Modifiers == ModifierKeys.Shift ? 5 : 1;
            switch (e.Key)
            {
                case Key.Space: EdPlay_Click(null, null); break;
                case Key.I: EdSetStart_Click(null, null); break;
                case Key.O: EdSetEnd_Click(null, null); break;
                case Key.Left: if (_edDur > 0) Seek(_edPos - pas); break;
                case Key.Right: if (_edDur > 0) Seek(_edPos + pas); break;
                default: return;
            }
            e.Handled = true;
        };
    }

    /// <summary>Ouvre un fichier (ou plusieurs musiques à assembler) dans l'onglet Retoucher.</summary>
    public void OpenInEditor(string path) => EditFiles(new[] { path });
    public void EditFiles(IEnumerable<string> paths) { GoTo("edit"); EdLoad(paths); }
    void OpenMix(IEnumerable<string> paths) { GoTo("edit"); EdLoad(paths); }
    void EdPause() { if (_mp != null && _mp.IsPlaying) _mp.SetPause(true); }

    /// <param name="append">ajouter à l'assemblage ouvert (« + Ajouter », glisser-déposer) plutôt que de tout remplacer</param>
    void EdLoad(IEnumerable<string> paths, bool append = false)
    {
        var files = Expand(paths).Where(p => Formats.KindOf(p) != Kind.Unknown).ToList();
        var audio = files.Where(p => Formats.KindOf(p) == Kind.Audio).ToList();
        EdDropError.Visibility = Visibility.Collapsed;
        bool mixing = MixPanel.Visibility == Visibility.Visible;
        append &= mixing;
        if (audio.Count >= 2 || (append && audio.Count > 0 && audio.Count == files.Count))
        {
            if (!append) _mixPaths.Clear();
            foreach (var p in audio.OrderBy(p => System.IO.Path.GetFileName(p), Comparer<string>.Create(StrCmpLogicalW)))
                if (!_mixPaths.Contains(p, StringComparer.OrdinalIgnoreCase)) _mixPaths.Add(p);
            ShowEditMode(mix: true);
            RefreshMix();
            return;
        }
        var one = files.FirstOrDefault(p => Formats.KindOf(p) is Kind.Audio or Kind.Video or Kind.Image);
        if (one == null)
        {
            if (EdPanel.Visibility != Visibility.Visible && !mixing)
            {
                EdDropError.Text = "Ici, on retouche les musiques, les vidéos et les images. Les autres fichiers se convertissent dans « Convertir ».";
                EdDropError.Visibility = Visibility.Visible;
            }
            return;
        }
        LoadSingle(one);
    }

    void ShowEditMode(bool? mix)
    {   // null : rien de chargé ; false : un fichier ; true : assemblage
        EdDrop.Visibility = Vis(mix == null);
        EdPanel.Visibility = Vis(mix == false);
        MixPanel.Visibility = Vis(mix == true);
        if (mix != false) { EdPause(); _edTimer.Stop(); }
    }

    void EdBrowse_Click(object sender, RoutedEventArgs e)
    {
        var exts = Formats.AudioExts.Concat(Formats.VideoExts).Concat(Formats.ImageExts).Select(x => "*." + x);
        var dlg = new OpenFileDialog
        {
            Multiselect = true, Title = "Choisir un fichier à retoucher (ou plusieurs musiques à assembler)",
            Filter = "Musiques, vidéos, images|" + string.Join(";", exts) + "|Tous les fichiers|*.*",
        };
        if (dlg.ShowDialog(this) == true) EdLoad(dlg.FileNames, append: true); // « + Ajouter » n'existe que dans l'assemblage
    }

    // ---------- un fichier ----------

    void EdClose_Click(object sender, RoutedEventArgs e)
    {
        _edPath = null; // les chargements encore en cours voient qu'un autre « fichier » a été choisi et s'arrêtent
        ShowEditMode(mix: null);
        if (_mp != null) { var mp = _mp; _ = Vlc(() => { mp.Stop(); mp.Media = null; }); } // VLC lâche le fichier : il peut être déplacé ou supprimé
        EdPic.Source = null; _imW = _imH = 0;
        EdError.Visibility = Visibility.Collapsed;
    }

    async void LoadSingle(string path)
    {
        EdPause(); _edTimer.Stop();
        _edPath = path;
        var kind = Formats.KindOf(path);
        ShowEditMode(mix: false);
        EdName.Text = System.IO.Path.GetFileName(path); EdName.ToolTip = path;
        EdIcon.Data = IconFor(path);
        EdInfo.Text = ""; EdError.Visibility = Visibility.Collapsed;
        EdMedia.Visibility = Vis(kind != Kind.Image);
        EdImage.Visibility = Vis(kind == Kind.Image);
        EdGif.Visibility = Vis(kind == Kind.Video);
        EdGripImg.Visibility = Vis(kind == Kind.Image);
        foreach (var rb in EdGifFps.Children.OfType<RadioButton>()) rb.IsChecked = (string)rb.Tag == S.GifFps.ToString();
        foreach (var rb in EdGifQuality.Children.OfType<RadioButton>()) rb.IsChecked = (string)rb.Tag == S.GifQuality;
        var same = Formats.Find(path, "same");
        bool keeps = Formats.TargetsFor(path).All(t => t.Id != same.Id); // pas proposé = c'est le format d'origine
        // « Même format » en tête du premier onglet (le plus naturel après une retouche)
        var tabs = Formats.Tabs(kind, Formats.TargetsFor(path).Where(t => t.Id != "pdf-merge").ToList());
        tabs[0] = (tabs[0].Name, tabs[0].Targets.Prepend(Formats.Same(keeps ? $"Reste en {same.Label}" : $"En {same.Label}")).ToList());
        EdTargets.Content = new TargetGroup("ENREGISTRER EN", new List<string> { path }, tabs, edit: true);
        _ = Dispatcher.InvokeAsync(EdResize, DispatcherPriority.Loaded); // une fois la page mesurée : l'aperçu prend la place libre
        if (kind == Kind.Image) await LoadImage(path); else await LoadMedia(path, kind == Kind.Video);
    }

    int _edRot; double _edVol = 1, _edSpeed = 1;

    /// <summary>Rotation, volume, vitesse (étiquette « rot:90 », « vol:1.5 », « spd:2 »). L'aperçu suit le volume et la vitesse, pas la rotation.</summary>
    void EdFx_Click(object sender, RoutedEventArgs e)
    {
        var tag = ((string)((FrameworkElement)sender).Tag).Split(':');
        var v = double.Parse(tag[1], System.Globalization.CultureInfo.InvariantCulture);
        if (tag[0] == "rot") _edRot = (int)v; else if (tag[0] == "vol") _edVol = v; else _edSpeed = v;
        if (_mp != null) { _mp.Volume = (int)(_edVol * 100); _mp.SetRate((float)_edSpeed); }
        EdError.Visibility = Visibility.Collapsed;
    }

    async Task LoadMedia(string path, bool video)
    {
        _edDur = _edA = _edB = _edPos = 0; _edPending = -1; _edSel = false;
        _edRot = 0; _edVol = _edSpeed = 1;
        if (_mp != null) { _mp.Volume = 100; _mp.SetRate(1); }
        EdRot.Visibility = Vis(video);
        foreach (var rb in new[] { EdRot, EdVol, EdSpd }.SelectMany(p => p.Children.OfType<RadioButton>()))
            rb.IsChecked = (string)rb.Tag is "rot:0" or "vol:1" or "spd:1";
        _edVideo = video; _edReady = false; _stripGen++;
        EdStrip.Children.Clear(); EdWave.Source = null;
        EdScreen.Height = !video ? 150 : S.PreviewHeight > 0 ? Math.Clamp(S.PreviewHeight, 160, 2000) : Math.Max(290, EdScreen.ActualHeight);
        EdGrip.IsHitTestVisible = video; EdGrip.Child.Visibility = Vis(video);
        // la vidéo VLC est une fenêtre à part, rien ne se dessine par-dessus : on la réduit à 1 pixel tant que l'anneau tourne
        // (masquée, elle perdrait sa fenêtre et VLC ouvrirait la sienne)
        ShrinkVideo(true); EdAudioArt.Visibility = Visibility.Collapsed;
        EdLoading.Visibility = Visibility.Visible;
        EdNoPreview.Visibility = Visibility.Collapsed;
        UpdateTimeline(); UpdateTimes();
        var d = await Converter.Duration(path, CancellationToken.None);
        if (path != _edPath) return; // un autre fichier a été choisi entre-temps
        _edDur = d; _edB = d;
        _ = LoadStrip(path, d, video);
        EdInfo.Text = d > 0 ? $"durée {Edit.Time(d)}" : "";
        if (d <= 0) { EdError.Text = "Durée illisible : ce fichier ne peut pas être coupé."; EdError.Visibility = Visibility.Visible; }
        UpdateTimeline(); UpdateTimes();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded); // l'onglet vient d'apparaître : sa vue vidéo doit exister
        if (path != _edPath) return;
        if (!await EnsureVlc()) { PreviewReady(false); return; }
        if (path != _edPath) return;
        var player = _mp; var lib = _vlc;
        _edArm = true; // on lance puis on met en pause tout de suite : la première image s'affiche
        await Vlc(() => { using (var media = new Media(lib, new Uri(path))) player.Media = media; player.Mute = true; player.Play(); });
        if (path != _edPath) return;
        _edTimer.Start();
    }

    bool _edVideo, _edReady; uint _vidW, _vidH;   // _edReady : l'aperçu a sa première image ; taille de la vidéo (0 = inconnue)
    void PreviewReady(bool ok)
    {
        EdLoading.Visibility = Visibility.Collapsed;
        _edReady = ok; _vidW = _vidH = 0;
        if (ok && _edVideo && !(_mp.Size(0, ref _vidW, ref _vidH) && _vidW > 0 && _vidH > 0)) _vidW = _vidH = 0;
        FitVideo();
        EdAudioArt.Visibility = Vis(!ok || !_edVideo);
        EdNoPreview.Visibility = Vis(!ok);
        foreach (var b in new[] { EdPlayBtn, EdPlaySelBtn }) b.IsEnabled = ok;
    }

    /// <summary>Place la vue vidéo dans son cadre, à chaque fois que le cadre change de taille. Elle prend les proportions de la
    /// vidéo (pas de bandes claires autour : sa fenêtre n'est pas noire) ; elle se réduit à 1 pixel tant qu'il n'y a rien à montrer,
    /// ou quand le menu réduit se déroule par-dessus elle.</summary>
    void FitVideo()
    {
        if (EdVideo == null) return;
        bool show = _edReady && _edVideo;
        double boxW = Math.Max(1, EdScreen.ActualWidth - 2), boxH = Math.Max(1, EdScreen.ActualHeight - 2), w = boxW, h = boxH;
        if (show && _vidW > 0) { double k = Math.Min(boxW / _vidW, boxH / _vidH); w = Math.Max(1, Math.Floor(_vidW * k)); h = Math.Max(1, Math.Floor(_vidH * k)); }
        if (show && SideCovers && EdScreen.TranslatePoint(new Point((boxW - w) / 2, 0), Root).X < SideWide + 2) show = false;
        if (!show || _vidW == 0) { ShrinkVideo(!show); return; }
        EdVideo.Width = w; EdVideo.Height = h;
        EdVideo.HorizontalAlignment = HorizontalAlignment.Center; EdVideo.VerticalAlignment = VerticalAlignment.Center;
    }

    void EdScreen_Size(object sender, SizeChangedEventArgs e) => FitVideo();
    void PageEdit_Size(object sender, SizeChangedEventArgs e) => EdResize();

    /// <summary>Hauteur de l'aperçu : celle choisie avec la poignée, sinon la place libre de la fenêtre
    /// (l'aperçu, la frise et les boutons de lecture tiennent à l'écran sans défiler).</summary>
    void EdResize()
    {
        double view = PageEdit.ActualHeight;   // la page ne défile que de haut en bas : c'est sa hauteur visible
        if (view <= 0 || EdPanel.Visibility != Visibility.Visible) return;
        if (EdImage.Visibility == Visibility.Visible)
        { EdStage.Height = S.StageHeight > 0 ? Math.Clamp(S.StageHeight, 160, 2000) : Math.Max(460, view - 260); return; }
        if (!_edVideo || S.PreviewHeight > 0) return;
        // tout ce qui n'est pas l'aperçu, du haut de la page au bas des boutons de lecture
        double rest = EdPlayRow.ActualHeight <= 0 ? 400
            : EdPlayRow.TranslatePoint(new Point(0, EdPlayRow.ActualHeight), EdPage).Y + EdPage.Margin.Top - EdScreen.ActualHeight;
        EdScreen.Height = Math.Max(220, view - rest - 6);
    }

    double _gripY, _gripH;
    FrameworkElement GripBox(object grip) => grip == EdGripImg ? EdStage : EdScreen;
    void GripSet(object grip, double h)
    {
        if (grip == EdGripImg) S.StageHeight = h; else S.PreviewHeight = h;
        if (h > 0) GripBox(grip).Height = h; else EdResize();
    }

    void EdGrip_Down(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { GripSet(sender, 0); try { S.Save(); } catch (Exception ex) { App.Log(ex.ToString()); } return; } // retour à la taille automatique
        _gripY = e.GetPosition(this).Y; _gripH = GripBox(sender).ActualHeight;
        ((UIElement)sender).CaptureMouse();
    }

    void EdGrip_Move(object sender, MouseEventArgs e)
    {
        if (((UIElement)sender).IsMouseCaptured) GripSet(sender, Math.Clamp(_gripH + e.GetPosition(this).Y - _gripY, 160, 2000));
    }

    void EdGrip_Up(object sender, MouseButtonEventArgs e)
    {
        if (!((UIElement)sender).IsMouseCaptured) return;
        ((UIElement)sender).ReleaseMouseCapture();
        try { S.Save(); } catch (Exception ex) { App.Log(ex.ToString()); }
    }

    int _stripGen;   // un autre fichier a été ouvert : la frise en cours de fabrication s'arrête

    static BitmapImage LoadBitmap(string file)
    {
        var b = new BitmapImage();
        b.BeginInit(); b.UriSource = new Uri(file); b.CacheOption = BitmapCacheOption.OnLoad; b.EndInit(); b.Freeze();
        return b;
    }

    /// <summary>Habille la frise comme dans un logiciel de montage : douze vignettes pour une vidéo (elles arrivent une à une),
    /// la forme du son pour une musique. Sans elles la frise marche pareil : un échec ici ne gêne rien.</summary>
    async Task LoadStrip(string path, double dur, bool video)
    {
        int gen = _stripGen;
        if (dur <= 0) return;
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"prisme-frise-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(dir);
            if (!video)
            {
                var png = System.IO.Path.Combine(dir, "son.png");
                await Engines.Run(Engines.Ffmpeg, new[] { "-hide_banner", "-nostdin", "-y", "-i", path, "-filter_complex",
                    "aformat=channel_layouts=mono,showwavespic=s=1600x96:colors=0xC9A8FF:filter=peak", "-frames:v", "1", png }, null, null, CancellationToken.None);
                if (gen == _stripGen && path == _edPath && File.Exists(png)) EdWave.Source = LoadBitmap(png);
                return;
            }
            const int n = 12;
            for (int i = 0; i < n && gen == _stripGen && path == _edPath; i++)
            {   // « -ss » avant « -i » : saut direct, une vignette coûte une fraction de seconde même sur un long film
                var jpg = System.IO.Path.Combine(dir, $"{i}.jpg");
                var at = ((i + 0.5) * dur / n).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
                await Engines.Run(Engines.Ffmpeg, new[] { "-hide_banner", "-nostdin", "-y", "-ss", at, "-i", path, "-frames:v", "1", "-an",
                    "-vf", "scale=-2:96", "-q:v", "4", jpg }, null, null, CancellationToken.None);
                if (gen != _stripGen || path != _edPath) break;
                EdStrip.Children.Add(new Image { Source = File.Exists(jpg) ? LoadBitmap(jpg) : null, Stretch = Stretch.UniformToFill, ClipToBounds = true });
            }
        }
        catch (Exception e) { App.Log("frise : " + e.Message); }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    void ShrinkVideo(bool tiny)
    {
        EdVideo.Width = EdVideo.Height = tiny ? 1 : double.NaN;
        EdVideo.HorizontalAlignment = tiny ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        EdVideo.VerticalAlignment = tiny ? VerticalAlignment.Top : VerticalAlignment.Stretch;
    }

    async Task<bool> EnsureVlc()
    {
        if (_mp != null) return true;
        try
        {
            PrepareVlc();
            _vlc ??= await _vlcInit;   // déjà prêt d'habitude ; sinon on attend sans figer la fenêtre
            if (_mp != null) return true;
            _mp = new VlcPlayer(_vlc) { EnableHardwareDecoding = true };
            EdVideo.MediaPlayer = _mp;
            return true;
        }
        catch (Exception e) { App.Log("VLC : " + e); _mp = null; return false; }
    }

    // les évènements de VLC arrivent sur ses propres fils : on interroge le lecteur au rythme d'une minuterie à la place
    void EdTick(object sender, EventArgs e)
    {
        if (_mp == null) return;
        // Time > 0 : la première image est décodée ; mise en pause plus tôt, VLC ignorerait les sauts suivants
        if (_edArm && _mp.IsPlaying && _mp.Time > 0) { _mp.SetPause(true); _edArm = false; _mp.Mute = false; if (_edPending < 0) _edPending = _edPos; PreviewReady(true); }
        var st = _mp.State;
        if (st == VLCState.Error && EdLoading.Visibility == Visibility.Visible) { _edArm = false; PreviewReady(false); }
        if (_edPending >= 0 && _mp.IsSeekable && st is VLCState.Playing or VLCState.Paused)
        { _mp.Time = (long)(_edPending * 1000); _edTarget = _edPending; _edRetry = 5; _edPending = -1; _edSkip = 4; }
        else if (_edSkip > 0) _edSkip--; // juste après un saut, VLC donne encore l'ancienne position
        else if (_edRetry > 0 && st is VLCState.Playing or VLCState.Paused)
        {   // VLC ignore parfois un saut demandé pendant qu'il ouvre le fichier : on vérifie et on redemande
            if (Math.Abs(_mp.Time / 1000.0 - _edTarget) > 1) { _mp.Time = (long)(_edTarget * 1000); _edSkip = 4; _edRetry--; }
            else _edRetry = 0;
        }
        else if (_mp.IsPlaying && _edDrag != 3 && _edPending < 0) _edPos = Math.Min(_edDur, _mp.Time / 1000.0);
        if (st == VLCState.Ended) _edPos = _edDur;
        if (_edSel && _mp.IsPlaying && _edPos >= _edB) { _mp.SetPause(true); _edSel = false; _edPos = _edB; _edPending = _edB; }
        bool playing = _mp.IsPlaying && !_edArm;
        EdPlayIcon.Data = (Geometry)FindResource(playing ? "IcPause" : "IcPlay");
        EdPlayText.Text = playing ? "Pause" : "Lecture";
        UpdateHead();
    }

    void Seek(double t)
    {
        _edPos = Math.Clamp(t, 0, _edDur);
        if (_mp == null) { UpdateHead(); return; }
        if (_mp.State is VLCState.Ended or VLCState.Stopped or VLCState.Error)
        {   // fin atteinte : VLC ne sait plus sauter, on le relance en pause
            var mp = _mp; _edArm = true; _ = Vlc(() => { mp.Stop(); mp.Mute = true; mp.Play(); });
        }
        _edPending = _edPos;
        UpdateHead();
    }

    void EdPlay_Click(object sender, RoutedEventArgs e)
    {
        _edSel = false;
        if (_mp != null && _mp.IsPlaying && !_edArm) _mp.SetPause(true); else Play();
    }

    void Play()
    {
        if (_mp == null || _edDur <= 0) return;
        if (_mp.State is VLCState.Ended or VLCState.Stopped or VLCState.Error)
        {
            if (_edPos >= _edDur - 0.2) _edPos = _edSel ? _edA : 0;
            var mp = _mp; _edArm = false; _ = Vlc(() => { mp.Stop(); mp.Mute = false; mp.Play(); }); _edPending = _edPos;
            return;
        }
        _edArm = false; _mp.Mute = false;
        if (_edPos >= _edDur - 0.2) _edPos = 0;
        _mp.SetPause(false);
        _edPending = _edPos; // repart toujours de la position affichée, même si un saut a été perdu en pause
    }

    void EdPlaySel_Click(object sender, RoutedEventArgs e)
    {
        if (_mp == null || _edDur <= 0) return;
        Seek(_edA); Play(); _edSel = true;
    }

    void EdSetStart_Click(object sender, RoutedEventArgs e) { _edA = Math.Clamp(_edPos, 0, Math.Max(0, _edB - 0.1)); UpdateTimeline(); UpdateTimes(); }
    void EdSetEnd_Click(object sender, RoutedEventArgs e) { _edB = Math.Clamp(_edPos, Math.Min(_edDur, _edA + 0.1), _edDur); UpdateTimeline(); UpdateTimes(); }
    void EdKeepAll_Click(object sender, RoutedEventArgs e) { _edA = 0; _edB = _edDur; UpdateTimeline(); UpdateTimes(); }

    double TlW => Math.Max(1, EdTimeline.ActualWidth - 24);   // 12 px de chaque côté : la place des deux poignées
    double TlX(double t) => 12 + (_edDur > 0 ? t / _edDur : 0) * TlW;
    double TlT(double x) => Math.Clamp((x - 12) / TlW * _edDur, 0, _edDur);

    void UpdateTimeline()
    {
        double xa = TlX(_edA), xb = TlX(_edB), w = EdTimeline.ActualWidth;
        Canvas.SetLeft(EdBand, xa); EdBand.Width = Math.Max(0, xb - xa);
        EdDimA.Width = Math.Max(0, xa - 12); Canvas.SetLeft(EdDimB, xb); EdDimB.Width = Math.Max(0, w - 12 - xb);
        Canvas.SetLeft(EdHandleA, xa - 12); Canvas.SetLeft(EdHandleB, xb);   // les poignées sont les deux bords du cadre, vers l'extérieur
        double ca = xa - 6, cb = xb + 6;
        // les étiquettes se placent sous leur poignée, sans sortir de la frise ni se chevaucher
        EdTabAText.Text = Edit.Time(_edA); EdTabBText.Text = Edit.Time(_edB);
        var inf = new Size(double.PositiveInfinity, double.PositiveInfinity);
        EdTabA.Measure(inf); EdTabB.Measure(inf);
        double wa = EdTabA.DesiredSize.Width, wb = EdTabB.DesiredSize.Width;
        double la = Math.Clamp(ca - wa / 2, 0, Math.Max(0, w - wa)), lb = Math.Clamp(cb - wb / 2, 0, Math.Max(0, w - wb));
        if (la + wa + 4 > lb)
        {
            double m = (xa + xb) / 2;
            la = Math.Min(la, m - 2 - wa); lb = Math.Max(lb, m + 2);
            if (la < 0) { la = 0; lb = Math.Max(lb, wa + 4); }
            if (lb > w - wb) { lb = Math.Max(0, w - wb); la = Math.Max(0, Math.Min(la, lb - 4 - wa)); }
        }
        Canvas.SetLeft(EdTabA, la); Canvas.SetLeft(EdTabB, lb);
        UpdateHead();
    }

    void UpdateHead()
    {
        Canvas.SetLeft(EdHead, TlX(_edPos) - 8);
        EdNow.Text = Edit.Time(_edPos);
        EdTotal.Text = _edDur > 0 ? " / " + Edit.Time(_edDur) : "";
    }

    void UpdateTimes()
    {
        if (!EdStartBox.IsKeyboardFocused) EdStartBox.Text = Edit.Time(_edA);
        if (!EdEndBox.IsKeyboardFocused) EdEndBox.Text = Edit.Time(_edB);
        EdLen.Text = "extrait de " + Edit.Time(Math.Max(0, _edB - _edA));
    }

    void EdTimeline_Size(object sender, SizeChangedEventArgs e) => UpdateTimeline();

    double _edGrab;       // écart entre la souris et la poignée au moment où on l'attrape : elle ne saute pas

    /// <summary>1 = début, 2 = fin, 0 = aucune. Une poignée ne s'attrape que sur elle-même ou sur son étiquette :
    /// partout ailleurs, cliquer déplace la lecture (avant, cliquer près d'une poignée la déplaçait par surprise).</summary>
    int Poignee(Point p)
    {
        bool Dans(FrameworkElement tab) { double l = Canvas.GetLeft(tab); return p.X >= l && p.X <= l + tab.ActualWidth; }
        if (p.Y >= 70) return Dans(EdTabA) ? 1 : Dans(EdTabB) ? 2 : 0;
        if (p.Y < 10) return 0;
        double xa = TlX(_edA), xb = TlX(_edB);
        bool a = p.X >= xa - 16 && p.X <= xa + 2, b = p.X >= xb - 2 && p.X <= xb + 16;
        return a && b ? (p.X < (xa + xb) / 2 ? 1 : 2) : a ? 1 : b ? 2 : 0;
    }

    void EdTimeline_Down(object sender, MouseButtonEventArgs e)
    {
        if (_edDur <= 0) return;
        var p = e.GetPosition(EdTimeline);
        int h = Poignee(p);
        _edGrab = h == 1 ? p.X - TlX(_edA) : h == 2 ? p.X - TlX(_edB) : 0;
        _edDrag = h == 0 ? 3 : h;
        EdTimeline.CaptureMouse();
        EdTimeline_Move(sender, e);
    }

    void EdTimeline_Move(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(EdTimeline);
        if (_edDrag == 0)
        {
            EdTimeline.Cursor = _edDur > 0 && Poignee(p) != 0 ? Cursors.SizeWE : Cursors.Hand;
            return;
        }
        var t = TlT(p.X - _edGrab);
        if (_edDrag == 1) { _edA = Math.Clamp(t, 0, Math.Max(0, _edB - 0.1)); Seek(_edA); }
        else if (_edDrag == 2) { _edB = Math.Clamp(t, Math.Min(_edDur, _edA + 0.1), _edDur); Seek(_edB); }
        else Seek(t);
        UpdateTimeline(); UpdateTimes();
    }

    void EdTimeline_Up(object sender, MouseButtonEventArgs e) { _edDrag = 0; EdTimeline.ReleaseMouseCapture(); }

    void EdTimeBox_Key(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) EdTimeBox_Commit(sender, e); }

    void EdTimeBox_Commit(object sender, RoutedEventArgs e)
    {
        var box = (TextBox)sender;
        // on ne bouge la lecture que si la valeur a changé : quitter la case « Fin » sans la toucher ne doit pas sauter à la fin
        if (Edit.TryParseTime(box.Text, out var v) && _edDur > 0 && box.Text != Edit.Time(box == EdStartBox ? _edA : _edB))
        {
            if (box == EdStartBox) _edA = Math.Clamp(v, 0, Math.Max(0, _edB - 0.1));
            else _edB = Math.Clamp(v, Math.Min(_edDur, _edA + 0.1), _edDur);
            Seek(box == EdStartBox ? _edA : _edB);
        }
        box.Text = Edit.Time(box == EdStartBox ? _edA : _edB); // valeur refusée : on remet la bonne
        UpdateTimeline(); UpdateTimes();
    }

    // ---------- image ----------

    async Task LoadImage(string path)
    {
        _imW = _imH = 0; EdPic.Source = null; EdPicMsg.Text = "";
        EdPicLoading.Visibility = Visibility.Visible;
        DrawCrop();
        // FFmpeg décode tout (HEIC compris) et redresse la photo comme à la conversion : le cadre tombe au même endroit
        var tmp = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"prisme-{Guid.NewGuid():N}.png");
        try
        {
            await Engines.Run(Engines.Ffmpeg, new[] { "-hide_banner", "-nostdin", "-y", "-i", path, "-frames:v", "1", "-update", "1", tmp }, null, null, CancellationToken.None);
            if (path != _edPath) return;
            if (!File.Exists(tmp)) { EdPicMsg.Text = "Image illisible ou endommagée"; return; }
            var bmp = new BitmapImage();
            bmp.BeginInit(); bmp.UriSource = new Uri(tmp); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.EndInit(); bmp.Freeze();
            _imW = bmp.PixelWidth; _imH = bmp.PixelHeight;
            EdPic.Source = bmp; EdPicMsg.Text = "";
            EdInfo.Text = $"{_imW} × {_imH} px";
            EdReset_Click(null, null);
        }
        catch (Exception e) { App.Log(e.ToString()); EdPicMsg.Text = "Image illisible ou endommagée"; }
        finally
        {
            try { File.Delete(tmp); } catch { }
            if (path == _edPath) EdPicLoading.Visibility = Visibility.Collapsed;
        }
    }

    void EdStage_Size(object sender, SizeChangedEventArgs e) => DrawCrop();

    int CropW => Math.Max(1, (int)Math.Round(_crop.Width * _imW));
    int CropH => Math.Max(1, (int)Math.Round(_crop.Height * _imH));
    Rect ScreenCrop => new(_fit.X + _crop.X * _fit.Width, _fit.Y + _crop.Y * _fit.Height, _crop.Width * _fit.Width, _crop.Height * _fit.Height);

    void DrawCrop(bool cropChanged = false)
    {
        var shapes = new FrameworkElement[] { EdCropRect, EdC0, EdC1, EdC2, EdC3, EdShade };
        foreach (var s in shapes) s.Visibility = Vis(_imW > 0);
        if (_imW == 0) return;
        double aw = Math.Max(1, EdStage.ActualWidth - 32), ah = Math.Max(1, EdStage.ActualHeight - 32);
        double k = Math.Min(aw / _imW, ah / _imH);
        _fit = new Rect(16 + (aw - _imW * k) / 2, 16 + (ah - _imH * k) / 2, _imW * k, _imH * k);
        var r = ScreenCrop;
        Canvas.SetLeft(EdCropRect, r.X); Canvas.SetTop(EdCropRect, r.Y); EdCropRect.Width = r.Width; EdCropRect.Height = r.Height;
        var corners = new[] { r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft };
        var handles = new[] { EdC0, EdC1, EdC2, EdC3 };
        for (int i = 0; i < 4; i++) { Canvas.SetLeft(handles[i], corners[i].X - 6); Canvas.SetTop(handles[i], corners[i].Y - 6); }
        EdShade.Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(_fit), new RectangleGeometry(r));
        if (cropChanged) ShowSize();
    }

    void ShowSize()
    {
        _sizeSync = true;
        EdW.Text = Math.Max(1, (int)Math.Round(CropW * _sx)).ToString();
        EdH.Text = Math.Max(1, (int)Math.Round(CropH * _sy)).ToString();
        _sizeSync = false;
        bool cropped = CropW != _imW || CropH != _imH;
        EdSizeInfo.Text = $"Original : {_imW} × {_imH} px" + (cropped ? $"\nAprès recadrage : {CropW} × {CropH} px" : "");
    }

    void EdSize_Changed(object sender, TextChangedEventArgs e)
    {
        if (_sizeSync || _imW == 0 || !int.TryParse(((TextBox)sender).Text, out var v) || v <= 0) return;
        if (sender == EdW) { _sx = v / (double)CropW; if (EdLock.IsChecked == true) _sy = _sx; }
        else { _sy = v / (double)CropH; if (EdLock.IsChecked == true) _sx = _sy; }
        if (EdLock.IsChecked == true)
        {
            _sizeSync = true;
            var other = sender == EdW ? EdH : EdW;
            other.Text = Math.Max(1, (int)Math.Round((sender == EdW ? CropH : CropW) * _sx)).ToString();
            _sizeSync = false;
        }
    }

    void EdPct_Click(object sender, RoutedEventArgs e)
    {
        _sx = _sy = double.Parse((string)((FrameworkElement)sender).Tag, System.Globalization.CultureInfo.InvariantCulture);
        ShowSize();
    }

    void EdReset_Click(object sender, RoutedEventArgs e)
    {
        _crop = new Rect(0, 0, 1, 1); _ratio = null; _sx = _sy = 1;
        foreach (var rb in EdRatios.Children.OfType<RadioButton>()) rb.IsChecked = (string)rb.Tag == "free";
        DrawCrop(cropChanged: true);
    }

    void EdRatio_Click(object sender, RoutedEventArgs e)
    {
        if (_imW == 0) return;
        var tag = (string)((FrameworkElement)sender).Tag;
        _ratio = tag switch
        {
            "free" => null,
            "orig" => _imW / (double)_imH,
            _ => double.Parse(tag.Split(':')[0]) / double.Parse(tag.Split(':')[1]),
        };
        if (_ratio is double r)
        {   // le plus grand cadre de ces proportions, centré
            double rf = r * _imH / _imW, w = rf <= 1 ? rf : 1, h = rf <= 1 ? 1 : 1 / rf;
            _crop = new Rect((1 - w) / 2, (1 - h) / 2, w, h);
        }
        DrawCrop(cropChanged: true);
    }

    Point ToImage(Point p) => new(Math.Clamp((p.X - _fit.X) / _fit.Width, 0, 1), Math.Clamp((p.Y - _fit.Y) / _fit.Height, 0, 1));

    int HitCorner(Point p)
    {
        var r = ScreenCrop;
        var corners = new[] { r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft };
        for (int i = 0; i < 4; i++) if ((corners[i] - p).Length <= 14) return i + 1;
        return 0;
    }

    bool Full => _crop.Width > 0.9999 && _crop.Height > 0.9999;

    void EdStage_Down(object sender, MouseButtonEventArgs e)
    {
        if (_imW == 0) return;
        var p = e.GetPosition(EdStage);
        int c = HitCorner(p);
        _cropMode = c > 0 ? c : ScreenCrop.Contains(p) && !Full ? 5 : 6; // 1-4 coins, 5 déplacer, 6 nouveau cadre
        _from = ToImage(p); _cropFrom = _crop;
        EdStage.CaptureMouse();
    }

    void EdStage_Move(object sender, MouseEventArgs e)
    {
        if (_imW == 0) return;
        var p = e.GetPosition(EdStage);
        if (_cropMode == 0)
        {
            int c = HitCorner(p);
            EdStage.Cursor = c is 1 or 3 ? Cursors.SizeNWSE : c is 2 or 4 ? Cursors.SizeNESW : ScreenCrop.Contains(p) && !Full ? Cursors.SizeAll : Cursors.Cross;
            return;
        }
        var u = ToImage(p);
        if (_cropMode == 5)
        {
            double x = Math.Clamp(_cropFrom.X + u.X - _from.X, 0, 1 - _cropFrom.Width);
            double y = Math.Clamp(_cropFrom.Y + u.Y - _from.Y, 0, 1 - _cropFrom.Height);
            _crop = new Rect(x, y, _cropFrom.Width, _cropFrom.Height);
        }
        else
        {   // le coin opposé reste fixe
            var f = _cropFrom;
            var anchor = _cropMode switch { 1 => f.BottomRight, 2 => f.BottomLeft, 3 => f.TopLeft, 4 => f.TopRight, _ => _from };
            _crop = FromAnchor(anchor, u);
        }
        DrawCrop(cropChanged: true);
    }

    void EdStage_Up(object sender, MouseButtonEventArgs e)
    {
        // un simple clic (ou un cadre minuscule) ne remplace pas le cadre existant
        if (_cropMode != 0 && (_crop.Width * _imW < 8 || _crop.Height * _imH < 8)) { _crop = _cropFrom; DrawCrop(cropChanged: true); }
        _cropMode = 0;
        EdStage.ReleaseMouseCapture();
    }

    Rect FromAnchor(Point a, Point u)
    {
        double w = Math.Abs(u.X - a.X), h = Math.Abs(u.Y - a.Y);
        int sx = u.X >= a.X ? 1 : -1, sy = u.Y >= a.Y ? 1 : -1;
        if (_ratio is double r)
        {
            double rf = r * _imH / _imW; // proportions exprimées en fractions de l'image
            if (w > h * rf) w = h * rf; else h = w / rf;
            double maxW = sx > 0 ? 1 - a.X : a.X, maxH = sy > 0 ? 1 - a.Y : a.Y;
            if (w > maxW) { w = maxW; h = w / rf; }
            if (h > maxH) { h = maxH; w = h * rf; }
        }
        return new Rect(sx > 0 ? a.X : a.X - w, sy > 0 ? a.Y : a.Y - h, w, h);
    }

    // ---------- enregistrer ----------

    Edit BuildEdit()
    {
        if (Formats.KindOf(_edPath) != Kind.Image)
            return (_edDur <= 0 ? Edit.None : new Edit(_edA < 0.05 ? 0 : _edA, _edB >= _edDur - 0.05 ? 0 : _edB, 0, 0, 1, 1, 0, 0))
                with { Rotate = _edVideo ? _edRot : 0, Volume = _edVol, Speed = _edSpeed };
        if (_imW == 0) throw new Exception("L'image n'est pas encore chargée.");
        if (!int.TryParse(EdW.Text, out var w) || !int.TryParse(EdH.Text, out var h) || w <= 0 || h <= 0 || w > 16384 || h > 16384)
            throw new Exception("Taille invalide : largeur et hauteur en pixels, entre 1 et 16384.");
        bool resize = w != CropW || h != CropH;
        return Full
            ? new Edit(0, 0, 0, 0, 1, 1, resize ? w : 0, resize ? h : 0)
            : new Edit(0, 0, _crop.X, _crop.Y, _crop.Width, _crop.Height, resize ? w : 0, resize ? h : 0);
    }

    void EdGif_Click(object sender, RoutedEventArgs e)
    {
        var tag = (string)((FrameworkElement)sender).Tag;
        if (int.TryParse(tag, out var fps)) S.GifFps = fps; else S.GifQuality = tag;
        try { S.Save(); } catch (Exception ex) { App.Log(ex.ToString()); }
    }

    void EdSave(Target t)
    {
        Edit edit;
        try { edit = BuildEdit(); }
        catch (Exception ex) { EdError.Text = ex.Message; EdError.Visibility = Visibility.Visible; return; }
        if (edit.IsEmpty && t.Id == "same")
        {
            EdError.Text = Formats.KindOf(_edPath) == Kind.Image
                ? "Rien n'a changé : trace un cadre ou change la taille d'abord."
                : "Rien n'a changé : tire les étiquettes Début et Fin de la frise pour choisir l'extrait, ou choisis une rotation, un volume, une vitesse.";
            EdError.Visibility = Visibility.Visible; return;
        }
        EdError.Visibility = Visibility.Collapsed;
        App.Current.StartConversions(new List<string> { _edPath }, t.Id,
            new Dictionary<string, Edit>(StringComparer.OrdinalIgnoreCase) { [_edPath] = edit });
    }

    // ---------- assembler ----------

    async void RefreshMix()
    {
        void Show()
        {
            MixList.ItemsSource = _mixPaths.Select((p, i) => new MixItem(p, System.IO.Path.GetFileName(p),
                _mixDur.TryGetValue(p, out var d) ? (d > 0 ? Edit.Time(d) : "?") : "…", $"{i + 1}.")).ToList();
            var total = _mixPaths.Sum(p => _mixDur.TryGetValue(p, out var d) ? d : 0);
            MixTitle.Text = $"{_mixPaths.Count} MUSIQUE{(_mixPaths.Count > 1 ? "S" : "")} · {Edit.Time(total)} AU TOTAL · DANS CET ORDRE";
        }
        if (_mixPaths.Count == 0) { ShowEditMode(mix: null); return; }
        Show();
        foreach (var p in _mixPaths.Where(p => !_mixDur.ContainsKey(p)).ToList())
        {
            _mixDur[p] = await Converter.Duration(p, CancellationToken.None);
            Show();
        }
    }

    int MixIndex(object sender) => _mixPaths.FindIndex(p => p == ((MixItem)((FrameworkElement)sender).DataContext).Path);
    void MixMove(object sender, int by)
    {
        int i = MixIndex(sender), j = i + by;
        if (i < 0 || j < 0 || j >= _mixPaths.Count) return;
        (_mixPaths[i], _mixPaths[j]) = (_mixPaths[j], _mixPaths[i]);
        RefreshMix();
    }
    void MixUp_Click(object sender, RoutedEventArgs e) => MixMove(sender, -1);
    void MixDown_Click(object sender, RoutedEventArgs e) => MixMove(sender, 1);
    void MixRemove_Click(object sender, RoutedEventArgs e) { int i = MixIndex(sender); if (i >= 0) _mixPaths.RemoveAt(i); RefreshMix(); }
    void MixClear_Click(object sender, RoutedEventArgs e) { _mixPaths.Clear(); RefreshMix(); }

    void MixSave_Click(object sender, RoutedEventArgs e)
    {
        if (_mixPaths.Count < 2) return;
        App.Current.StartConversions(_mixPaths.ToList(), ((Target)((FrameworkElement)sender).DataContext).Id);
    }
}
