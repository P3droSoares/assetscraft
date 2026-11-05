namespace AssetsCraft.Core.Projects;

/// <summary>What a conversation produces. Each kind has its own section in the sidebar.</summary>
public enum AssetKind { Sprite, Tile, Animation, Sfx, Music, Cutscene }

/// <param name="Folder">Subfolder of the game's Content where "Enviar ao projeto" puts the PNG.</param>
/// <param name="Available">False = shown in the menu as "em breve".</param>
/// <param name="Instructions">Appended to Claude's system prompt for conversations of this kind.</param>
public sealed record AssetKindInfo(
    AssetKind Kind, string Label, string Singular, string NewLabel, string Folder, bool Available, string Hint, string Instructions);

public static class AssetKinds
{
    public static readonly IReadOnlyList<AssetKindInfo> All =
    [
        new(AssetKind.Sprite, "Sprites", "sprite", "Novo sprite", "Sprites", true,
            "Personagens, inimigos, itens e objetos, desenhados parados (pose idle).",
            "TIPO DESTA CONVERSA: SPRITE ESTÁTICO. Gere apenas 1 quadro: a pose idle/neutra do sprite, sem animação " +
            "(uma única animação \"idle\" com 1 quadro). Não crie quadros de andar, pular, atacar etc.: animações são " +
            "feitas depois, numa conversa da seção Animação que parte deste sprite. Desenhe pensando nisso: pose " +
            "neutra, de frente ou 3/4, pés na última linha útil, 1px livre na borda para o contorno. Se o usuário pedir " +
            "animação aqui, faça o idle e diga que a animação é feita em Animação → Nova animação. Uma conversa = um sprite."),
        new(AssetKind.Tile, "Tiles", "tile", "Novo tileset", "Tiles", true,
            "Tiles e tilesets estáticos que encaixam entre si.",
            "TIPO DESTA CONVERSA: TILES ESTÁTICOS. Cada tile é um quadro de uma animação \"tiles\" (fps irrelevante); " +
            "use export.columns para uma grade (ex.: 8). Nada animado. As bordas devem encaixar entre tiles vizinhos " +
            "(confira montando mentalmente 2x2 na preview). Tamanho padrão 16×16."),
        new(AssetKind.Animation, "Animação", "animação", "Nova animação", "Animations", true,
            "Animações de um sprite já criado (andar, pular, atacar…).",
            "TIPO DESTA CONVERSA: ANIMAÇÃO DE UM SPRITE EXISTENTE. O arquivo-fonte em sprites/ já traz o sprite base aprovado " +
            "como a animação \"idle\" (quadro idle/0) e assets/base.png é a imagem dele. NUNCA mude o design do sprite base " +
            "(cores, proporções, silhueta, detalhes): as animações devem parecer o mesmo personagem se movendo. Construa " +
            "cada quadro a partir de \"base\": \"idle/0\" com patch/shift/layers sempre que possível; mantenha o mesmo " +
            "\"size\" (só aumente se a animação realmente sair do quadro, mantendo o personagem na mesma posição). " +
            "Mantenha a animação idle e acrescente as que o usuário pedir, uma linha da folha por animação."),
        new(AssetKind.Sfx, "SFX", "efeito sonoro", "Novo SFX", "Sfx", false, "Efeitos sonoros (em breve).", ""),
        new(AssetKind.Music, "Música", "música", "Nova música", "Music", false, "Trilhas do jogo (em breve).", ""),
        new(AssetKind.Cutscene, "Cutscene", "cutscene", "Nova cutscene", "Cutscenes", false, "Cenas animadas (em breve).", ""),
    ];

    public static AssetKindInfo Get(AssetKind kind) => All.First(k => k.Kind == kind);
}
