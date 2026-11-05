using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace AssetsCraft.Desktop.Views;

/// <summary>
/// Minimal Markdown → Inlines for chat text: **bold**, `code`, # headings, - bullets, ``` fences.
/// Attach with <c>views:MarkdownText.Source="{Binding Text}"</c> on a SelectableTextBlock.
/// </summary>
public static partial class MarkdownText
{
    public static readonly AttachedProperty<string?> SourceProperty =
        AvaloniaProperty.RegisterAttached<SelectableTextBlock, string?>("Source", typeof(MarkdownText));

    static MarkdownText() =>
        SourceProperty.Changed.AddClassHandler<SelectableTextBlock>((tb, e) => Render(tb, e.NewValue as string));

    public static string? GetSource(SelectableTextBlock tb) => tb.GetValue(SourceProperty);
    public static void SetSource(SelectableTextBlock tb, string? value) => tb.SetValue(SourceProperty, value);

    private static readonly FontFamily Mono =
        new("Berkeley Mono, JetBrains Mono, IBM Plex Mono, Cascadia Mono, Consolas, DejaVu Sans Mono, monospace");
    private static readonly IBrush Paper = new SolidColorBrush(Color.Parse("#FFFFFF"));
    private static readonly IBrush Fog = new SolidColorBrush(Color.Parse("#8A8F98"));
    private static readonly IBrush CodeBg = new SolidColorBrush(Color.Parse("#14FFFFFF"));

    private static void Render(SelectableTextBlock tb, string? text)
    {
        var inlines = new InlineCollection();
        var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
        var inFence = false;

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith("```"))
            {
                inFence = !inFence;
                continue;
            }

            if (inFence)
            {
                inlines.Add(new Run(line) { FontFamily = Mono, FontSize = 12, Foreground = Fog, Background = CodeBg });
            }
            else if (HeadingRe().Match(line) is { Success: true } h)
            {
                inlines.Add(new Run(h.Groups[2].Value) { FontWeight = FontWeight.SemiBold, Foreground = Paper });
            }
            else
            {
                var bullet = BulletRe().Match(line);
                if (bullet.Success)
                {
                    inlines.Add(new Run(bullet.Groups[1].Value + "•  ") { Foreground = Fog });
                    line = line[bullet.Length..];
                }
                AddSpans(inlines, line);
            }

            if (i < lines.Length - 1) inlines.Add(new LineBreak());
        }

        tb.Inlines = inlines;
    }

    private static void AddSpans(InlineCollection inlines, string line)
    {
        var pos = 0;
        foreach (Match m in SpanRe().Matches(line))
        {
            if (m.Index > pos) inlines.Add(new Run(line[pos..m.Index]));
            if (m.Groups["code"].Success)
                inlines.Add(new Run(m.Groups["code"].Value) { FontFamily = Mono, FontSize = 12.5, Background = CodeBg, Foreground = Paper });
            else
                inlines.Add(new Run(m.Groups["bold"].Value) { FontWeight = FontWeight.SemiBold, Foreground = Paper });
            pos = m.Index + m.Length;
        }
        if (pos < line.Length) inlines.Add(new Run(line[pos..]));
    }

    [GeneratedRegex(@"^(#{1,4})\s+(.*)$")]
    private static partial Regex HeadingRe();

    [GeneratedRegex(@"^(\s*)[-*]\s+")]
    private static partial Regex BulletRe();

    [GeneratedRegex(@"`(?<code>[^`]+)`|\*\*(?<bold>[^*]+)\*\*")]
    private static partial Regex SpanRe();
}
