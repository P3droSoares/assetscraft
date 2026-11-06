using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AssetsCraft.Core.Claude;
using AssetsCraft.Core.Projects;

namespace AssetsCraft.Core.Sessions;

/// <summary>
/// Everything the UI needs for one project: the Claude Code conversation, chat history,
/// and dependency setup.
/// </summary>
public sealed class ProjectSession : IAsyncDisposable
{
    /// <summary>A sprite version is out/<name>.png (sheet) with its .json metadata next to it.</summary>
    public const string SheetExtension = ".png";

    private readonly ProjectStore _store;
    private readonly ClaudeCodeSession _claude;
    private readonly object _lock = new();
    private readonly List<ChatMessage> _messages;
    private readonly string _chatFile;
    private bool _disposed;

    private readonly SharedRuntime _runtime;

    public ProjectSession(SpriteProject project, ProjectStore store, SharedRuntime runtime, ClaudeCodeOptions options)
    {
        Project = project;
        _store = store;
        _runtime = runtime;
        _options = options;
        _chatFile = Path.Combine(project.Path, ".assetscraft", "chat.json");
        _messages = LoadChat();
        // what this conversation may produce (static sprite, tiles, animation of a base sprite…)
        var kind = AssetKinds.Get(project.Kind);
        // new sprites and tiles follow the style of the PNGs already in the linked game (animations keep their base)
        var instructions = project.Kind is AssetKind.Sprite or AssetKind.Tile
            ? kind.Instructions + "\n\n" + GameStyleSample.Instructions
            : kind.Instructions;
        _claude = new ClaudeCodeSession(options.WithAppendedPrompt(instructions), project.Path, project.SessionId);
        if (project.Model is not null) _claude.Model = project.Model;
        _claude.EventReceived += OnClaudeEvent;

        SyncManagedFiles(store.TemplateDir, project.Path, project.Kind);

        if (_messages.Count == 0 && project.Kind == AssetKind.Animation && project.BaseSpriteName is { } baseName)
            _messages.Add(new ChatMessage
            {
                Role = ChatRole.System,
                Text = $"Base: **{baseName}** (idle). O design dele fica fixo; descreva a animação que você quer " +
                       "(ex.: “andar para a direita, 6 quadros”). Cada pedido acrescenta uma animação à folha.",
            });

        // Shared runtime already installed: linking is instant, no install needed
        // (also migrates conversations linked to an older runtime or with a private node_modules).
        if (!runtime.IsLinkedInto(project.Path) && runtime.IsReady && Directory.Exists(project.Path))
        {
            try { runtime.LinkInto(project.Path); } catch { /* "Instalar dependências" retries */ }
        }
    }

    public SpriteProject Project { get; }
    public bool IsBusy => _claude.IsBusy;

    /// <summary>Shared dependencies installed and up to date, and this conversation linked to them.</summary>
    public bool DependenciesReady =>
        _runtime.IsReady && _runtime.IsLinkedInto(Project.Path);

    /// <summary>Model chosen for this conversation (null = default).</summary>
    public string? Model => Project.Model;

    /// <summary>Model actually reported by Claude Code in the last session start.</summary>
    public string? ActiveModel { get; private set; }

    public void SetModel(string? modelId)
    {
        if (string.Equals(Project.Model, modelId, StringComparison.Ordinal)) return;
        _claude.Model = modelId ?? _options.Model; // throws while busy
        Project.Model = modelId;
        _store.Save(Project);
        AddMessage(new ChatMessage { Role = ChatRole.System, Text = $"Modelo: {ModelCatalog.Find(modelId).Label}" });
    }

    /// <summary>Short status line (npm install, studio, etc.). Null when idle.</summary>
    public string? Status { get; private set; }
    public bool IsInstalling { get; private set; }

    /// <summary>Raised from background threads; UI must marshal with InvokeAsync.</summary>
    public event Action? Changed;

