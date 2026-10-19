using System.Collections.Concurrent;

namespace FC;

public sealed class ThreadRuntime
{
    public string Url { get; set; } = "";
    public string Name { get; set; } = "";
    public string State { get; set; } = "En espera";
    public string? LastPostId { get; set; }
    public DateTimeOffset? FirstObserved { get; set; }
    public DateTimeOffset? LastActivity { get; set; }
    public DateTimeOffset NextCheck { get; set; }
    public int WaitMinutes { get; set; }
    public string? LastError { get; set; }
}

public sealed class RuntimeState
{
    public ConcurrentDictionary<string, ThreadRuntime> Threads { get; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;
    public string LoginState { get; set; } = "No comprobada";
    public string? LastGlobalError { get; set; }
    public object Snapshot() => new
    {
        startedAt = StartedAt,
        loginState = LoginState,
        lastGlobalError = LastGlobalError,
        threads = Threads.Values.OrderBy(x => x.Name).Select(x => new
        {
            x.Name, x.Url, x.State, x.LastPostId, x.LastActivity,
            x.NextCheck, x.WaitMinutes, x.LastError
        }).ToArray()
    };
}
