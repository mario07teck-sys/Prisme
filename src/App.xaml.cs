using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Prisme.Core;
using Forms = System.Windows.Forms;

namespace Prisme;

public partial class App : Application
{
    public static Settings Settings { get; private set; }
    public static ObservableCollection<Job> Conversions { get; } = new();
    public static ObservableCollection<Job> Downloads { get; } = new();
    public static new App Current => (App)Application.Current;

    const string MutexName = "Local\\Prisme-Instance-7c1";
    static string PipeName => "Prisme-Pipe-" + Environment.UserName;
    static readonly SemaphoreSlim ConvSlots = new(2); // 2 conversions en parallèle maximum
    static readonly SemaphoreSlim DlSlots = new(2);   // files séparées : un téléchargement n'attend pas 50 conversions

    Mutex _mutex;
    Forms.NotifyIcon _tray;
    MainWindow _win;
    bool _exiting;
    readonly Dictionary<string, List<string>> _pending = new();
    DispatcherTimer _debounce;

    protected override void OnStartup(StartupEventArgs e)
    {
        var args = e.Args;
        Settings = Settings.Load();

        // commandes sans interface (installeur / désinstalleur / test)
        if (args.Length > 0)
        {
            switch (args[0])
            {
                case "--register-shell": Shell.RegisterContextMenu(); Settings.ContextMenu = true; Settings.Save(); Shutdown(0); return;
                case "--unregister": Shell.UnregisterContextMenu(); Shell.SetAutostart(false); Shutdown(0); return;
                case "--unregister-menu": Shell.UnregisterContextMenu(); Settings.ContextMenu = false; Settings.Save(); Shutdown(0); return;
                case "--autostart":
                    Settings.StartWithWindows = args.Length > 1 && args[1] == "on";
                    Shell.SetAutostart(Settings.StartWithWindows); Settings.Save(); Shutdown(0); return;
                case "--selftest": Shutdown(args.Length > 1 ? SelfTest.RunReal(args[1]) : SelfTest.Run()); return;
                case "--selftest-office" when args.Length > 1: Shutdown(SelfTest.RunOffice(args[1])); return;
            }
        }

        _mutex = new Mutex(true, MutexName, out bool first);
        if (!first) { SendToFirst(args); Shutdown(0); return; }

        // après une mise à jour : menu clic droit et démarrage auto suivent le nouvel exe, et yt-dlp
        // (remplacé par la version de l'installeur, parfois plus ancienne) est revérifié tout de suite.
        // Seule la version installée le fait : une build de dev ne détourne pas le menu.
        var version = typeof(App).Assembly.GetName().Version.ToString();
        bool updated = Settings.LastVersion != "" && Settings.LastVersion != version;
        if (Settings.LastVersion != version) { Settings.LastVersion = version; Settings.LastEngineUpdate = default; Settings.Save(); }
        if (Shell.IsInstalledCopy)
            try
            {
                if (Settings.ContextMenu && !Shell.ContextMenuRegistered()) Shell.RegisterContextMenu();
                if (Settings.StartWithWindows && !Shell.Autostart()) Shell.SetAutostart(true);
            }
            catch (Exception ex) { Log(ex.ToString()); }

        DispatcherUnhandledException += (_, ex) =>
        {
            Log(ex.Exception.ToString());
            ex.Handled = true;
        };

        CreateTray();
        _win = new MainWindow();
        // LibreOffice manquant : la fenêtre le propose, puis montre son installation
        OfficeSetup.Ask = (job, size) => Dispatcher.InvokeAsync(() => { ShowMain(); return _win.AskOffice(job, size); }).Task.Unwrap();
        OfficeSetup.Finished += (job, ex) => Dispatcher.BeginInvoke(() => _win.OfficeFinished(job, ex));
        LoadHistory(); // après la fenêtre : elle affiche l'en-tête « Activité » quand la liste se remplit
        _debounce =new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); FlushPending(); };
        StartPipeServer();

        bool tray = args.Contains("--tray");
        if (!tray) ShowMain();
        HandleArgs(args);
        _ = Downloader.UpdateEngine(Settings);
        Updater.Cleanup();
        if (updated && Settings.Notify) _tray.ShowBalloonTip(4000, "Prisme est à jour", $"Version {Updater.Current} installée.", Forms.ToolTipIcon.None);
        if (Shell.IsInstalledCopy) _ = UpdateLoop(); // une build de dev ne se remplace pas par la version publiée
    }

    // ---------- mises à jour (releases GitHub) ----------

    public string UpdateStatus { get; private set; } = "";
    public Version UpdateVersion { get; private set; }
    string _updateSetup;
    public bool UpdateReady => _updateSetup != null;
    public event Action UpdateChanged;
    bool _checking;

    void SetUpdate(string status) { UpdateStatus = status; UpdateChanged?.Invoke(); }

    async Task UpdateLoop()
    {
        await Task.Delay(TimeSpan.FromSeconds(20)); // laisse Prisme démarrer tranquillement
        var idle = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        idle.Tick += (_, _) => TryAutoInstall();
        idle.Start();
        while (true)
        {
            await CheckUpdatesNow();
            await Task.Delay(TimeSpan.FromHours(6));
        }
    }

    public async Task CheckUpdatesNow()
    {
        if (_checking || UpdateReady) return;
        _checking = true;
        try
        {
            SetUpdate("Recherche d'une nouvelle version…");
            var r = await Task.Run(() => Updater.Latest(CancellationToken.None));
            if (!Updater.IsNewer(r)) { SetUpdate($"Prisme est à jour (version {Updater.Current})."); return; }
            UpdateVersion = r.Version;
            SetUpdate($"Téléchargement de la version {r.Version}…");
            int last = -1;
            _updateSetup = await Task.Run(() => Updater.Download(r, p =>
            {
                if ((int)p == last) return; last = (int)p;
                Dispatcher.BeginInvoke(() => SetUpdate($"Téléchargement de la version {r.Version} ({r.Size / 1048576.0:0} Mo)… {last} %"));
            }, CancellationToken.None));
            SetUpdate(Settings.AutoUpdate
                ? $"Version {r.Version} prête : elle s'installera dès que Prisme sera rangé ou fermé."
                : $"Version {r.Version} prête à installer.");
            if (Settings.Notify)
            {
                _lastNotified = null;
                _tray.ShowBalloonTip(5000, $"Prisme {r.Version} est disponible", "Ouvre Prisme et clique sur « Mettre à jour ».", Forms.ToolTipIcon.None);
            }
        }
        catch (Exception ex) { Log("mise à jour : " + ex); SetUpdate("Impossible de vérifier les mises à jour pour l'instant (pas de connexion ?)."); }
        finally { _checking = false; }
    }

    bool Busy => Conversions.Concat(Downloads).Any(j => j.IsActive);

    /// <summary>Installation automatique quand personne n'utilise Prisme : rangé près de l'horloge et sans tâche en cours.</summary>
    void TryAutoInstall()
    {
        if (UpdateReady && Settings.AutoUpdate && _win is { IsVisible: false } && !Busy) ApplyUpdate("tray");
    }

    /// <summary>Bouton « Mettre à jour » : relance Prisme une fois installé.</summary>
    public void InstallUpdateNow()
    {
        if (!UpdateReady) return;
        if (Busy && MessageBox.Show(_win, "Des tâches sont en cours : elles seront annulées. Mettre à jour maintenant ?", "Prisme",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        ApplyUpdate("show");
    }

    void ApplyUpdate(string relaunch)
    {
        try { Updater.Install(_updateSetup, relaunch); }
        catch (Exception ex) { Log("installation de la mise à jour : " + ex); SetUpdate("L'installation de la mise à jour a échoué."); _updateSetup = null; return; }
        foreach (var j in Conversions.Concat(Downloads).Where(j => j.IsActive)) j.Cts.Cancel();
        _exiting = true;
        _tray.Visible = false; _tray.Dispose();
        _win?.Close();
        Shutdown(0);
    }

    // ---------- instance unique ----------

    static void SendToFirst(string[] args)
    {
        for (int i = 0; i < 25; i++) // la 1re instance peut être en train de démarrer
        {
            try
            {
                using var c = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                c.Connect(200);
                var data = Encoding.UTF8.GetBytes(string.Join("\0", args.Length == 0 ? new[] { "--show" } : args));
                c.Write(data, 0, data.Length);
                return;
            }
            catch { Thread.Sleep(100); }
        }
    }

    void StartPipeServer()
    {
        Task.Run(async () =>
        {
            while (true)
            {
                try
                {
                    using var s = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await s.WaitForConnectionAsync();
                    using var ms = new MemoryStream();
                    await s.CopyToAsync(ms);
                    var args = Encoding.UTF8.GetString(ms.ToArray()).Split('\0', StringSplitOptions.RemoveEmptyEntries);
                    _ = Dispatcher.BeginInvoke(() => HandleArgs(args, fromOther: true));
                }
                catch (Exception ex) { Log(ex.ToString()); await Task.Delay(200); }
            }
        });
    }

    void HandleArgs(string[] args, bool fromOther = false)
    {
        if (args.Length == 0 || args[0] == "--show") { if (fromOther) ShowMain(); return; }
        if (args[0] == "--tray") return;
        if (args[0] == "--download" && args.Length >= 2 && Downloader.IsUrl(args[1]))
        {   // Prisme.exe --download <lien> [best|1080|720|mp3]
            ShowMain(); _win.GoTo("download");
            StartDownload(args[1], args.Length > 2 && args[2] is "best" or "1080" or "720" or "mp3" ? args[2] : "best");
            return;
        }
        if (args[0] == "--edit" && args.Length >= 2)
        {   // Prisme.exe --edit <fichier> [autres musiques…] : onglet Retoucher
            ShowMain(); _win.EditFiles(args.Skip(1).Where(File.Exists));
            return;
        }
        if (args[0] == "--to" && args.Length >= 3)
        {
            var id = args[1];
            if (!_pending.TryGetValue(id, out var list)) _pending[id] = list = new();
            list.AddRange(args.Skip(2).Where(File.Exists));
            _debounce.Stop(); _debounce.Start(); // un clic droit sur 20 fichiers = 20 lancements : on les regroupe
            return;
        }
        var files = args.SkipWhile(a => a.StartsWith("--")).ToList();
        ShowMain();
        if (files.Count > 0)
        {
            if (files.Count == 1 && Downloader.IsUrl(files[0])) _win.ShowDownload(files[0]);
            else _win.AddFiles(files);
        }
    }

    void FlushPending()
    {
        foreach (var (id, files) in _pending)
            StartConversions(files.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase).ToList(), id);
        _pending.Clear();
        ShowMain();
        _win.GoTo("convert");
    }

    // ---------- file d'attente ----------

    /// <summary>Lance la conversion de <paramref name="files"/> vers le format <paramref name="id"/>.</summary>
    public void StartConversions(List<string> files, string id, IReadOnlyDictionary<string, Edit> edits = null)
    {
        files = files.Where(f => Formats.Find(f, id) != null).ToList();
        if (files.Count == 0) return;
        bool combine = id == "pdf-merge" || id.StartsWith("merge-") || (id == "pdf" && files.All(f => Formats.KindOf(f) == Kind.Image));
        var groups = combine ? new List<List<string>> { files } : files.Select(f => new List<string> { f }).ToList();
        foreach (var g in groups)
        {
            var job = new Job
            {
                Inputs = g, Target = Formats.Find(g[0], id), TargetId = id, Edits = edits,
                Title = g.Count > 1 ? $"{Path.GetFileName(g[0])} + {g.Count - 1} autre{(g.Count > 2 ? "s" : "")}" : Path.GetFileName(g[0]),
            };
            Conversions.Insert(0, job);
            _ = RunJob(job, ConvSlots, "Conversion…", () => Converter.Run(job, Settings));
        }
    }

    public void StartDownload(string url, string mode)
    {
        var job = new Job { Url = url.Trim(), DownloadMode = mode, Title = url.Trim() };
        Downloads.Insert(0, job);
        if (Downloader.IsPlaylist(job.Url))
        {   // une liste : sa tâche ne fait que la lire, puis laisse la place à un téléchargement par élément
            _ = RunJob(job, DlSlots, "Lecture de la liste…", async () =>
            {
                var urls = await Downloader.Expand(job.Url, job.Cts.Token);
                Downloads.Remove(job);
                foreach (var u in urls) StartDownload(u, mode);
            });
            return;
        }
        _ = RunJob(job, DlSlots, "Analyse du lien…", () => Downloader.Run(job, Settings));
    }

    // ---------- historique (tâches terminées, gardées d'une session à l'autre) ----------

    record Past(string Title, string Output, string Badge, bool Download);
    static string HistoryPath => Path.Combine(Settings.Dir, "history.json");
    const int HistoryMax = 100; // par liste

    /// <summary>Relit les tâches terminées dont le fichier existe encore. « Effacer les terminées » et la croix d'une tâche les retirent aussi du fichier.</summary>
    void LoadHistory()
    {
        try
        {
            foreach (var p in System.Text.Json.JsonSerializer.Deserialize<List<Past>>(File.ReadAllText(HistoryPath)) ?? new())
                if (File.Exists(p.Output) || Directory.Exists(p.Output))
                    (p.Download ? Downloads : Conversions).Add(new Job
                        { Title = p.Title, BadgeText = p.Badge, Output = p.Output, Progress = 100, State = JobState.Done, Status = "Terminé" });
        }
        catch { /* pas encore d'historique, ou fichier abîmé : on repart de zéro */ }
        Conversions.CollectionChanged += (_, e) => { if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Add) SaveHistory(); };
        Downloads.CollectionChanged += (_, e) => { if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Add) SaveHistory(); };
    }

    void SaveHistory()
    {
        try
        {
            var all = new[] { (Conversions, false), (Downloads, true) }.SelectMany(l => l.Item1.Where(j => j.IsDone && j.Output != null)
                .Take(HistoryMax).Select(j => new Past(j.Title, j.Output, j.Badge, l.Item2))).ToList();
            Directory.CreateDirectory(Settings.Dir);
            File.WriteAllText(HistoryPath, System.Text.Json.JsonSerializer.Serialize(all));
        }
        catch (Exception ex) { Log("historique : " + ex); }
    }

    /// <summary>Relance une tâche échouée ou annulée, à la même place dans la liste.</summary>
    public void Retry(Job job)
    {
        var list = Downloads.Contains(job) ? Downloads : Conversions;
        int at = list.IndexOf(job);
        if (at < 0 || job.IsActive) return;
        var inputs = job.Inputs.Where(File.Exists).ToList();
        if (job.Url == null && inputs.Count < job.Inputs.Count) { job.Detail = "Fichier introuvable : déplacé ou supprimé ?"; return; } // pas d'assemblage incomplet
        list.RemoveAt(at);
        int before = list.Count;
        if (job.Url != null) StartDownload(job.Url, job.DownloadMode);
        else StartConversions(inputs, job.TargetId ?? job.Target.Id, job.Edits);
        if (list.Count == before + 1 && at > 0) list.Move(0, at); // la nouvelle tâche reprend la place de l'ancienne
    }

    async Task RunJob(Job job, SemaphoreSlim slots, string runningStatus, Func<Task> work)
    {
        try
        {
            await slots.WaitAsync(job.Cts.Token);
            try
            {
                job.State = JobState.Running;
                job.Status = runningStatus;
                if (job.Url != null) job.Indeterminate = true;
                await work();
                job.Progress = 100; job.Indeterminate = false;
                job.State = JobState.Done; job.Status = "Terminé";
                if (job.Output != null) { SaveHistory(); NotifyDone(job); } // sans fichier : la tâche qui a lu une liste
            }
            finally { slots.Release(); }
        }
        catch (OperationCanceledException) { job.Indeterminate = false; job.State = JobState.Canceled; job.Status = "Annulé"; }
        catch (Exception ex)
        {
            Log(ex.ToString());
            job.Indeterminate = false; job.State = JobState.Error; job.Status = "Échec"; job.Detail = ex.Message;
            if (Settings.Notify && !(_win?.IsActive ?? false))
                _ = Dispatcher.BeginInvoke(() =>
                {
                    _lastNotified = null; // un clic ouvre Prisme, pas le fichier d'une tâche précédente
                    _tray.ShowBalloonTip(4000, "Prisme — échec", $"{job.Title}\n{ex.Message}", Forms.ToolTipIcon.Error);
                });
        }
    }

    string _lastNotified;
    void NotifyDone(Job job)
    {
        if (!Settings.Notify || (_win?.IsActive ?? false)) return;
        _lastNotified = job.Output;
        var what = job.Url != null ? "Téléchargement terminé" : "Conversion terminée";
        Dispatcher.BeginInvoke(() => _tray.ShowBalloonTip(4000, what, Path.GetFileName(job.Output) + "\nCliquer pour l'afficher", Forms.ToolTipIcon.None));
    }

    public static void Reveal(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (File.Exists(path)) Process.Start("explorer.exe", $"/select,\"{path}\"");
        else if (Directory.Exists(path)) Process.Start("explorer.exe", $"\"{path}\"");
    }

    // ---------- zone de notification ----------

    void CreateTray()
    {
        var res = GetResourceStream(new Uri("pack://application:,,,/Assets/prisme.ico"));
        _tray = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(res.Stream, Forms.SystemInformation.SmallIconSize),
            Text = "Prisme", Visible = true,
        };
        var menu = new Forms.ContextMenuStrip { Renderer = new DarkMenu(), ShowImageMargin = false, Font = new System.Drawing.Font("Segoe UI", 10f) };
        menu.Items.Add("Ouvrir Prisme", null, (_, _) => ShowMain());
        menu.Items.Add("Télécharger le lien copié", null, (_, _) =>
        {
            ShowMain();
            string t = "";
            try { if (Clipboard.ContainsText()) t = Clipboard.GetText().Trim(); } catch { /* presse-papiers tenu par une autre appli */ }
            _win.ShowDownload(Downloader.IsUrl(t) ? t : null);
        });
        menu.Items.Add("Réglages", null, (_, _) => { ShowMain(); _win.GoTo("settings"); });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quitter", null, (_, _) => Quit());
        foreach (Forms.ToolStripItem i in menu.Items) { i.ForeColor = System.Drawing.Color.FromArgb(244, 243, 255); i.Padding = new Forms.Padding(6, 5, 6, 5); }
        _tray.ContextMenuStrip = menu;
        _tray.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ShowMain(); };
        _tray.BalloonTipClicked += (_, _) => { if (_lastNotified != null) Reveal(_lastNotified); else ShowMain(); };
    }

    public void ShowMain()
    {
        if (_win == null) return;
        if (!_win.IsVisible) _win.Show();
        if (_win.WindowState == WindowState.Minimized) _win.WindowState = WindowState.Normal;
        _win.Activate();
        _win.Topmost = true; _win.Topmost = false; // passe devant les autres fenêtres
    }

    bool _trayHintShown;
    public void HideToTray()
    {
        _win.Hide();
        if (!_trayHintShown)
        {
            _trayHintShown = true;
            _lastNotified = null;
            _tray.ShowBalloonTip(3000, "Prisme reste disponible", "Il est rangé ici, près de l'horloge. Clic droit pour quitter.", Forms.ToolTipIcon.None);
        }
    }

    public bool Exiting => _exiting;

    public void Quit()
    {
        var running = Conversions.Concat(Downloads).Where(j => j.IsActive).ToList();
        if (running.Count > 0)
        {
            ShowMain();
            if (MessageBox.Show(_win, $"{running.Count} tâche(s) en cours seront annulées. Quitter quand même ?", "Prisme",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            running.ForEach(j => j.Cts.Cancel());
        }
        if (UpdateReady && Settings.AutoUpdate) { ApplyUpdate("none"); return; } // on quitte : la mise à jour s'installe sans relancer Prisme
        _exiting = true;
        _tray.Visible = false; _tray.Dispose();
        _win?.Close();
        Shutdown(0);
    }

    static readonly object LogLock = new();
    public static void Log(string msg)
    {
        lock (LogLock) try
        {
            Directory.CreateDirectory(Settings.Dir);
            var path = Path.Combine(Settings.Dir, "prisme.log");
            // au-delà de 1 Mo, l'ancien journal est gardé à côté (un seul) et on repart de zéro
            if (new FileInfo(path) is { Exists: true, Length: > 1_000_000 }) File.Move(path, Path.Combine(Settings.Dir, "prisme.old.log"), true);
            File.AppendAllText(path,$"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\n");
        }
        catch { }
    }

    /// <summary>Menu de la zone de notification aux couleurs de Prisme.</summary>
    class DarkMenu : Forms.ToolStripProfessionalRenderer
    {
        public DarkMenu() : base(new Colors()) { RoundedEdges = true; }
        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e) { e.TextColor = System.Drawing.Color.FromArgb(244, 243, 255); base.OnRenderItemText(e); }
        class Colors : Forms.ProfessionalColorTable
        {
            static readonly System.Drawing.Color Bg = System.Drawing.Color.FromArgb(21, 21, 35), Hi = System.Drawing.Color.FromArgb(35, 35, 57), Line = System.Drawing.Color.FromArgb(58, 58, 92);
            public override System.Drawing.Color ToolStripDropDownBackground => Bg;
            public override System.Drawing.Color MenuBorder => Line;
            public override System.Drawing.Color MenuItemBorder => Hi;
            public override System.Drawing.Color MenuItemSelected => Hi;
            public override System.Drawing.Color SeparatorDark => Line;
            public override System.Drawing.Color SeparatorLight => Bg;
            public override System.Drawing.Color ImageMarginGradientBegin => Bg;
            public override System.Drawing.Color ImageMarginGradientMiddle => Bg;
            public override System.Drawing.Color ImageMarginGradientEnd => Bg;
        }
    }
}