    /// <summary>Usage-limit snapshot received from the CLI (forwarded to the UsageMonitor).</summary>
    public event Action<IReadOnlyList<UsageWindow>>? RateLimitReceived;

    /// <summary>A turn finished (good moment to refresh sizes and usage).</summary>
    public event Action? TurnCompleted;

    /// <summary>The conversation got an automatic title.</summary>
    public event Action? Renamed;

    private readonly ClaudeCodeOptions _options;

    public IReadOnlyList<ChatMessage> Messages
    {
        get { lock (_lock) return _messages.ToList(); }
    }

    // ---------------------------------------------------------------- chat

    private const long MaxInlineImageBytes = 3_500_000; // keeps the base64 block under the API image limit

    public Task SendAsync(string text) => SendAsync(text, []);

    /// <summary>
    /// Sends a message with optional reference attachments. Files are copied into references/ (inside the
    /// project, so they are removed with it). Images also go inline so Claude sees them right away;
    /// other kinds are described with how to inspect them (frames via ffmpeg, sprite inspect, Read for PDFs).
    /// </summary>
    /// <param name="materials">Subset of <paramref name="attachmentPaths"/> to use for real in the sprite (edit,
    /// crop, reuse the palette): copied into assets/ instead of references/.</param>
    /// <summary>PNGs of the linked game copied as style reference (see <see cref="GameStyleSample"/>).</summary>
    public int GameStyleCount { get; private set; }

    /// <summary>Refreshes references/game-style/ from the linked game (sprites and tiles only).</summary>
    public int SyncGameStyle(GameProject? game)
    {
        if (Project.Kind is not (AssetKind.Sprite or AssetKind.Tile)) return 0;
        return GameStyleCount = GameStyleSample.Sync(Project, game);
    }

