namespace Prisme.Core;

public enum Kind { Audio, Video, Image, Pdf, Text, Sheet, Slides, Drawing, Subtitle, Unknown }

/// <summary>Un format de sortie. Id = ce qu'on passe en ligne de commande (--to mp3).</summary>
public record Target(string Id, string Label, string Ext, string Hint = "");

public static class Formats
{
    public static readonly string[] AudioExts = { "mp3", "wav", "flac", "ogg", "oga", "m4a", "aac", "wma", "opus", "aiff", "aif", "amr", "ac3", "eac3", "dts", "ape", "caf", "m4r", "mka", "wv", "au", "mp2" };
    public static readonly string[] VideoExts = { "mp4", "mkv", "avi", "mov", "webm", "wmv", "flv", "m4v", "3gp", "3g2", "mpg", "mpeg", "ts", "mts", "m2ts", "ogv", "vob", "asf", "rm", "rmvb", "divx", "f4v", "mxf" };
    public static readonly string[] ImageExts = { "png", "jpg", "jpeg", "jfif", "webp", "bmp", "gif", "tif", "tiff", "ico", "heic", "heif", "avif", "tga", "psd", "dds", "qoi", "exr", "jp2" };
    public static readonly string[] TextExts = { "doc", "docx", "docm", "dotx", "odt", "rtf", "txt", "md", "html", "htm", "wpd", "wps", "pages" };
    public static readonly string[] SheetExts = { "xls", "xlsx", "xlsm", "ods", "csv", "tsv", "numbers" };
    public static readonly string[] SlidesExts = { "ppt", "pptx", "pps", "ppsx", "odp", "key" };
    public static readonly string[] DrawingExts = { "svg", "vsd", "vsdx", "pub", "cdr", "emf", "wmf", "odg" };
    public static readonly string[] SubtitleExts = { "srt", "vtt", "ass", "ssa" };

    public static IEnumerable<string> AllExts => AudioExts.Concat(VideoExts).Concat(ImageExts).Concat(TextExts).Concat(SheetExts)
        .Concat(SlidesExts).Concat(DrawingExts).Concat(SubtitleExts).Append("pdf");

    /// <summary>Documents traités par le moteur bureautique (Office / LibreOffice).</summary>
    public static bool IsOffice(Kind k) => k is Kind.Text or Kind.Sheet or Kind.Slides or Kind.Drawing;

