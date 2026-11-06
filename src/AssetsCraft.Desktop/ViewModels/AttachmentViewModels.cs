using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using AssetsCraft.Core.Sessions;

namespace AssetsCraft.Desktop.ViewModels;

/// <summary>A file waiting in the composer to be sent (inspiration, or material for the sprite).</summary>
public sealed partial class PendingAttachment : ObservableObject
{
    public PendingAttachment(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        Kind = ProjectSession.ReferenceKind(path);
        Badge = BadgeFor(Kind);
        try { SizeText = FormatSize(new FileInfo(path).Length); } catch { SizeText = ""; }
        if (Kind == "imagem") _ = LoadThumbnailAsync();
    }

    public string Path { get; }
    public string Name { get; }
    public string Kind { get; }
    public string Badge { get; }
    public string SizeText { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail))]
    private Bitmap? _thumbnail;

    public bool HasThumbnail => Thumbnail is not null;

    /// <summary>
    /// Off = inspiration only (style, palette, poses). On = material used for real in the sprite (edit, crop,
    /// animate, reuse the palette): it goes to assets/ instead of references/.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UseLabel))]
    private bool _useAsMaterial;

    public string UseLabel => UseAsMaterial ? "usar no sprite" : "só inspiração";
    public bool CanBeMaterial => Kind is "imagem" or "arquivo";

    private async Task LoadThumbnailAsync()
    {
        try
        {
            var bmp = await Task.Run(() =>
            {
                using var s = File.OpenRead(Path);
                return Bitmap.DecodeToWidth(s, 64);
            });
            Avalonia.Threading.Dispatcher.UIThread.Post(() => Thumbnail = bmp);
        }
        catch
        {
            // unsupported format (svg, bmp variants): keep the badge
        }
    }

    public static string BadgeFor(string kind) => kind switch
    {
        "imagem" => "IMG",
        "vídeo" => "VID",
        "áudio" => "AUD",
        "pdf" => "PDF",
        "texto" => "TXT",
        _ => "ARQ",
    };

    public static string FormatSize(long bytes)
    {
        const double kb = 1024, mb = kb * 1024, gb = mb * 1024;
        if (bytes >= gb) return $"{bytes / gb:0.##} GB";
        if (bytes >= mb) return $"{bytes / mb:0.#} MB";
        if (bytes >= kb) return $"{bytes / kb:0.#} KB";
        return $"{bytes} B";
    }
}

/// <summary>Attachment shown inside a sent user message.</summary>
public sealed record SentAttachment(string Name, string Badge);

/// <summary>Entry of the per-conversation model picker.</summary>
public sealed record ModelChoice(AssetsCraft.Core.Claude.ModelOption Option, bool IsSelected)
{
    public string Label => Option.Label;
    public string Description => Option.Description;
}