    public async Task SendAsync(string text, IReadOnlyList<string> attachmentPaths, IReadOnlySet<string>? materials = null)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0 && attachmentPaths.Count == 0) return;
        if (IsInstalling)
        {
            AddMessage(new ChatMessage { Role = ChatRole.System, Text = "Aguarde as dependências terminarem de instalar." });
            return;
        }

        var attached = new List<string>();
        var images = new List<ImageInput>();
        var notes = new StringBuilder();
        try
        {
            foreach (var src in attachmentPaths.Where(File.Exists))
            {
                var material = materials?.Contains(src) == true;
                var rel = await CopyIntoAsync(src, material ? Project.AssetsDir : Project.ReferencesDir);
                attached.Add(rel);
                var full = Path.Combine(Project.Path, rel);
                var kind = ReferenceKind(full);
                var size = new FileInfo(full).Length;
                var tag = material ? "MATERIAL, use de verdade no sprite" : "referência";

                if (kind == "imagem" && size <= MaxInlineImageBytes && ImageMediaType(full) is { } mt)
                {
                    images.Add(new ImageInput(mt, await File.ReadAllBytesAsync(full)));
                    notes.AppendLine($"- `{rel}` (imagem, {tag}, anexada acima)");
                }
                else
                {
                    var how = kind switch
                    {
                        "vídeo" => "referência de movimento: extraia quadros com ffmpeg (ex.: `ffmpeg -i ARQ -vf fps=8 -frames:v 12 references/frames/x-%02d.png`) e leia as imagens para estudar poses e tempo da animação",
                        "áudio" => "não entra no sprite; use só como contexto (ex.: ritmo de uma animação)",
                        "pdf" => "leia com a ferramenta Read (ex.: game design document, guia de estilo)",
                        "imagem" => "leia com a ferramenta Read; para pixel art use `node tools/sprite.mjs inspect ARQ` para ver a paleta e a grade de pixels",
                        _ => "abra com a ferramenta Read se for texto",
                    };
                    notes.AppendLine($"- `{rel}` ({kind}, {tag}) — {how}");
                }
            }
        }
        catch (Exception ex)
        {
            AddMessage(new ChatMessage { Role = ChatRole.Error, Text = $"Não foi possível anexar: {ex.Message}" });
            return;
        }

        var isFirstRequest = !Messages.Any(m => m.Role == ChatRole.User);
        AddMessage(new ChatMessage
        {
            Role = ChatRole.User,
            Text = text.Length > 0 ? text : "(referências anexadas)",
            Attachments = attached.Count > 0 ? attached : null,
        });
        if (isFirstRequest && Project.AutoName) _ = AutoTitleAsync(text, attached);

        var prompt = text;
        if (attached.Count > 0)
        {
            prompt = (text.Length > 0 ? text : "Use as referências anexadas para criar o sprite.") +
                     "\n\n---\nAnexos (copiados para o projeto):\n" + notes +
                     "Os marcados como MATERIAL já estão em assets/: use-os de verdade (editar, recortar, animar, aproveitar a paleta). " +
                     "Os demais são referência de estilo, paleta, proporções, poses e movimento; se algum deles precisar entrar " +
                     "de verdade no sprite, copie-o para assets/.";
        }

        if (isFirstRequest && GameStyleCount > 0)
            prompt += $"\n\n---\nEstilo do jogo: há {GameStyleCount} PNGs do projeto em {GameStyleSample.Folder}/ (veja o INDEX.md). " +
                      "Analise-os antes de desenhar e siga esse estilo, a menos que eu peça outro.";

        try
        {
            await _claude.SendAsync(prompt, images);
        }
        catch (Exception ex)
        {
            AddMessage(new ChatMessage { Role = ChatRole.Error, Text = $"Falha ao falar com o Claude Code: {ex.Message}" });
        }
        NotifyChanged();
    }

    /// <summary>Instant local title, then a better one from Claude (unless the user renames meanwhile).</summary>
    private async Task AutoTitleAsync(string request, IReadOnlyList<string> attachments)
    {
        var quick = ConversationTitler.Heuristic(request, attachments);
        if (quick != Project.Name)
        {
            _store.Rename(Project, quick, byUser: false);
            Renamed?.Invoke();
        }

        var workDir = Path.Combine(_store.Root, "usage"); // shared scratch cwd for one-shot calls
        var better = await ConversationTitler.GenerateAsync(_options, workDir, request, attachments);
        if (_disposed || better is null || !Project.AutoName || better == Project.Name) return;
        _store.Rename(Project, better, byUser: false);
        Renamed?.Invoke();
    }

    private async Task<string> CopyIntoAsync(string source, string dir)
    {
        Directory.CreateDirectory(dir);
        var name = Path.GetFileNameWithoutExtension(source);
        var ext = Path.GetExtension(source);
        var dest = Path.Combine(dir, name + ext);
        for (var i = 2; File.Exists(dest); i++) dest = Path.Combine(dir, $"{name}-{i}{ext}");
        await using (var src = File.OpenRead(source))
        await using (var dst = File.Create(dest))
            await src.CopyToAsync(dst);
        return Path.GetRelativePath(Project.Path, dest).Replace('\\', '/');
    }

    public static string ReferenceKind(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".svg" => "imagem",
        ".mp4" or ".mov" or ".webm" or ".mkv" or ".avi" or ".m4v" => "vídeo",
        ".mp3" or ".wav" or ".m4a" or ".aac" or ".ogg" or ".flac" => "áudio",
        ".pdf" => "pdf",
        ".txt" or ".md" or ".srt" or ".vtt" or ".json" or ".csv" => "texto",
        _ => "arquivo",
    };

    private static string? ImageMediaType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        _ => null,
    };

    public Task InterruptAsync() => _claude.InterruptAsync();

    public void ClearChat()
    {
        lock (_lock) _messages.Clear();
        Project.SessionId = null;
        _store.Save(Project);
        _claude.Kill();
        PersistChat();
        NotifyChanged();
    }

    private void OnClaudeEvent(ClaudeEvent e)
    {
        if (_disposed) return;
        if (e is RateLimitEvent rl)
        {
            RateLimitReceived?.Invoke(rl.Windows);
            return;
        }
        lock (_lock)
        {
            switch (e)
            {
                case SessionInitEvent init:
                    ActiveModel = init.Model;
                    if (Project.SessionId != init.SessionId || !Project.SessionIds.Contains(init.SessionId))
                    {
                        Project.SessionId = init.SessionId;
                        if (!Project.SessionIds.Contains(init.SessionId)) Project.SessionIds.Add(init.SessionId);
                        _store.Save(Project);
                    }
                    break;

                case TextDeltaEvent d:
                    var last = _messages.LastOrDefault();
                    if (last is { Role: ChatRole.Assistant, IsStreaming: true }) last.Text += d.Text;
                    else _messages.Add(new ChatMessage { Role = ChatRole.Assistant, Text = d.Text, IsStreaming = true });
                    break;

                case AssistantTextEvent t:
                    var streaming = _messages.LastOrDefault(m => m is { Role: ChatRole.Assistant, IsStreaming: true });
                    if (streaming is not null)
                    {
                        streaming.Text = t.Text;
                        streaming.IsStreaming = false;
                    }
                    else if (!string.IsNullOrWhiteSpace(t.Text))
                    {
                        _messages.Add(new ChatMessage { Role = ChatRole.Assistant, Text = t.Text });
                    }
                    break;

                case ToolUseEvent u:
                    FinishStreamingLocked();
                    _messages.Add(new ChatMessage
                    {
                        Role = ChatRole.Tool,
                        ToolUseId = u.Id,
                        ToolName = u.Name,
                        Text = u.InputSummary,
                        IsPending = true,
                    });
                    break;

                case ToolResultEvent r:
                    var tool = _messages.LastOrDefault(m => m.ToolUseId == r.ToolUseId);
                    if (tool is not null)
                    {
                        tool.ToolOutput = r.Content;
                        tool.IsError = r.IsError;
                        tool.IsPending = false;
                    }
                    break;

                case TurnCompletedEvent done:
                    FinishStreamingLocked();
                    foreach (var m in _messages.Where(m => m.IsPending)) m.IsPending = false;
                    var info = new StringBuilder();
                    if (done.DurationMs is { } ms) info.Append($"{ms / 1000.0:0.0}s");
                    if (done.NumTurns is { } n) info.Append($" · {n} passos");
                    if (done.CostUsd is { } c) info.Append($" · US$ {c:0.000}");
                    _messages.Add(new ChatMessage
                    {
                        Role = done.IsError ? ChatRole.Error : ChatRole.System,
                        Text = done.IsError ? $"Turno terminou com erro ({done.Subtype}). {done.Result}" : info.ToString(),
                    });
                    break;

                case ProcessExitedEvent exit:
                    FinishStreamingLocked();
                    foreach (var m in _messages.Where(m => m.IsPending)) m.IsPending = false;
                    _messages.Add(new ChatMessage
                    {
                        Role = ChatRole.Error,
                        Text = $"Processo do Claude Code encerrou (código {exit.ExitCode}). " +
                               "A próxima mensagem retoma a sessão.\n\n```\n" + Tail(exit.StdErrTail, 1500) + "\n```",
                    });
                    break;
            }
        }

        // Saved at every step (not only at the end of the turn): closing or killing the app mid-answer must
        // never lose the user's prompt or what Claude already did. Text deltas are covered by the next step.
        if (e is TurnCompletedEvent or ProcessExitedEvent) PersistChat();
        else if (e is not TextDeltaEvent) PersistChat(touchProject: false);
        NotifyChanged();
        if (e is TurnCompletedEvent or ProcessExitedEvent) TurnCompleted?.Invoke();
    }

    private void FinishStreamingLocked()
    {
        foreach (var m in _messages.Where(m => m.IsStreaming)) m.IsStreaming = false;
    }

    private void AddMessage(ChatMessage m)
    {
        lock (_lock) _messages.Add(m);
        PersistChat(touchProject: m.Role == ChatRole.User);
        NotifyChanged();
    }

    private List<ChatMessage> LoadChat()
    {
        List<ChatMessage> messages;
        try
        {
            messages = File.Exists(_chatFile)
                ? JsonSerializer.Deserialize<List<ChatMessage>>(File.ReadAllText(_chatFile)) ?? []
                : [];
        }
        catch (JsonException)
        {
            return [];
        }

        // The app was closed in the middle of an answer: no spinner forever, and say what happened.
        var interrupted = messages.Any(m => m.IsPending || m.IsStreaming)
                          || messages.LastOrDefault() is { Role: ChatRole.User or ChatRole.Tool };
        foreach (var m in messages)
        {
            m.IsPending = false;
            m.IsStreaming = false;
        }
        if (interrupted)
            messages.Add(new ChatMessage
            {
                Role = ChatRole.System,
                Text = "O app foi fechado no meio desta resposta. O que o Claude já gerou continua nos arquivos; a próxima mensagem retoma a sessão.",
            });
        return messages;
    }

    /// <param name="touchProject">Also bump the conversation in projects.json (order in the sidebar).</param>
    private void PersistChat(bool touchProject = true)
    {
        if (_disposed) return; // project being deleted: never recreate its folder
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_chatFile)!);
            string json;
            lock (_lock) json = JsonSerializer.Serialize(_messages);
            // write + rename: a kill in the middle of a write never leaves a truncated chat.json
            var tmp = _chatFile + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _chatFile, overwrite: true);
            if (touchProject) _store.Save(Project);
        }
        catch (IOException)
        {
            // best effort
        }
        catch (UnauthorizedAccessException)
        {
            // best effort
        }
    }

    // ---------------------------------------------------------------- files

    /// <summary>
    /// Sprite sheets in out/ (top level only: gif/, .preview/ and .sources/ are byproducts of each render),
    /// newest first. PNGs without metadata are listed too (shown as a single frame).
    /// </summary>
    public IReadOnlyList<FileInfo> ListSprites() => ListSprites(Project);

    /// <summary>Same as <see cref="ListSprites()"/> without opening a session (sidebar thumbnails and progress).</summary>
    public static IReadOnlyList<FileInfo> ListSprites(SpriteProject project) =>
        Directory.Exists(project.OutputDir)
            ? new DirectoryInfo(project.OutputDir)
                .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                .Where(f => string.Equals(f.Extension, SheetExtension, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList()
            : [];

    public IReadOnlyList<FileInfo> ListAssets() =>
        Directory.Exists(Project.AssetsDir)
            ? new DirectoryInfo(Project.AssetsDir)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .OrderBy(f => f.Name)
                .ToList()
            : [];

    public async Task SaveAssetAsync(string fileName, Stream content, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Directory.CreateDirectory(Project.AssetsDir);
        var safe = Path.GetFileName(fileName);
        if (string.IsNullOrWhiteSpace(safe)) throw new ArgumentException("Nome de arquivo inválido.");
        var dest = Path.Combine(Project.AssetsDir, safe);
        await using var fs = File.Create(dest);
        await content.CopyToAsync(fs, ct);
        NotifyChanged();
    }

    /// <summary>Copies a version's PNG into the linked game and remembers which version is there.</summary>
    public string SendToGame(string sheetFull, GameProject game)
    {
        var dest = ConversationFiles.SendToGame(Project, sheetFull, game);
        Project.SentVersion = Path.GetFileName(sheetFull);
        Project.SentPath = dest;
        _store.Save(Project);
        NotifyChanged();
        return dest;
    }

    /// <summary>Undoes the last send: deletes the PNG from the game and restores what it had replaced.</summary>
    public void RemoveFromGame(GameProject game)
    {
        ConversationFiles.RemoveFromGame(Project, game);
        _store.Save(Project);
        NotifyChanged();
    }

    /// <summary>Resolves a path relative to the workspace, refusing anything outside it.</summary>
    public string? ResolveWorkspaceFile(string relative)
    {
        var root = Path.GetFullPath(Project.Path) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relative));
        return full.StartsWith(root, StringComparison.Ordinal) && File.Exists(full) ? full : null;
    }

    // ---------------------------------------------------------------- npm / studio

    /// <summary>
    /// Files owned by the app (Claude's instructions and helper tools) are refreshed from the template,
    /// so conversations created by older versions get new features. User content
    /// (sprites/, assets/, out/, references/) is never touched.
    /// </summary>
    private static void SyncManagedFiles(string templateDir, string projectPath, AssetKind kind)
    {
        try
        {
            if (!Directory.Exists(projectPath) || !Directory.Exists(templateDir)) return;
            var managed = new List<string> { "CLAUDE.md" };
            var tools = Path.Combine(templateDir, "tools");
            if (Directory.Exists(tools))
                managed.AddRange(Directory.GetFiles(tools, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(templateDir, f)));
            foreach (var rel in managed)
            {
                var src = Path.Combine(templateDir, rel);
                var dst = Path.Combine(projectPath, rel);
                if (!File.Exists(src)) continue;
                if (File.Exists(dst) && File.ReadAllBytes(dst).AsSpan().SequenceEqual(File.ReadAllBytes(src))) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst, overwrite: true);
            }
            foreach (var dir in new[] { "sprites", "assets", "out" }) Directory.CreateDirectory(Path.Combine(projectPath, dir));
            // read by tools/sprite.mjs: a static sprite renders only one frame, tiles never animate
            var kindFile = Path.Combine(projectPath, ".assetscraft", "kind.json");
            var kindJson = $"{{\"kind\": \"{kind.ToString().ToLowerInvariant()}\"}}";
            Directory.CreateDirectory(Path.GetDirectoryName(kindFile)!);
            if (!File.Exists(kindFile) || File.ReadAllText(kindFile) != kindJson) File.WriteAllText(kindFile, kindJson);
        }
        catch { /* best effort */ }
    }

    /// <summary>Makes sure the shared node_modules exists and links it into this project.</summary>
    public async Task InstallDependenciesAsync()
    {
        if (IsInstalling || _disposed) return;
        IsInstalling = true;
        var firstTime = !_runtime.IsReady;
        Status = "Preparando dependências…";
        NotifyChanged();
        try
        {
            var (ok, output) = await _runtime.EnsureAsync(msg => { Status = msg; NotifyChanged(); });
            if (_disposed) return; // project deleted meanwhile: never recreate its folder
            if (!ok)
            {
                AddMessage(new ChatMessage { Role = ChatRole.Error, Text = $"Falha ao instalar as dependências compartilhadas.\n\n```\n{Tail(output, 2000)}\n```" });
                return;
            }
            _runtime.LinkInto(Project.Path);
            if (firstTime)
                AddMessage(new ChatMessage { Role = ChatRole.System, Text = "Dependências prontas (compartilhadas entre todas as conversas). Pode pedir o seu sprite." });
        }
        catch (Exception ex)
        {
            if (!_disposed) AddMessage(new ChatMessage { Role = ChatRole.Error, Text = $"Falha ao preparar dependências: {ex.Message}" });
        }
        finally
        {
            IsInstalling = false;
            Status = null;
            NotifyChanged();
        }
    }

    private static string Tail(string s, int max) => s.Length <= max ? s.Trim() : "…" + s[^max..].Trim();

    private void NotifyChanged()
    {
        try { Changed?.Invoke(); } catch { /* ignore */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        if (Directory.Exists(Project.Path)) PersistChat(touchProject: false); // app closing mid-answer: keep streamed text
        _disposed = true;
        _claude.EventReceived -= OnClaudeEvent;
        _claude.Kill();
        await _claude.DisposeAsync();
    }
}
