using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AssetsCraft.Core.Sprites;

namespace AssetsCraft.Desktop.Views;

/// <summary>
/// Pixel-perfect sprite viewer: plays one animation of a sheet (nearest-neighbour, integer zoom) over a
/// checkerboard, or shows the whole sheet with frame boundaries. The image is read into memory, so the
/// file is never locked (versions can be deleted while shown).
/// </summary>
public sealed class SpriteCanvas : Control
{
    public static readonly StyledProperty<SpriteSheet?> SheetProperty =
        AvaloniaProperty.Register<SpriteCanvas, SpriteSheet?>(nameof(Sheet));

    public static readonly StyledProperty<SpriteAnimation?> AnimationProperty =
        AvaloniaProperty.Register<SpriteCanvas, SpriteAnimation?>(nameof(Animation));

    public static readonly StyledProperty<bool> IsPlayingProperty =
        AvaloniaProperty.Register<SpriteCanvas, bool>(nameof(IsPlaying), true);

    /// <summary>Position inside the animation's playback sequence (two-way: the view model steps it).</summary>
    public static readonly StyledProperty<int> PositionProperty =
        AvaloniaProperty.Register<SpriteCanvas, int>(nameof(Position), defaultBindingMode: BindingMode.TwoWay);

    /// <summary>Integer zoom; 0 = biggest integer zoom that fits.</summary>
    public static readonly StyledProperty<int> ZoomProperty =
        AvaloniaProperty.Register<SpriteCanvas, int>(nameof(Zoom));

    public static readonly StyledProperty<double> SpeedProperty =
        AvaloniaProperty.Register<SpriteCanvas, double>(nameof(Speed), 1.0);

    /// <summary>"xadrez" | "escuro" | "claro" | "verde".</summary>
    public static readonly StyledProperty<string> BackdropProperty =
        AvaloniaProperty.Register<SpriteCanvas, string>(nameof(Backdrop), "xadrez");

    /// <summary>Whole sheet with frame boundaries instead of the animation.</summary>
    public static readonly StyledProperty<bool> ShowSheetProperty =
        AvaloniaProperty.Register<SpriteCanvas, bool>(nameof(ShowSheet));

    /// <summary>Pixel grid (only drawn when each pixel is at least 6 px on screen).</summary>
    public static readonly StyledProperty<bool> ShowGridProperty =
        AvaloniaProperty.Register<SpriteCanvas, bool>(nameof(ShowGrid));

