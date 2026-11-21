using System.Collections.Concurrent;
using RoomControl.Sim.Services;

namespace RoomControl.Tests;

public class IdempotencyGateTests
{
    private static IdempotencyGate<T> NewGate<T>(ConcurrentBag<Exception>? retryErrors = null) where T : notnull =>
        new((_, ex) => retryErrors?.Add(ex));

    [Fact]
    public async Task Cross_key_cycle_does_not_deadlock()
    {
        var gate = NewGate<string>();
        var aIn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bIn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = Task.Run(() => gate.RunOnceAsync("A",
            async () => { aIn.SetResult(); await bIn.Task; await gate.RunOnceAsync("B", () => Task.CompletedTask, () => Task.CompletedTask); },
            () => Task.CompletedTask));
        var b = Task.Run(() => gate.RunOnceAsync("B",
            async () => { bIn.SetResult(); await aIn.Task; await gate.RunOnceAsync("A", () => Task.CompletedTask, () => Task.CompletedTask); },
            () => Task.CompletedTask));

        await Task.WhenAll(a, b).WaitAsync(TestSupport.WaitLimit, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Failed_attempt_releases_the_key_for_a_later_redelivery()
    {
        var gate = NewGate<string>();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gate.RunOnceAsync("k", () => throw new InvalidOperationException("x"), () => Task.CompletedTask));
        var ran = false;
        Assert.True(await gate.RunOnceAsync("k", () => { ran = true; return Task.CompletedTask; }, () => Task.CompletedTask));
        Assert.True(ran);
        Assert.False(await gate.RunOnceAsync("k", () => throw new Exception("must not run"), () => Task.CompletedTask));
    }

    [Fact]
    public async Task Stress_never_overlaps_and_succeeds_at_most_once_per_key()
    {
        var retryErrors = new ConcurrentBag<Exception>();
        for (var round = 0; round < 10; round++)
        {
            var gate = NewGate<int>(retryErrors);
            var running = new ConcurrentDictionary<int, int>();
            var successes = new ConcurrentDictionary<int, int>();
            var overlap = 0;
            var rnd = new ThreadLocal<Random>(() => new Random(Guid.NewGuid().GetHashCode()));

            async Task Work(int key)
            {
                if (running.AddOrUpdate(key, 1, (_, v) => v + 1) > 1) Interlocked.Increment(ref overlap);
                try
                {
                    await Task.Delay(rnd.Value!.Next(0, 3));
                    if (rnd.Value!.NextDouble() < 0.6) throw new InvalidOperationException("injected");
                    successes.AddOrUpdate(key, 1, (_, v) => v + 1);
                }
                finally { running.AddOrUpdate(key, 0, (_, v) => v - 1); }
            }

            Task Handle(int key) => gate.RunOnceAsync(key, () => Work(key), () => Handle(key));

            await Task.WhenAll(Enumerable.Range(0, 2000).Select(i => Task.Run(async () =>
            {
                try { await Handle(i % 20); } catch (InvalidOperationException) { }
            })));
            await TestSupport.WaitUntilAsync(() => running.Values.All(v => v == 0), "background retries settled");
            await Task.Delay(50, TestContext.Current.CancellationToken);

            Assert.Equal(0, overlap);
            Assert.All(successes, kv => Assert.True(kv.Value <= 1, $"key {kv.Key} succeeded {kv.Value} times"));
        }
    }
}
