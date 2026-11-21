using System.Collections.Concurrent;

namespace RoomControl.Sim.Services;

/// <summary>
/// Runs a unit of work at most once per key, for QoS1-style redeliveries. It never makes a
/// caller wait for another caller, so it cannot deadlock (inline re-entry from a bus handler,
/// cross-key cycles) and never blocks on someone else's cancellation:
/// <list type="bullet">
/// <item>Duplicate of a completed attempt → returns false (no-op).</item>
/// <item>Duplicate while an attempt is in flight → returns false immediately and records a
/// pending retry. If that in-flight attempt later fails, the gate runs the pending retry once in
/// the background. The background retry gets no further retry of its own (budget: one), so a
/// request that keeps failing — even one whose handler redelivers itself inline — cannot loop;
/// after that the failure is only reported and the client must send again.</item>
/// <item>Failed attempt → the key is released, so a later redelivery runs the work again.</item>
/// </list>
/// Records are in-memory only and unbounded (see README: production needs a shared store with a TTL).
/// </summary>
internal sealed class IdempotencyGate<TKey> where TKey : notnull
{
    private sealed class Attempt
    {
        public bool Completed;           // work succeeded
        public bool Failed;              // work threw and the key was released
        public Func<Task>? PendingRetry; // set by a duplicate that arrived while in flight
    }

    private readonly ConcurrentDictionary<TKey, Attempt> _attempts = new();

    // Keys whose background retry is currently running; a failure inside it does not schedule another.
    private readonly ConcurrentDictionary<TKey, byte> _retrying = new();
    private readonly Action<TKey, Exception> _onBackgroundRetryFailed;

    public IdempotencyGate(Action<TKey, Exception> onBackgroundRetryFailed)
    {
        _onBackgroundRetryFailed = onBackgroundRetryFailed;
    }

    /// <summary>
    /// Runs <paramref name="work"/> unless this key is already done or in flight.
    /// <paramref name="retry"/> is what to run later if this call is a duplicate of an in-flight
    /// attempt that ends up failing (normally: handle the same request again).
    /// Returns true if this call performed the work.
    /// </summary>
    public async Task<bool> RunOnceAsync(TKey key, Func<Task> work, Func<Task> retry)
    {
        while (true)
        {
            var mine = new Attempt();
            var existing = _attempts.GetOrAdd(key, mine);
            if (!ReferenceEquals(existing, mine))
            {
                lock (existing)
                {
                    if (existing.Completed)
                    {
                        return false;
                    }

                    if (!existing.Failed)
                    {
                        existing.PendingRetry = retry; // in flight: retried only if it fails
                        return false;
                    }
                }

                continue; // it failed and released the key between GetOrAdd and lock: run it ourselves
            }

            try
            {
                await work().ConfigureAwait(false);
            }
            catch
            {
                Func<Task>? pending;
                lock (mine)
                {
                    mine.Failed = true;
                    pending = mine.PendingRetry;
                    mine.PendingRetry = null;
                    _attempts.TryRemove(new KeyValuePair<TKey, Attempt>(key, mine));
                }

                if (pending is not null && !_retrying.ContainsKey(key))
                {
                    _ = Task.Run(() => RunBackgroundRetryAsync(key, pending));
                }

                throw;
            }

            lock (mine)
            {
                mine.Completed = true;
                mine.PendingRetry = null;
            }

            return true;
        }
    }

    private async Task RunBackgroundRetryAsync(TKey key, Func<Task> retry)
    {
        _retrying[key] = 0;
        try
        {
            await retry().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Retry budget spent: report only. The key was released, so a later redelivery from the
            // client can still run the work again.
            _onBackgroundRetryFailed(key, ex);
        }
        finally
        {
            _retrying.TryRemove(key, out _);
        }
    }
}