    public SpriteSheet? Sheet { get => GetValue(SheetProperty); set => SetValue(SheetProperty, value); }
    public SpriteAnimation? Animation { get => GetValue(AnimationProperty); set => SetValue(AnimationProperty, value); }
    public bool IsPlaying { get => GetValue(IsPlayingProperty); set => SetValue(IsPlayingProperty, value); }
    public int Position { get => GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public int Zoom { get => GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    public double Speed { get => GetValue(SpeedProperty); set => SetValue(SpeedProperty, value); }
    public string Backdrop { get => GetValue(BackdropProperty); set => SetValue(BackdropProperty, value); }
    public bool ShowSheet { get => GetValue(ShowSheetProperty); set => SetValue(ShowSheetProperty, value); }
    public bool ShowGrid { get => GetValue(ShowGridProperty); set => SetValue(ShowGridProperty, value); }

    /// <summary>Zoom actually used in the last render (for the "8x" label).</summary>
    public int EffectiveZoom { get; private set; } = 1;
    public event Action<int>? EffectiveZoomChanged;

    private static readonly IBrush Dark = new SolidColorBrush(Color.FromRgb(0x3a, 0x3a, 0x3e));
    private static readonly IBrush Light = new SolidColorBrush(Color.FromRgb(0x4a, 0x4a, 0x50));
    private static readonly IBrush Grid = new SolidColorBrush(Color.FromArgb(0x30, 0xff, 0xff, 0xff));
    private static readonly IPen GridPen = new Pen(Grid, 1);
    private static readonly IPen FramePen = new Pen(new SolidColorBrush(Color.FromArgb(0xb0, 0xff, 0x2d, 0xb4)), 1);
    private static readonly IPen CurrentPen = new Pen(new SolidColorBrush(Color.FromRgb(0xd4, 0xff, 0x3a)), 2);

    private readonly DispatcherTimer _timer;
    private Bitmap? _bitmap;
    private string? _loadedPath;
    private DateTime _loadedStamp;

    static SpriteCanvas()
    {
        AffectsRender<SpriteCanvas>(SheetProperty, AnimationProperty, PositionProperty, ZoomProperty, BackdropProperty,
            ShowSheetProperty, ShowGridProperty);
    }

    public SpriteCanvas()
    {
        ClipToBounds = true;
        _timer = new DispatcherTimer(DispatcherPriority.Render);
        _timer.Tick += (_, _) => Advance();
    }

    private IReadOnlyList<int> Sequence => Animation?.Sequence() ?? (Sheet is { Frames.Count: > 0 } s ? Enumerable.Range(0, s.Frames.Count).ToList() : []);

    /// <summary>Frame (index into the sheet) currently displayed.</summary>
    public int CurrentFrameIndex
    {
        get
        {
            var seq = Sequence;
            return seq.Count == 0 ? -1 : seq[Math.Clamp(Position, 0, seq.Count - 1)];
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SheetProperty)
        {
            LoadBitmap();
            Position = 0;
            Schedule();
        }
        else if (change.Property == AnimationProperty)
        {
            Position = 0;
            Schedule();
        }
        else if (change.Property == IsPlayingProperty || change.Property == SpeedProperty || change.Property == PositionProperty)
        {
            Schedule();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Schedule();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void LoadBitmap()
    {
        var path = Sheet?.ImagePath;
        if (path is null || !File.Exists(path))
        {
            _bitmap?.Dispose();
            _bitmap = null;
            _loadedPath = null;
            return;
        }
        var stamp = File.GetLastWriteTimeUtc(path);
        if (path == _loadedPath && stamp == _loadedStamp && _bitmap is not null) return;
        try
        {
            var bytes = File.ReadAllBytes(path);
            using var ms = new MemoryStream(bytes);
            var bmp = new Bitmap(ms);
            _bitmap?.Dispose();
            _bitmap = bmp;
            _loadedPath = path;
            _loadedStamp = stamp;
        }
        catch
        {
            _bitmap = null;
            _loadedPath = null;
        }
    }

    private void Schedule()
    {
        _timer.Stop();
        if (!IsPlaying || ShowSheet || Sheet is null || VisualRoot is null) return;
        var seq = Sequence;
        if (seq.Count < 2) return;
        var frame = Sheet.Frames[CurrentFrameIndex];
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(10, frame.DurationMs / Math.Max(0.05, Speed)));
        _timer.Start();
    }

    private void Advance()
    {
        var seq = Sequence;
        if (seq.Count == 0) return;
        var next = Position + 1;
        if (next >= seq.Count)
        {
            if (Animation is { Loop: false })
            {
                IsPlaying = false; // one-shot animation: stop on the last frame
                return;
            }
            next = 0;
        }
        Position = next; // re-schedules with the next frame's duration
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        DrawBackdrop(context, bounds);
        if (_bitmap is null || Sheet is null) return;

        Rect src;
        if (ShowSheet) src = new Rect(0, 0, _bitmap.PixelSize.Width, _bitmap.PixelSize.Height);
        else
        {
            var idx = CurrentFrameIndex;
            if (idx < 0) return;
            var f = Sheet.Frames[idx];
            src = new Rect(f.X, f.Y, f.W, f.H);
        }

        var zoom = Zoom > 0 ? Zoom : FitZoom(src.Size, bounds.Size);
        if (zoom != EffectiveZoom)
        {
            EffectiveZoom = zoom;
            // Render pass não pode invalidar visuais: assinantes (ex.: TextBlock) rodam depois.
            Dispatcher.UIThread.Post(() => EffectiveZoomChanged?.Invoke(zoom));
        }
        var size = new Size(src.Width * zoom, src.Height * zoom);
        var dest = new Rect(Math.Round((bounds.Width - size.Width) / 2), Math.Round((bounds.Height - size.Height) / 2), size.Width, size.Height);

        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
            context.DrawImage(_bitmap, src, dest);

        if (ShowGrid && zoom >= 6) DrawPixelGrid(context, dest, (int)src.Width, (int)src.Height, zoom);

        if (ShowSheet)
        {
            foreach (var f in Sheet.Frames)
                context.DrawRectangle(null, FramePen, new Rect(dest.X + f.X * zoom + 0.5, dest.Y + f.Y * zoom + 0.5, f.W * zoom - 1, f.H * zoom - 1));
            if (Animation is { } a)
                for (var i = a.From; i <= a.To; i++)
                {
                    var f = Sheet.Frames[i];
                    if (i == CurrentFrameIndex)
                        context.DrawRectangle(null, CurrentPen, new Rect(dest.X + f.X * zoom, dest.Y + f.Y * zoom, f.W * zoom, f.H * zoom));
                }
        }
    }

    private static int FitZoom(Size content, Size room)
    {
        if (content.Width <= 0 || content.Height <= 0) return 1;
        var z = Math.Floor(Math.Min((room.Width - 16) / content.Width, (room.Height - 16) / content.Height));
        return (int)Math.Clamp(z, 1, 64);
    }

    private void DrawBackdrop(DrawingContext context, Rect bounds)
    {
        switch (Backdrop)
        {
            case "escuro":
                context.FillRectangle(new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x12)), bounds);
                return;
            case "claro":
                context.FillRectangle(new SolidColorBrush(Color.FromRgb(0xe8, 0xe8, 0xea)), bounds);
                return;
            case "verde":
                context.FillRectangle(new SolidColorBrush(Color.FromRgb(0x5d, 0x8a, 0x4a)), bounds);
                return;
        }
        context.FillRectangle(Dark, bounds);
        const int cell = 10;
        for (var y = 0; y * cell < bounds.Height; y++)
            for (var x = y % 2; x * cell < bounds.Width; x += 2)
                context.FillRectangle(Light, new Rect(x * cell, y * cell, cell, cell));
    }

    private static void DrawPixelGrid(DrawingContext context, Rect dest, int w, int h, int zoom)
    {
        for (var x = 1; x < w; x++)
            context.DrawLine(GridPen, new Point(dest.X + x * zoom + 0.5, dest.Y), new Point(dest.X + x * zoom + 0.5, dest.Bottom));
        for (var y = 1; y < h; y++)
            context.DrawLine(GridPen, new Point(dest.X, dest.Y + y * zoom + 0.5), new Point(dest.Right, dest.Y + y * zoom + 0.5));
    }
}
