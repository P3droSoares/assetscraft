using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace AssetsCraft.Core.Claude;

/// <summary>
/// Long-lived conversation with the local Claude Code CLI.
///
/// Launches <c>claude -p --input-format stream-json --output-format stream-json</c> in the
/// project's working directory, writes user messages as JSON lines on stdin and parses the
/// JSON events from stdout. This is the same transport the official Agent SDKs (TS/Python)
/// use under the hood, so it reuses the Claude Code installation and login on this machine.
///
/// If the process dies (or is interrupted by killing it), the next <see cref="SendAsync"/>
/// restarts it with <c>--resume &lt;sessionId&gt;</c>, keeping the conversation context.
/// </summary>
public sealed class ClaudeCodeSession : IAsyncDisposable
{
    private const int MaxToolOutputChars = 4000;
    private const int MaxStdErrChars = 8000;

    private readonly ClaudeCodeOptions _options;
    private readonly string _workingDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly StringBuilder _stderr = new();
    private Process? _process;
    private Task? _readerTask;
    private volatile bool _busy;

    public ClaudeCodeSession(ClaudeCodeOptions options, string workingDirectory, string? resumeSessionId = null)
    {
        _options = options;
        _workingDirectory = workingDirectory;
        SessionId = resumeSessionId;
        _model = options.Model;
    }

    public string? SessionId { get; private set; }

    private string? _model;
    private volatile bool _restarting;

