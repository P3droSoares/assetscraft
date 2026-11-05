using System.Text.Json;

namespace AssetsCraft.Core.Sprites;

public sealed record SpriteFrame(string Name, int X, int Y, int W, int H, int DurationMs);

/// <summary>A named run of frames (Aseprite "frameTag"). Frames index into <see cref="SpriteSheet.Frames"/>.</summary>
public sealed record SpriteAnimation(string Name, int From, int To, bool Loop, bool PingPong, double Fps)
{
    public int Count => To - From + 1;

    /// <summary>Playback order (ping-pong plays forward then back without repeating the ends).</summary>
    public IReadOnlyList<int> Sequence()
    {
        var seq = Enumerable.Range(From, Count).ToList();
        if (PingPong && Count > 2) seq.AddRange(Enumerable.Range(From + 1, Count - 2).Reverse());
        return seq;
    }
}

/// <summary>
/// Metadata written next to a sheet by tools/sprite.mjs (Aseprite JSON, array or hash flavour).
/// Sheets without metadata are shown as a single frame.
/// </summary>
public sealed class SpriteSheet
{
    public required string ImagePath { get; init; }
    public string? MetadataPath { get; init; }
    public required IReadOnlyList<SpriteFrame> Frames { get; init; }
    public required IReadOnlyList<SpriteAnimation> Animations { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int Scale { get; init; } = 1;
    public string? Source { get; init; }

    public SpriteFrame? FirstFrame => Frames.Count > 0 ? Frames[0] : null;

    public static string MetadataFor(string imagePath) => Path.ChangeExtension(imagePath, ".json");

    /// <summary>Reads the sheet's size from the PNG header and its metadata.</summary>
    public static SpriteSheet Load(string imagePath)
    {
        var (w, h) = ReadPngSize(imagePath);
        return Load(imagePath, w, h);
    }

    /// <summary>Width/height from the IHDR chunk (no decoding). (0, 0) when the file is not a PNG.</summary>
    public static (int W, int H) ReadPngSize(string path)
    {
        try
        {
            Span<byte> head = stackalloc byte[24];
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Read(head) < 24 || head[0] != 0x89 || head[1] != (byte)'P') return (0, 0);
            return (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(head[16..]),
                    System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(head[20..]));
        }
        catch (IOException)
        {
            return (0, 0);
        }
    }

    public int FrameWidth => FirstFrame?.W ?? Width;
    public int FrameHeight => FirstFrame?.H ?? Height;

    /// <summary>"48×30 · quadro 12×10 · 8 quadros · 3 animações".</summary>
    public string Summary =>
        $"{Width}×{Height} · quadro {FrameWidth}×{FrameHeight} · {Frames.Count} {(Frames.Count == 1 ? "quadro" : "quadros")}" +
        (MetadataPath is null ? " · sem metadados" : $" · {Animations.Count} {(Animations.Count == 1 ? "animação" : "animações")}") +
        (Scale > 1 ? $" · escala {Scale}x" : "");

    /// <summary>Reads &lt;sheet&gt;.json; falls back to one frame covering the whole image.</summary>
    public static SpriteSheet Load(string imagePath, int imageWidth, int imageHeight)
    {
        var metaPath = MetadataFor(imagePath);
        if (File.Exists(metaPath))
        {
            try
            {
                return Parse(imagePath, metaPath, File.ReadAllText(metaPath), imageWidth, imageHeight);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
            {
                // malformed metadata: still show the image
            }
        }
        return Single(imagePath, imageWidth, imageHeight);
    }

    private static SpriteSheet Single(string imagePath, int w, int h) => new()
    {
        ImagePath = imagePath,
        Frames = [new SpriteFrame(Path.GetFileNameWithoutExtension(imagePath), 0, 0, w, h, 100)],
        Animations = [new SpriteAnimation("imagem", 0, 0, true, false, 10)],
        Width = w,
        Height = h,
    };

    public static SpriteSheet Parse(string imagePath, string? metaPath, string json, int imageWidth, int imageHeight)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var frames = new List<SpriteFrame>();
        var framesEl = root.GetProperty("frames");
        IEnumerable<(string name, JsonElement el)> items = framesEl.ValueKind == JsonValueKind.Array
            ? framesEl.EnumerateArray().Select((e, i) => (e.TryGetProperty("filename", out var n) ? n.GetString() ?? $"{i}" : $"{i}", e))
            : framesEl.EnumerateObject().Select(p => (p.Name, p.Value));
        foreach (var (name, el) in items)
        {
            var f = el.GetProperty("frame");
            var duration = el.TryGetProperty("duration", out var d) && d.TryGetInt32(out var ms) ? ms : 100;
            frames.Add(new SpriteFrame(name, f.GetProperty("x").GetInt32(), f.GetProperty("y").GetInt32(),
                f.GetProperty("w").GetInt32(), f.GetProperty("h").GetInt32(), Math.Max(10, duration)));
        }
        if (frames.Count == 0) return Single(imagePath, imageWidth, imageHeight);

        var animations = new List<SpriteAnimation>();
        var scale = 1;
        string? source = null;
        if (root.TryGetProperty("meta", out var meta))
        {
            JsonElement extra = default;
            var hasExtra = meta.TryGetProperty("assetscraft", out extra);
            if (hasExtra)
            {
                if (extra.TryGetProperty("scale", out var s) && s.TryGetInt32(out var si)) scale = si;
                if (extra.TryGetProperty("source", out var src)) source = src.GetString();
            }
            if (meta.TryGetProperty("frameTags", out var tags) && tags.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in tags.EnumerateArray())
                {
                    var name = t.GetProperty("name").GetString() ?? "anim";
                    var from = Math.Clamp(t.GetProperty("from").GetInt32(), 0, frames.Count - 1);
                    var to = Math.Clamp(t.GetProperty("to").GetInt32(), from, frames.Count - 1);
                    var dir = t.TryGetProperty("direction", out var de) ? de.GetString() : "forward";
                    var loop = !(t.TryGetProperty("repeat", out var rep) && rep.ToString() is { Length: > 0 } r && r != "0");
                    if (hasExtra && extra.TryGetProperty("animations", out var an) && an.TryGetProperty(name, out var a)
                        && a.TryGetProperty("loop", out var l) && l.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        loop = l.GetBoolean();
                    var avg = frames.Skip(from).Take(to - from + 1).Average(f => f.DurationMs);
                    animations.Add(new SpriteAnimation(name, from, to, loop, dir == "pingpong", Math.Round(1000.0 / avg, 1)));
                }
            }
        }
        if (animations.Count == 0)
            animations.Add(new SpriteAnimation("todos os quadros", 0, frames.Count - 1, true, false,
                Math.Round(1000.0 / frames.Average(f => f.DurationMs), 1)));

        return new SpriteSheet
        {
            ImagePath = imagePath,
            MetadataPath = metaPath,
            Frames = frames,
            Animations = animations,
            Width = imageWidth,
            Height = imageHeight,
            Scale = scale,
            Source = source,
        };
    }
}