    static readonly Target[] AudioTargets =
    {
        new("mp3", "MP3", "mp3", "Le plus compatible"),
        new("wav", "WAV", "wav", "Sans perte, lourd"),
        new("flac", "FLAC", "flac", "Sans perte, compressé"),
        new("m4a", "M4A", "m4a", "AAC, Apple"),
        new("alac", "ALAC", "m4a", "Sans perte, Apple"),
        new("ogg", "OGG", "ogg", "Vorbis"),
        new("opus", "OPUS", "opus", "Petit et net"),
        new("aiff", "AIFF", "aiff", "Sans perte, Mac"),
        new("wma", "WMA", "wma", "Windows Media"),
        new("ac3", "AC3", "ac3", "Dolby Digital"),
        new("m4r", "Sonnerie", "m4r", "iPhone, 40 s max"),
        new("mp4", "MP4", "mp4", "Vidéo fond noir"),
    };
    static readonly Target[] VideoTargets =
    {
        new("mp4", "MP4", "mp4", "Le plus compatible"),
        new("mp4-small", "MP4 léger", "mp4", "Compresse la vidéo"),
        new("mp4-hevc", "MP4 H.265", "mp4", "Même qualité, 2× plus léger"),
        new("mkv", "MKV", "mkv"),
        new("webm", "WEBM", "webm", "Pour le web"),
        new("mov", "MOV", "mov", "Apple"),
        new("avi", "AVI", "avi"),
        new("wmv", "WMV", "wmv", "Windows Media"),
        new("mpg", "MPEG", "mpg", "Vieux lecteurs, DVD"),
        new("gif", "GIF", "gif", "Animation"),
        new("webp", "WEBP animé", "webp", "Animation légère"),
        new("mp3", "MP3", "mp3", "Extraire le son"),
        new("wav", "WAV", "wav", "Extraire le son"),
        new("m4a", "M4A", "m4a", "Extraire le son"),
        new("flac", "FLAC", "flac", "Extraire le son"),
    };
    static readonly Target[] ImageTargets =
    {
        new("pdf", "PDF", "pdf", "Plusieurs images = un seul PDF"),
        new("png", "PNG", "png", "Sans perte"),
        new("jpg", "JPG", "jpg", "Léger"),
        new("webp", "WEBP", "webp", "Moderne, très léger"),
        new("avif", "AVIF", "avif", "Le plus léger"),
        new("gif", "GIF", "gif"),
        new("bmp", "BMP", "bmp"),
        new("tiff", "TIFF", "tiff", "Impression"),
        new("ico", "ICO", "ico", "Icône Windows"),
    };
    static readonly Target[] GifExtra =
    {
        new("mp4", "MP4", "mp4", "Animation → vidéo"),
        new("webm", "WEBM", "webm", "Animation → vidéo"),
    };
    static readonly Target[] PdfTargets =
    {
        new("docx", "Word", "docx", "Texte modifiable"),
        new("pptx", "PowerPoint", "pptx", "Une diapo par page"),
        new("png", "PNG", "png", "Une image par page"),
        new("jpg", "JPG", "jpg", "Une image par page"),
        new("txt", "Texte", "txt", "Texte brut"),
        new("pdf-split", "Séparer", "pdf", "Un PDF par page"),
        new("pdf-merge", "Fusionner", "pdf", "Plusieurs PDF = un seul"),
    };
    static readonly Target[] TextTargets =
    {
        new("pdf", "PDF", "pdf"),
        new("docx", "Word", "docx", "DOCX"),
        new("odt", "ODT", "odt", "LibreOffice"),
        new("rtf", "RTF", "rtf"),
        new("txt", "Texte", "txt", "Texte brut"),
        new("md", "Markdown", "md"),
        new("html", "HTML", "html", "Page web"),
        new("epub", "EPUB", "epub", "Livre numérique"),
        new("png-pages", "PNG", "png", "Une image par page"),
        new("jpg-pages", "JPG", "jpg", "Une image par page"),
    };
    static readonly Target[] SheetTargets =
    {
        new("pdf", "PDF", "pdf"),
        new("xlsx", "Excel", "xlsx", "XLSX"),
        new("ods", "ODS", "ods", "LibreOffice"),
        new("csv", "CSV", "csv", "Données, UTF-8"),
        new("html", "HTML", "html", "Page web"),
    };
    static readonly Target[] SlidesTargets =
    {
        new("pdf", "PDF", "pdf"),
        new("pptx", "PowerPoint", "pptx", "PPTX"),
        new("odp", "ODP", "odp", "LibreOffice"),
        new("png-pages", "PNG", "png", "Une image par diapo"),
        new("jpg-pages", "JPG", "jpg", "Une image par diapo"),
    };
    static readonly Target[] DrawingTargets =
    {
        new("pdf", "PDF", "pdf"),
        new("png", "PNG", "png"),
        new("jpg", "JPG", "jpg"),
    };
    static readonly Target[] SubtitleTargets =
    {
        new("srt", "SRT", "srt", "Le plus compatible"),
        new("vtt", "VTT", "vtt", "Web"),
        new("ass", "ASS", "ass", "Avec styles"),
    };

