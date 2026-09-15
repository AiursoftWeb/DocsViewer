namespace Aiursoft.DocsViewer.Services.Agents;

public enum AgentLimitStatus { Acquired, RateLimited, ConcurrencyLimited }
public sealed record AgentLimitResult(AgentLimitStatus Status, AgentRequestLimiter.Lease? Lease);

/// <summary>Process-local limits. Idle user entries expire after one minute.</summary>
public sealed class AgentRequestLimiter(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly object sync = new();
    private readonly Dictionary<string, UserState> users = new(StringComparer.Ordinal);
    private int active;

    public ValueTask<AgentLimitResult> AcquireAsync(string userKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(userKey);
        lock (sync)
        {
            var now = clock.GetUtcNow();
            foreach (var pair in users.ToArray())
            {
                while (pair.Value.Starts.TryPeek(out var started) && now - started >= TimeSpan.FromMinutes(1))
                    pair.Value.Starts.Dequeue();
                if (!pair.Value.InFlight && pair.Value.Starts.Count == 0) users.Remove(pair.Key);
            }
            if (!users.TryGetValue(userKey, out var state)) state = new UserState();
            if (state.InFlight || active >= 4)
                return ValueTask.FromResult(new AgentLimitResult(AgentLimitStatus.ConcurrencyLimited, null));
            if (state.Starts.Count >= 3)
                return ValueTask.FromResult(new AgentLimitResult(AgentLimitStatus.RateLimited, null));
            state.Starts.Enqueue(now);
            state.InFlight = true;
            users[userKey] = state;
            active++;
            return ValueTask.FromResult(new AgentLimitResult(AgentLimitStatus.Acquired, new Lease(() => Release(state))));
        }
    }

    public async ValueTask<Lease?> TryAcquireAsync(string userKey, CancellationToken cancellationToken = default) =>
        (await AcquireAsync(userKey, cancellationToken)).Lease;

    private void Release(UserState state)
    {
        lock (sync)
        {
            state.InFlight = false;
            active--;
        }
    }

    public sealed class Lease(Action release) : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) release();
        }
    }

    private sealed class UserState
    {
        public bool InFlight;
        public Queue<DateTimeOffset> Starts { get; } = new();
    }
}
