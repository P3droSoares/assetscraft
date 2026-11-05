using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AssetsCraft.Core.Claude;

/// <summary>
/// Names a conversation from its first request: an instant local title, then a better one from a tiny
/// one-shot Claude call (haiku, no tools, no MCP, --no-session-persistence → leaves no transcript).
/// </summary>
public static partial class ConversationTitler
{
    public const string DefaultName = "Nova conversa";
    private const int MaxLength = 48;

    private static readonly HashSet<string> Filler = new(StringComparer.OrdinalIgnoreCase)
    {
        "quero", "queria", "gostaria", "preciso", "faça", "faca", "faz", "crie", "cria", "criar", "gere", "gera",
        "gerar", "me", "um", "uma", "o", "a", "os", "as", "de", "do", "da", "por", "favor", "pra", "para",
        "você", "voce", "pode", "poderia", "please", "make", "create", "i", "want", "an", "the",
    };

    /// <summary>Local title: first meaningful words of the request (or attachment names).</summary>
    public static string Heuristic(string text, IReadOnlyList<string> attachments)
    {
        var words = WordRe().Matches(text ?? "").Select(m => m.Value).ToList();
        while (words.Count > 0 && Filler.Contains(words[0])) words.RemoveAt(0);
        var title = string.Join(' ', words.Take(6));
        if (title.Length == 0 && attachments.Count > 0)
            title = "Referências: " + Path.GetFileNameWithoutExtension(attachments[0]);
        if (title.Length == 0) return DefaultName;
        return Clean(char.ToUpper(title[0]) + title[1..]);
    }

    /// <summary>Asks Claude for a short title. Returns null on any failure (caller keeps the heuristic).</summary>
    public static async Task<string?> GenerateAsync(ClaudeCodeOptions options, string workDir, string request,
        IReadOnlyList<string> attachments, CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(workDir);
            var files = attachments.Count > 0 ? $" Arquivos de referência anexados: {string.Join(", ", attachments.Select(Path.GetFileName))}." : "";
            var prompt =
                "Crie um título curto (no máximo 5 palavras, em português, sem aspas, sem emoji e sem ponto final) " +
                "para uma conversa de criação de sprites cujo primeiro pedido é:\n\"\"\"\n" +
                Truncate(request, 1500) + "\n\"\"\"" + files + "\nResponda apenas o título.";

            var psi = CommandResolver.Create(options.ExecutablePath,
                ["-p", prompt, "--model", "claude-haiku-5-5", "--no-session-persistence", "--tools", "", "--strict-mcp-config",
                 "--setting-sources", options.SettingSources ?? "project,local", "--output-format", "json"],
                workDir);
            using var p = ChildProcesses.Start(psi);
            if (p is null) return null;
            var stdout = p.StandardOutput.ReadToEndAsync(ct);
            _ = p.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            try
            {
                await p.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return null;
            }
            if (p.ExitCode != 0) return null;

            using var doc = JsonDocument.Parse(await stdout);
            if (doc.RootElement.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True) return null;
            var title = doc.RootElement.TryGetProperty("result", out var r) ? r.GetString() : null;
            title = Clean(title?.Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "");
            return title.Length is > 0 and <= MaxLength + 10 ? title : null;
        }
        catch
        {
            return null;
        }
    }

    private static string Clean(string s)
    {
        s = s.Trim().Trim('"', '\'', '“', '”', '«', '»', '*', '#', ' ').TrimEnd('.', '!', ':', ';');
        s = Regex.Replace(s, @"\s+", " ");
        if (s.StartsWith("Título:", StringComparison.OrdinalIgnoreCase)) s = s[7..].Trim();
        return s.Length > MaxLength ? s[..MaxLength].TrimEnd() + "…" : s;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}:/x×\-]*")]
    private static partial Regex WordRe();
}