    /// <summary>
    /// Model for the next turns. Changing it restarts the CLI process (idle only) with --resume,
    /// so the conversation context is kept and the new --model applies.
    /// </summary>
    public string? Model
    {
        get => _model;
        set
        {
            if (string.Equals(_model, value, StringComparison.Ordinal)) return;
            if (_busy) throw new InvalidOperationException("Aguarde o Claude terminar para trocar o modelo.");
            _model = value;
            if (IsRunning)
            {
                // Stop the idle process and wait until it is really gone, so the next SendAsync
                // starts a fresh one (--resume keeps the context) instead of writing to the dying one.
                _restarting = true;
                var proc = _process;
                Kill();
                try { proc?.WaitForExit(5000); } catch { /* ignore */ }
                try { _readerTask?.Wait(5000); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>True while a turn (user message → final result) is in progress.</summary>
    public bool IsBusy => _busy;

    public bool IsRunning => _process is { HasExited: false };

    public event Action<ClaudeEvent>? EventReceived;

    public Task SendAsync(string prompt, CancellationToken ct = default) => SendAsync(prompt, [], ct);

    /// <summary>Sends a user turn. Images are passed inline as base64 content blocks so the model sees them directly.</summary>
    public async Task SendAsync(string prompt, IReadOnlyList<ImageInput> images, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_busy) throw new InvalidOperationException("Claude ainda está processando a mensagem anterior.");
            EnsureStarted();
            _busy = true;

            var content = new List<object>();
            foreach (var img in images)
            {
                content.Add(new
                {
                    type = "image",
                    source = new { type = "base64", media_type = img.MediaType, data = Convert.ToBase64String(img.Data) },
                });
            }
            content.Add(new { type = "text", text = prompt });

            var payload = new
            {
                type = "user",
                message = new { role = "user", content },
            };
            await WriteLineAsync(JsonSerializer.Serialize(payload), ct);
        }
        catch
        {
            _busy = false;
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Asks the CLI to stop the current turn; kills the process if it does not comply.</summary>
    public async Task InterruptAsync()
    {
        if (!_busy || _process is null) return;

        try
        {
            var request = new
            {
                type = "control_request",
                request_id = Guid.NewGuid().ToString("N"),
                request = new { subtype = "interrupt" },
            };
            await WriteLineAsync(JsonSerializer.Serialize(request), CancellationToken.None);
        }
        catch
        {
            // stdin may already be closed; fall through to kill.
        }

        for (var i = 0; i < 40 && _busy; i++) await Task.Delay(100);
        if (_busy) Kill();
    }

    public void Kill()
    {
        try
        {
            ChildProcesses.KillTree(_process);
        }
        catch (InvalidOperationException)
        {
            // already exited
        }
    }

    private async Task WriteLineAsync(string line, CancellationToken ct)
    {
        var stdin = _process!.StandardInput;
        await stdin.WriteLineAsync(line.AsMemory(), ct);
        await stdin.FlushAsync(ct);
    }

    private void EnsureStarted()
    {
        if (IsRunning) return;

        _process?.Dispose();
        lock (_stderr) _stderr.Clear();

        var psi = CommandResolver.Create(_options.ExecutablePath, BuildArguments(), _workingDirectory);
        psi.RedirectStandardInput = true;
        psi.StandardInputEncoding = new UTF8Encoding(false);

        var process = ChildProcesses.Start(psi);
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (_stderr)
            {
                _stderr.AppendLine(e.Data);
                if (_stderr.Length > MaxStdErrChars) _stderr.Remove(0, _stderr.Length - MaxStdErrChars);
            }
        };

        process.BeginErrorReadLine();
        _process = process;
        _readerTask = Task.Run(() => ReadLoopAsync(process));
    }

    private IEnumerable<string> BuildArguments()
    {
        yield return "-p";
        yield return "--input-format";
        yield return "stream-json";
        yield return "--output-format";
        yield return "stream-json";
        yield return "--verbose";
        yield return "--include-partial-messages";

        if (!string.IsNullOrWhiteSpace(_options.PermissionMode))
        {
            yield return "--permission-mode";
            yield return _options.PermissionMode;
        }

        if (!string.IsNullOrWhiteSpace(_model))
        {
            yield return "--model";
            yield return _model;
        }

        if (!string.IsNullOrWhiteSpace(_options.SettingSources))
        {
            yield return "--setting-sources";
            yield return _options.SettingSources;
        }

        if (_options.AllowedTools.Count > 0)
        {
            yield return "--allowedTools";
            foreach (var t in _options.AllowedTools) yield return t;
        }

        if (_options.DisallowedTools.Count > 0)
        {
            yield return "--disallowedTools";
            foreach (var t in _options.DisallowedTools) yield return t;
        }

        if (!string.IsNullOrWhiteSpace(_options.AppendSystemPrompt))
        {
            yield return "--append-system-prompt";
            yield return _options.AppendSystemPrompt;
        }

        if (!string.IsNullOrWhiteSpace(SessionId))
        {
            yield return "--resume";
            yield return SessionId;
        }

        foreach (var a in _options.ExtraArgs) yield return a;
    }

    private async Task ReadLoopAsync(Process process)
    {
        try
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (!ReferenceEquals(process, _process)) continue; // stale process output
                try
                {
                    HandleLine(line);
                }
                catch (JsonException)
                {
                    // Non-JSON noise on stdout; ignore.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // stream closed
        }

        try { await process.WaitForExitAsync(); } catch { /* ignore */ }

        // A replaced process (model change / restart) must not touch the state of the new one.
        if (!ReferenceEquals(process, _process))
        {
            _restarting = false;
            return;
        }

        var wasBusy = _busy;
        _busy = false;
        string tail;
        lock (_stderr) tail = _stderr.ToString();
        var exitCode = SafeExitCode(process);
        if (_restarting && !wasBusy)
        {
            _restarting = false; // intentional restart (model change): not an error
            return;
        }
        // Only an exit in the middle of a turn is an error. An idle process being stopped (app closing,
        // model change, restart) is normal; the next message simply resumes the session.
        if (wasBusy) Raise(new ProcessExitedEvent(exitCode, tail));
    }

    private static int SafeExitCode(Process p)
    {
        try { return p.ExitCode; } catch { return -1; }
    }

    private void HandleLine(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var type = root.Str("type");

        // Ignore messages produced inside sub-agents (Task tool) to keep the chat readable.
        if (root.TryGetProperty("parent_tool_use_id", out var parent) && parent.ValueKind == JsonValueKind.String)
            return;

        switch (type)
        {
            case "system" when root.Str("subtype") == "init":
                SessionId = root.Str("session_id") ?? SessionId;
                if (SessionId is not null) Raise(new SessionInitEvent(SessionId, root.Str("model")));
                break;

            case "stream_event":
                if (root.TryGetProperty("event", out var ev)
                    && ev.Str("type") == "content_block_delta"
                    && ev.TryGetProperty("delta", out var delta)
                    && delta.Str("type") == "text_delta")
                {
                    var text = delta.Str("text");
                    if (!string.IsNullOrEmpty(text)) Raise(new TextDeltaEvent(text));
                }
                break;

            case "assistant":
                foreach (var block in Blocks(root))
                {
                    switch (block.Str("type"))
                    {
                        case "text":
                            Raise(new AssistantTextEvent(block.Str("text") ?? ""));
                            break;
                        case "tool_use":
                            Raise(new ToolUseEvent(
                                block.Str("id") ?? "",
                                block.Str("name") ?? "?",
                                SummarizeToolInput(block.Str("name"), block.TryGetProperty("input", out var inp) ? inp : default)));
                            break;
                    }
                }
                break;

            case "user":
                foreach (var block in Blocks(root))
                {
                    if (block.Str("type") != "tool_result") continue;
                    var isError = block.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
                    Raise(new ToolResultEvent(block.Str("tool_use_id") ?? "", Truncate(ToolResultText(block)), isError));
                }
                break;

            case "rate_limit_event":
                Raise(new RateLimitEvent(UsageMonitor.ParseRateLimitEvent(root)));
                break;

            case "result":
                _busy = false;
                SessionId = root.Str("session_id") ?? SessionId;
                Raise(new TurnCompletedEvent(
                    IsError: root.TryGetProperty("is_error", out var err) && err.ValueKind == JsonValueKind.True,
                    Subtype: root.Str("subtype"),
                    Result: root.Str("result"),
                    CostUsd: root.Num("total_cost_usd"),
                    DurationMs: (long?)root.Num("duration_ms"),
                    NumTurns: (int?)root.Num("num_turns")));
                break;
        }
    }

    private static IEnumerable<JsonElement> Blocks(JsonElement root)
    {
        if (root.TryGetProperty("message", out var msg)
            && msg.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.Array)
        {
            foreach (var b in content.EnumerateArray()) yield return b;
        }
    }

    private static string ToolResultText(JsonElement block)
    {
        if (!block.TryGetProperty("content", out var c)) return "";
        if (c.ValueKind == JsonValueKind.String) return c.GetString() ?? "";
        if (c.ValueKind != JsonValueKind.Array) return c.GetRawText();

        var sb = new StringBuilder();
        foreach (var part in c.EnumerateArray())
        {
            if (part.Str("type") == "text") sb.AppendLine(part.Str("text"));
            else sb.AppendLine($"[{part.Str("type")}]");
        }
        return sb.ToString();
    }

    private static string SummarizeToolInput(string? name, JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return "";
        var summary = name switch
        {
            "Bash" => input.Str("command"),
            "Read" or "Write" or "Edit" or "MultiEdit" or "NotebookEdit" => input.Str("file_path"),
            "Glob" or "Grep" => input.Str("pattern"),
            "WebFetch" => input.Str("url"),
            "WebSearch" => input.Str("query"),
            "Task" or "Agent" => input.Str("description"),
            "TodoWrite" => "atualizando lista de tarefas",
            _ => null,
        };
        return Truncate(summary ?? input.GetRawText(), 600);
    }

    private static string Truncate(string s, int max = MaxToolOutputChars) =>
        s.Length <= max ? s : s[..max] + $"\n… ({s.Length - max} caracteres omitidos)";

    private void Raise(ClaudeEvent e)
    {
        try { EventReceived?.Invoke(e); }
        catch { /* subscriber errors must not kill the read loop */ }
    }

    public async ValueTask DisposeAsync()
    {
        try { _process?.StandardInput.Close(); } catch { /* ignore */ }
        if (_readerTask is not null)
        {
            var finished = await Task.WhenAny(_readerTask, Task.Delay(2000)) == _readerTask;
            if (!finished) Kill();
        }
        _process?.Dispose();
        _gate.Dispose();
    }
}

/// <summary>Image sent inline with a user message (png, jpeg, gif or webp).</summary>
public sealed record ImageInput(string MediaType, byte[] Data);

internal static class JsonExt
{
    public static string? Str(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    public static double? Num(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetDouble()
            : null;
}
