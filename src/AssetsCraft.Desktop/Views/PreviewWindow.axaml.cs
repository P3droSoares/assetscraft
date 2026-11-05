using Avalonia.Controls;
using Avalonia.Input;
using AssetsCraft.Core.Sprites;

namespace AssetsCraft.Desktop.Views;

/// <summary>Large pixel-perfect view of a sprite version: animations, frame stepping, zoom, sheet and grid.</summary>
public partial class PreviewWindow : Window
{
    private static readonly string[] Backdrops = ["xadrez", "escuro", "claro", "verde"];
    private readonly SpriteSheet? _sheet;

    public PreviewWindow() => InitializeComponent();

    public PreviewWindow(SpriteSheet sheet, string name, SpriteAnimation? animation) : this()
    {
        _sheet = sheet;
        Title = $"Ampliar · {name}";
        TitleText.Text = name;
        Canvas.Sheet = sheet;
        Canvas.Animation = animation ?? sheet.Animations.FirstOrDefault();
        Canvas.PropertyChanged += (_, e) =>
        {
            if (e.Property == SpriteCanvas.PositionProperty || e.Property == SpriteCanvas.AnimationProperty) UpdateInfo();
        };
        Canvas.EffectiveZoomChanged += _ => UpdateInfo();
        UpdateInfo();
    }

    private void UpdateInfo()
    {
        if (_sheet is null || Canvas.Animation is not { } a) return;
        var seq = a.Sequence();
        AnimText.Text = $"{a.Name} · {a.Fps:0.#} fps";
        InfoText.Text = (Canvas.ShowSheet ? "folha inteira" : $"quadro {Math.Clamp(Canvas.Position, 0, seq.Count - 1) + 1}/{seq.Count}") +
                        $" · {Canvas.EffectiveZoom}x";
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var a = Canvas.Animation;
        var count = a?.Sequence().Count ?? 0;
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                break;
            case Key.Space:
                if (!Canvas.IsPlaying && a is { Loop: false } && Canvas.Position >= count - 1) Canvas.Position = 0;
                Canvas.IsPlaying = !Canvas.IsPlaying;
                break;
            case Key.Left or Key.Right when count > 0:
                Canvas.IsPlaying = false;
                Canvas.Position = ((Canvas.Position + (e.Key == Key.Left ? -1 : 1)) % count + count) % count;
                break;
            case Key.Up or Key.Down when _sheet is { Animations.Count: > 0 } s:
                var i = a is null ? 0 : s.Animations.ToList().IndexOf(a);
                var n = s.Animations.Count;
                Canvas.Animation = s.Animations[((i + (e.Key == Key.Up ? -1 : 1)) % n + n) % n];
                Canvas.IsPlaying = true;
                break;
            case Key.OemPlus or Key.Add:
                Canvas.Zoom = Math.Min(64, Canvas.EffectiveZoom + 1);
                break;
            case Key.OemMinus or Key.Subtract:
                Canvas.Zoom = Math.Max(1, Canvas.EffectiveZoom - 1);
                break;
            case Key.D0 or Key.NumPad0:
                Canvas.Zoom = 0; // fit
                break;
            case Key.F:
                Canvas.ShowSheet = !Canvas.ShowSheet;
                break;
            case Key.G:
                Canvas.ShowGrid = !Canvas.ShowGrid;
                break;
            case Key.B:
                Canvas.Backdrop = Backdrops[(Array.IndexOf(Backdrops, Canvas.Backdrop) + 1) % Backdrops.Length];
                break;
            default:
                base.OnKeyDown(e);
                return;
        }
        e.Handled = true;
        UpdateInfo();
    }
}
