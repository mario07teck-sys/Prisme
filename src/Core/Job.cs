using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Prisme.Core;

public enum JobState { Waiting, Running, Done, Error, Canceled }

/// <summary>Retouche avant conversion. Extrait en secondes (End = 0 : jusqu'à la fin) ;
/// recadrage en fractions de l'image (indépendant de sa résolution) ; taille en pixels (0 = automatique).</summary>
/// <remarks>Son et vidéo : rotation en degrés dans le sens des aiguilles (90, 180, 270), volume (1 = inchangé), vitesse (0,5 à 2).</remarks>
public record Edit(double Start, double End, double CropX, double CropY, double CropW, double CropH, int Width, int Height,
    int Rotate = 0, double Volume = 1, double Speed = 1)
{
    public static readonly Edit None = new(0, 0, 0, 0, 1, 1, 0, 0);
    public bool Trims => Start > 0 || End > 0;
    public bool Crops => CropW < 0.9999 || CropH < 0.9999;
    public bool Resizes => Width > 0 || Height > 0;
    public bool Filters => Rotate != 0 || Volume != 1 || Speed != 1;
    public bool IsEmpty => !Trims && !Crops && !Resizes && !Filters;

    /// <summary>Filtre FFmpeg du recadrage puis de la taille ("" si rien).</summary>
    public string ImageFilter()
    {
        var f = new List<string>();
        if (Crops) f.Add(FormattableString.Invariant(
            $"crop=trunc(iw*{CropW:0.######}/2)*2:trunc(ih*{CropH:0.######}/2)*2:trunc(iw*{CropX:0.######}):trunc(ih*{CropY:0.######})"));
        if (Resizes) f.Add($"scale={(Width > 0 ? Width : -2)}:{(Height > 0 ? Height : -2)}:flags=lanczos");
        return string.Join(",", f);
    }

    public string Label => string.Join(" · ", new[]
    {
        Trims ? $"✂ {Time(Start)} → {(End > 0 ? Time(End) : "fin")}" : null,
        Crops ? "recadrée" : null,
        Resizes ? (Width > 0 && Height > 0 ? $"{Width}×{Height}" : Width > 0 ? $"{Width} px de large" : $"{Height} px de haut") : null,
        Rotate != 0 ? $"↻ {Rotate}°" : null,
        Volume != 1 ? $"volume {Volume * 100:0} %" : null,
        Speed != 1 ? $"vitesse ×{Speed:0.##}" : null,
    }.Where(s => s != null));

    /// <summary>Filtres FFmpeg vidéo et audio de la rotation, de la vitesse et du volume ("" si rien).</summary>
    public (string Video, string Audio) MediaFilters()
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var v = new List<string>(); var a = new List<string>();
        if (Rotate == 90) v.Add("transpose=1"); else if (Rotate == 180) v.Add("hflip,vflip"); else if (Rotate == 270) v.Add("transpose=2");
        if (Speed != 1) { v.Add(string.Format(inv, "setpts=PTS/{0:0.###}", Speed)); a.Add(string.Format(inv, "atempo={0:0.###}", Speed)); }
        if (Volume != 1) a.Add(string.Format(inv, "volume={0:0.###}", Volume));
        return (string.Join(",", v), string.Join(",", a));
    }

    /// <summary>1:05, 1:02:03 ou 0:07.5 (dixièmes seulement s'il y en a).</summary>
    public static string Time(double s)
    {
        s = Math.Max(0, Math.Round(s, 1));
        var t = TimeSpan.FromSeconds(s);
        var core = t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        return t.Milliseconds >= 50 ? $"{core}.{t.Milliseconds / 100}" : core;
    }

    /// <summary>Lit « 90 », « 1:30 », « 1:30.5 », « 1:02:03 » ou « 1,5 ».</summary>
    public static bool TryParseTime(string text, out double seconds)
    {
        seconds = 0;
        var parts = (text ?? "").Trim().Replace(',', '.').Split(':');
        if (parts.Length is 0 or > 3 || parts.Any(p => p.Length == 0)) return false;
        foreach (var p in parts)
        {
            if (!double.TryParse(p, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) || v < 0) return false;
            seconds = seconds * 60 + v;
        }
        return true;
    }
}

/// <summary>Une tâche affichée dans la file : conversion ou téléchargement.</summary>
public class Job : INotifyPropertyChanged
{
    public List<string> Inputs { get; init; } = new();
    public Target Target { get; init; }
    public string TargetId { get; init; }      // format demandé (« same », « merge-mp3 »…) : sert à relancer la tâche
    public string Url { get; init; }           // téléchargement
    public string DownloadMode { get; init; }   // best | 1080 | 720 | mp3
    public CancellationTokenSource Cts { get; } = new();
    public DateTime Created { get; } = DateTime.Now;
    /// <summary>Retouches par fichier d'entrée (extrait, recadrage, taille) ; absent = aucune.</summary>
    public IReadOnlyDictionary<string, Edit> Edits { get; init; }
    public Edit EditOf(string input) => Edits != null && Edits.TryGetValue(input, out var e) ? e : Edit.None;

    string _title, _status = "En attente", _output, _detail;
    double _progress; bool _indeterminate; JobState _state;

    public string Title { get => _title; set => Set(ref _title, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string Detail { get => _detail; set => Set(ref _detail, value); }
    public string Output { get => _output; set => Set(ref _output, value); }
    public double Progress { get => _progress; set => Set(ref _progress, Math.Clamp(value, 0, 100)); }
    public bool Indeterminate { get => _indeterminate; set => Set(ref _indeterminate, value); }
    public JobState State
    {
        get => _state;
        set { Set(ref _state, value); OnChanged(nameof(IsActive)); OnChanged(nameof(IsDone)); OnChanged(nameof(IsError)); }
    }
    public bool IsActive => State is JobState.Waiting or JobState.Running;
    public bool IsDone => State == JobState.Done;
    public bool IsError => State is JobState.Error or JobState.Canceled;
    public string BadgeText { get; init; }      // tâche relue de l'historique : le format n'est plus connu que par son nom
    public string Badge => BadgeText ?? Target?.Label ?? (DownloadMode == "mp3" ? "MP3" : "MP4");

    public event PropertyChangedEventHandler PropertyChanged;
    void OnChanged(string n) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    void Set<T>(ref T f, T v, [CallerMemberName] string n = null)
    {
        if (EqualityComparer<T>.Default.Equals(f, v)) return;
        f = v;
        // les tâches tournent en arrière-plan : on notifie l'interface sur son propre fil
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d != null && !d.CheckAccess()) d.BeginInvoke(() => OnChanged(n)); else OnChanged(n);
    }
}
