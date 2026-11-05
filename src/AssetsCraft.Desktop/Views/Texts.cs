using Avalonia.Data.Converters;

namespace AssetsCraft.Desktop.Views;

public static class Texts
{
    /// <summary>Zoom picker: 0 = fit.</summary>
    public static readonly IValueConverter ZoomLabel = new FuncValueConverter<int, string>(z => z <= 0 ? "Ajustar" : $"{z}x");
    public static readonly IValueConverter SpeedLabel = new FuncValueConverter<double, string>(s => $"{s:0.##}x");
}
