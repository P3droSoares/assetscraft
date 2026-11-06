using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using AssetsCraft.Core.Projects;

namespace AssetsCraft.Desktop.ViewModels;

/// <summary>One color of the palette preview.</summary>
public sealed record Swatch(string Hex)
{
    public string Color => "#" + Hex;
}

/// <summary>"Configurações do projeto": settings that apply to everything made for one game (today: the palette).</summary>
public sealed partial class GameSettingsViewModel : ObservableObject
{
    private readonly GameStore _store;
    private readonly Action _changed;

    public GameSettingsViewModel(GameProject game, GameStore store, Action changed)
    {
        Game = game;
        _store = store;
        _changed = changed;
        Refresh();
    }

    public GameProject Game { get; }
    public string GameName => Game.Name;
    public string GamePath => Game.Path;

    public ObservableCollection<Swatch> Swatches { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoPalette), nameof(PaletteTitle), nameof(UploadLabel))]
    private bool _hasPalette;

    [ObservableProperty] private string? _error;

    public bool HasNoPalette => !HasPalette;
    public string PaletteTitle => Game.Palette is { } p ? $"{p.Name} · {p.Colors.Count} {(p.Colors.Count == 1 ? "cor" : "cores")}" : "Sem paleta: cores livres";
    public string UploadLabel => HasPalette ? "Trocar paleta…" : "Enviar paleta…";

    private void Refresh()
    {
        Swatches.Clear();
        foreach (var c in Game.Palette?.Colors ?? []) Swatches.Add(new Swatch(c));
        HasPalette = Game.Palette is not null;
        OnPropertyChanged(nameof(PaletteTitle));
    }

    /// <summary>Loads a palette file (image or text) and makes it the game's palette.</summary>
    public async Task ImportAsync(string path)
    {
        try
        {
            var palette = await Task.Run(() => Load(path));
            _store.SetPalette(Game, palette);
            Error = null;
            Refresh();
            _changed();
        }
        catch (Exception ex)
        {
            Error = $"Não foi possível usar este arquivo: {ex.Message}";
        }
    }

    public void Remove()
    {
        _store.SetPalette(Game, null);
        Error = null;
        Refresh();
        _changed();
    }

    public static GamePalette Load(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" or ".gif" or ".bmp" or ".jpg" or ".jpeg" or ".webp" => FromImage(path),
        _ => GamePalette.FromTextFile(path),
    };

    /// <summary>
    /// Every distinct opaque color of the image, in reading order (left→right, top→bottom), so palette strips
    /// from lospec.com keep their order. Semi-transparent pixels are ignored.
    /// </summary>
    private static GamePalette FromImage(string path)
    {
        using var stream = File.OpenRead(path);
        using var bmp = new Bitmap(stream);
        var size = bmp.PixelSize;
        if ((long)size.Width * size.Height > 4_000_000) throw new InvalidDataException("Imagem grande demais para uma paleta.");
        var stride = size.Width * 4;
        var buffer = new byte[stride * size.Height];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            bmp.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), buffer.Length, stride);
        }
        finally
        {
            handle.Free();
        }
        var bgra = bmp.Format is not { } f || f == PixelFormat.Bgra8888;
        var seen = new HashSet<int>();
        var colors = new List<string>();
        for (var i = 0; i < buffer.Length; i += 4)
        {
            if (buffer[i + 3] < 255) continue;
            byte r = bgra ? buffer[i + 2] : buffer[i], g = buffer[i + 1], b = bgra ? buffer[i] : buffer[i + 2];
            if (!seen.Add((r << 16) | (g << 8) | b)) continue;
            colors.Add(GamePalette.Hex(r, g, b));
            if (colors.Count > GamePalette.MaxColors)
                throw new InvalidDataException($"a imagem tem mais de {GamePalette.MaxColors} cores. Use um arquivo de paleta " +
                                               "(como os do lospec.com), não um sprite ou screenshot.");
        }
        return GamePalette.Create(Path.GetFileNameWithoutExtension(path), colors);
    }
}
