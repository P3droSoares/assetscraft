namespace AssetsCraft.Core.Claude;

public sealed record ModelOption(string? Id, string Label, string Description);

/// <summary>Models offered in the per-conversation picker. Id null = Claude Code's default model.</summary>
public static class ModelCatalog
{
    public static readonly IReadOnlyList<ModelOption> All =
    [
        new(null, "Padrão", "O modelo padrão do seu Claude Code"),
        new("claude-fable-5-1", "Fable 5.1", "O mais capaz — personagens e animações complexas, mais lento e consome mais limite"),
        new("claude-opus-5-5", "Opus 5.5", "Muito capaz, ótimo para sprites e animações elaboradas"),
        new("claude-sonnet-5-5", "Sonnet 5.5", "Equilíbrio entre qualidade e velocidade"),
        new("claude-haiku-5-5", "Haiku 5.5", "O mais rápido e econômico, para ajustes simples"),
    ];

    public static ModelOption Find(string? id) =>
        All.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? new ModelOption(id, id ?? "Padrão", "Modelo personalizado");

    /// <summary>Short display name for a model id reported by the CLI (e.g. "claude-sonnet-5-5" → "Sonnet 5.5").</summary>
    public static string ShortName(string? id) =>
        id is null ? "" : All.FirstOrDefault(m => m.Id == id)?.Label ?? id;
}
