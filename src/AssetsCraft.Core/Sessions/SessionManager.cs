using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using AssetsCraft.Core.Claude;
using AssetsCraft.Core.Projects;

namespace AssetsCraft.Core.Sessions;

/// <summary>Singleton: keeps one live <see cref="ProjectSession"/> per project so sessions
/// survive switching between projects in the UI.</summary>
public sealed class SessionManager(ProjectStore store, IOptions<AssetsCraftOptions> options) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, ProjectSession> _sessions = new();

    /// <summary>node_modules shared by all projects (installed once).</summary>
    public SharedRuntime Runtime { get; } = new(store.Root, store.TemplateDir);

    /// <summary>Claude subscription usage limits (shared by all conversations).</summary>
    public UsageMonitor Usage { get; } = new(options.Value.Claude, store.Root);

    public ProjectSession Get(SpriteProject project) =>
        _sessions.GetOrAdd(project.Id, id =>
        {
            var s = new ProjectSession(project, store, Runtime, options.Value.Claude);
            s.RateLimitReceived += Usage.Apply;
            s.TurnCompleted += () => _ = Usage.RefreshAsync(); // free: /usage runs no model turn
            return s;
        });

    /// <summary>Stops the conversation's Claude Code / npm processes, then deletes all of its files.</summary>
    public async Task<ProjectCleanup.Report> DeleteProjectAsync(SpriteProject project)
    {
        if (_sessions.TryRemove(project.Id, out var session))
            await session.DisposeAsync();
        return await store.DeleteAsync(project);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _sessions.Values) await s.DisposeAsync();
        _sessions.Clear();
    }
}