    public static string ExtOf(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

    public static Kind KindOf(string path)
    {
        var e = ExtOf(path);
        if (e == "pdf") return Kind.Pdf;
        if (AudioExts.Contains(e)) return Kind.Audio;
        if (VideoExts.Contains(e)) return Kind.Video;
        if (ImageExts.Contains(e)) return Kind.Image;
        if (TextExts.Contains(e)) return Kind.Text;
        if (SheetExts.Contains(e)) return Kind.Sheet;
        if (SlidesExts.Contains(e)) return Kind.Slides;
        if (DrawingExts.Contains(e)) return Kind.Drawing;
        if (SubtitleExts.Contains(e)) return Kind.Subtitle;
        return Kind.Unknown;
    }

    static IEnumerable<Target> ForKind(Kind k, string ext) => k switch
    {
        Kind.Audio => AudioTargets, Kind.Video => VideoTargets,
        Kind.Image => ext == "gif" ? ImageTargets.Concat(GifExtra) : ImageTargets,
        Kind.Pdf => PdfTargets, Kind.Text => TextTargets, Kind.Sheet => SheetTargets, Kind.Slides => SlidesTargets,
        Kind.Drawing => DrawingTargets, Kind.Subtitle => SubtitleTargets, _ => Array.Empty<Target>()
    };

    /// <summary>Formats proposés pour un fichier, sans son propre format (« mp3 → mp3 » n'a pas de sens).</summary>
    public static IEnumerable<Target> TargetsFor(string path)
    {
        var e = ExtOf(path);
        return ForKind(KindOf(path), e).Where(t => t.Id != SameId(e));
    }

    static string SameId(string e) => e switch { "jpeg" or "jfif" => "jpg", "tif" => "tiff", "aif" => "aiff", "htm" => "html", "mpeg" => "mpg", "oga" => "ogg", _ => e };

    /// <summary>« Même format » : proposé après une retouche (couper un MP3 et le garder en MP3).</summary>
    public static Target Same(string hint) => new("same", "Même format", "", hint);

    /// <summary>Le vrai format de sortie de « Même format » ; à défaut (AAC, FLV, HEIC…) le plus proche qui sait l'écrire.</summary>
    static Target SameAs(string path)
    {
        var k = KindOf(path);
        if (k is not (Kind.Audio or Kind.Video or Kind.Image)) return null;
        var e = ExtOf(path);
        var all = ForKind(k, e).ToList();
        return all.FirstOrDefault(t => t.Id == SameId(e))
            ?? all.First(t => t.Id == (k == Kind.Audio ? (e is "aac" or "m4b" ? "m4a" : "mp3") : k == Kind.Video ? "mp4" : e is "heic" or "heif" ? "jpg" : "png"));
    }

    /// <summary>Formats proposés pour une sélection : ceux communs à tous les fichiers.</summary>
    public static List<Target> TargetsFor(IEnumerable<string> paths)
    {
        List<Target> common = null;
        foreach (var p in paths)
        {
            var t = TargetsFor(p).ToList();
            common = common == null ? t : common.Where(c => t.Any(x => x.Id == c.Id)).ToList();
        }
        return common ?? new List<Target>();
    }

    public static Target Find(string path, string id) =>
        id == "same" ? SameAs(path)
        : id.StartsWith("merge-") ? (KindOf(path) == Kind.Audio ? MergeTargets.FirstOrDefault(t => t.Id == id) : null)
        : TargetsFor(path).FirstOrDefault(t => t.Id == id);

    /// <summary>Plusieurs sons → un seul fichier, l'un après l'autre (album, playlist).</summary>
    public static readonly Target[] MergeTargets =
    {
        new("merge-mp3", "MP3", "mp3", "Le plus compatible"),
        new("merge-m4a", "M4A", "m4a", "AAC, Apple"),
        new("merge-flac", "FLAC", "flac", "Sans perte"),
        new("merge-wav", "WAV", "wav", "Sans perte, lourd"),
    };

    /// <summary>Réserve un chemin de sortie libre (« nom.ext », sinon « nom (2).ext »…) en y créant un fichier vide :
    /// deux conversions simultanées ne peuvent pas viser le même fichier. L'appelant l'écrase ou le supprime en cas d'échec.</summary>
    public static string UniquePath(string dir, string name, string ext)
    {
        Directory.CreateDirectory(dir);
        for (int i = 1; ; i++)
        {
            var p = Path.Combine(dir, i == 1 ? $"{name}.{ext}" : $"{name} ({i}).{ext}");
            if (Directory.Exists(p)) continue;
            try { using (new FileStream(p, FileMode.CreateNew)) { } return p; }
            catch (IOException) when (File.Exists(p)) { }
        }
    }

    static readonly object DirLock = new();

    /// <summary>Crée un dossier de sortie libre : « nom », sinon « nom (2) »…</summary>
    public static string UniqueDir(string dir, string name)
    {
        lock (DirLock)
        {
            var p = Path.Combine(dir, name);
            for (int i = 2; File.Exists(p) || Directory.Exists(p); i++) p = Path.Combine(dir, $"{name} ({i})");
            Directory.CreateDirectory(p);
            return p;
        }
    }
}
