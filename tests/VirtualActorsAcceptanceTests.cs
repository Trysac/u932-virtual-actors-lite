using Xunit;

namespace Trysac.VirtualActorsLite.Tests;

/// <summary>
/// Acceptance and adversarial tests verifying all specification requirements for Trysac.VirtualActorsLite.
/// </summary>
public class VirtualActorsAcceptanceTests
{
    [Fact]
    public async Task Concurrent500CallsRoundRobinOver3SilosActivatesOnceAndNeverInterleaves()
    {
        CounterActor.ResetMetrics();
        var cluster = TestCluster.Create(3, new ClusterOptions(TimeSpan.FromSeconds(120)));

        var tasks = new Task<int>[500];
        for (var i = 0; i < 500; i++)
        {
            tasks[i] = Task.Run(() => cluster.GetActor<CounterActor>("k1").CallAsync(a => a.Increment()));
        }

        await Task.WhenAll(tasks);
        var finalCount = await cluster.GetActor<CounterActor>("k1").CallAsync(a => a.GetCount());

        Assert.Equal(1, CounterActor.ActivateCount);
        Assert.Equal(500, finalCount);
        Assert.Equal(1, CounterActor.MaxConcurrentObserved);
    }

    [Fact]
    public async Task SingleActivation50SeededRoundsSeed13NeverShowsTwoLiveActivationsAtOnce()
    {
        var ledger = new ActivationLedger();
        LedgerActor.CurrentLedger = ledger;

        for (var round = 0; round < 50; round++)
        {
            var rng = new Random(13 + round);
            var clock = new ManualClock();
            var options = new ClusterOptions(TimeSpan.FromSeconds(30), MailboxCapacity: 1_000, Clock: clock)
            {
                Router = (_, liveSilos) => liveSilos[rng.Next(liveSilos.Count)],
            };
            var cluster = TestCluster.Create(3, options);

            var batch1 = Enumerable.Range(0, 15)
                .Select(_ => Task.Run(() => cluster.GetActor<LedgerActor>("k1").CallAsync(a => a.PingAsync())))
                .ToArray();
            await Task.WhenAll(batch1);

            if (round % 2 == 0)
            {
                await cluster.AdvanceAsync(TimeSpan.FromSeconds(31));
            }
            else
            {
                var ownerSilo = await cluster.GetActor<LedgerActor>("k1").CallAsync(a => a.GetSiloIdAsync());
                ledger.RecordSiloKill(ownerSilo);
                cluster.KillSilo(ownerSilo);
            }

            var batch2 = Enumerable.Range(0, 10)
                .Select(_ => Task.Run(() => cluster.GetActor<LedgerActor>("k1").CallAsync(a => a.PingAsync())))
                .ToArray();
            await Task.WhenAll(batch2);

            await cluster.AdvanceAsync(TimeSpan.FromSeconds(31));
        }

        Assert.Equal(0, ledger.OverlapViolations);
        Assert.True(ledger.MaxSimultaneousForSameId <= 1);
    }

    [Fact]
    public async Task StatePersistenceAcrossDeactivationAndSilosAndEtagConflictThrows()
    {
        var stateStore = new InMemoryStateStore();
        var currentTargetSilo = "s1";
        var cluster = TestCluster.Create(2, new ClusterOptions(TimeSpan.FromSeconds(60))
        {
            StateStore = stateStore,
            Router = (_, _) => currentTargetSilo,
        });

        await cluster.GetActor<StatefulActor>("k1").CallAsync(a => a.SetValueAsync("written-on-s1"));
        var firstSilo = await cluster.GetActor<StatefulActor>("k1").CallAsync(a => a.GetHostingSiloAsync());
        Assert.Equal("s1", firstSilo);

        await cluster.AdvanceAsync(TimeSpan.FromSeconds(61));

        currentTargetSilo = "s2";
        var readBack = await cluster.GetActor<StatefulActor>("k1").CallAsync(a => a.GetValueAsync());
        var secondSilo = await cluster.GetActor<StatefulActor>("k1").CallAsync(a => a.GetHostingSiloAsync());

        Assert.Equal("written-on-s1", readBack);
        Assert.Equal("s2", secondSilo);

        var actorId = new ActorId(nameof(StatefulActor), "k1");
        await stateStore.WriteAsync(actorId, "\"concurrent-external-update\""u8.ToArray(), expectedEtag: "v1");

        var ex = await Assert.ThrowsAsync<ActorException>(() =>
            cluster.GetActor<StatefulActor>("k1").CallAsync(a => a.OverwriteWithoutReadAsync("stale-write")));
        Assert.Equal("etag_conflict", ex.Code);
    }

    [Fact]
    public async Task RemindersTickEvery10sOver35sFires3TimesAcrossDeactivationAndInvalidPeriodThrows()
    {
        ReminderTestActor.Reset();
        var cluster = TestCluster.Create(2, new ClusterOptions(TimeSpan.FromSeconds(15)));
        var actorRef = cluster.GetActor<ReminderTestActor>("rem-1");

        await actorRef.CallAsync(a => a.ScheduleTickAsync("tick", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10)));

        await cluster.AdvanceAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, ReminderTestActor.TickCount);

        await cluster.DeactivateAsync<ReminderTestActor>("rem-1");
        Assert.Equal(1, ReminderTestActor.DeactivateCount);

        await cluster.AdvanceAsync(TimeSpan.FromSeconds(25));
        Assert.Equal(3, ReminderTestActor.TickCount);

        var invalidEx = await Assert.ThrowsAsync<ActorException>(() =>
            actorRef.CallAsync(a => a.ScheduleTickAsync("bad", TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(999))));
        Assert.Equal("invalid_period", invalidEx.Code);

        await actorRef.CallAsync(a => a.CancelTickAsync("tick"));
        await cluster.AdvanceAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(3, ReminderTestActor.TickCount);
    }

    [Fact]
    public async Task IdleTimeout120sWhenClockAdvances121sWithoutCallsDeactivatesOnceAndUnregistersFromDirectory()
    {
        IdleTrackingActor.Reset();
        var directory = new InMemoryActivationDirectory();
        var cluster = TestCluster.Create(2, new ClusterOptions(TimeSpan.FromSeconds(120))
        {
            Directory = directory,
        });

        await cluster.GetActor<IdleTrackingActor>("idle-1").CallAsync(a => a.TouchAsync());
        var id = new ActorId(nameof(IdleTrackingActor), "idle-1");
        Assert.True(directory.TryGetOwner(id, out _));

        await cluster.AdvanceAsync(TimeSpan.FromSeconds(121));

        Assert.Equal(1, IdleTrackingActor.DeactivateCount);
        Assert.False(directory.TryGetOwner(id, out _));
        Assert.Equal(0, directory.Count);
    }

    [Fact]
    public async Task KillSiloS1ActivatesK1OnAnotherSiloWithLastWrittenState()
    {
        var cluster = TestCluster.Create(3, new ClusterOptions(TimeSpan.FromSeconds(120))
        {
            Router = (_, live) => live[0],
        });

        await cluster.GetActor<StatefulActor>("k1").CallAsync(a => a.SetValueAsync("persisted-before-kill"));
        var initialSilo = await cluster.GetActor<StatefulActor>("k1").CallAsync(a => a.GetHostingSiloAsync());
        Assert.Equal("s1", initialSilo);

        cluster.KillSilo("s1");
        Assert.DoesNotContain("s1", cluster.Silos);

        var recoveredState = await cluster.GetActor<StatefulActor>("k1").CallAsync(a => a.GetValueAsync());
        var newSilo = await cluster.GetActor<StatefulActor>("k1").CallAsync(a => a.GetHostingSiloAsync());

        Assert.Equal("persisted-before-kill", recoveredState);
        Assert.NotEqual("s1", newSilo);
    }

    [Fact]
    public async Task ReentrantCallDirectAndIndirectThrowsReentrantCallAnd1001stQueuedCallThrowsMailboxFull()
    {
        var cluster = TestCluster.Create(2, new ClusterOptions(TimeSpan.FromSeconds(120), MailboxCapacity: 1_000));

        var directEx = await Assert.ThrowsAsync<ActorException>(() =>
            cluster.GetActor<ReentrantActor>("a1").CallAsync(a => a.CallSelfAsync()));
        Assert.Equal("reentrant_call", directEx.Code);

        var indirectEx = await Assert.ThrowsAsync<ActorException>(() =>
            cluster.GetActor<ReentrantActor>("a1").CallAsync(a => a.CallViaPeerAsync("b1")));
        Assert.Equal("reentrant_call", indirectEx.Code);

        BlockingActor.ResetGates();
        var blockedRef = cluster.GetActor<BlockingActor>("blocked-1");
        var activeTask = blockedRef.CallAsync(a => a.WaitUntilReleasedAsync());
        await BlockingActor.EnteredGate.Task;

        var queuedTasks = new List<Task<int>>(1_000);
        for (var i = 0; i < 1_000; i++)
        {
            queuedTasks.Add(blockedRef.CallAsync(a => a.NoOpAsync(i)));
        }

        var overflowEx = await Assert.ThrowsAsync<ActorException>(() =>
            blockedRef.CallAsync(a => a.NoOpAsync(1_001)));
        Assert.Equal("mailbox_full", overflowEx.Code);

        BlockingActor.ReleaseGate.TrySetResult(true);
        await activeTask;
        var results = await Task.WhenAll(queuedTasks);
        Assert.Equal(1_000, results.Length);
    }

    [Fact]
    public async Task ExceptionInsideActorMethodReachesCallerUnchangedAndActorStaysActiveForNextCall()
    {
        var cluster = TestCluster.Create(2, new ClusterOptions(TimeSpan.FromSeconds(120)));
        var actorRef = cluster.GetActor<FaultyActor>("f1");

        var beforeCount = await actorRef.CallAsync(a => a.IncrementAndGetAsync());
        Assert.Equal(1, beforeCount);

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            actorRef.CallAsync(a => a.FailWithAsync("boom-exact-message")));
        Assert.Equal("boom-exact-message", thrown.Message);

        var afterCount = await actorRef.CallAsync(a => a.IncrementAndGetAsync());
        Assert.Equal(2, afterCount);
        var activations = await actorRef.CallAsync(a => a.GetActivationOrderAsync());
        Assert.Equal(1, activations);
    }

    [Fact]
    public async Task KillSiloWhileCallIsQueuedFailsQueuedCallWithSiloUnavailable()
    {
        BlockingActor.ResetGates();
        var cluster = TestCluster.Create(2, new ClusterOptions(TimeSpan.FromSeconds(120))
        {
            Router = (_, live) => live[0],
        });

        var actorRef = cluster.GetActor<BlockingActor>("k-kill");
        var firstCall = actorRef.CallAsync(a => a.WaitUntilReleasedAsync());
        await BlockingActor.EnteredGate.Task;

        var queuedCall = actorRef.CallAsync(a => a.NoOpAsync(42));
        cluster.KillSilo("s1");

        var ex = await Assert.ThrowsAsync<ActorException>(() => queuedCall);
        Assert.Equal("silo_unavailable", ex.Code);

        BlockingActor.ReleaseGate.TrySetResult(true);
        await firstCall;
    }

    [Fact]
    public async Task DeactivationRacingWithCallRetriesCallOnFreshActivationWithoutLosingIt()
    {
        var cluster = TestCluster.Create(2, new ClusterOptions(TimeSpan.FromSeconds(30)));
        var actorRef = cluster.GetActor<StatefulActor>("race-1");
        await actorRef.CallAsync(a => a.SetValueAsync("v1"));

        var deactivateTask = cluster.DeactivateAsync<StatefulActor>("race-1");
        var concurrentCallTask = Task.Run(() => actorRef.CallAsync(a => a.GetValueAsync()));

        await Task.WhenAll(deactivateTask, concurrentCallTask);
        Assert.Equal("v1", await concurrentCallTask);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void InvalidActorKeyEmptyOrNullThrowsInvalidKey(string? invalidKey)
    {
        var cluster = TestCluster.Create(1, new ClusterOptions(TimeSpan.FromSeconds(60)));
        var ex = Assert.Throws<ActorException>(() => cluster.GetActor<CounterActor>(invalidKey!));
        Assert.Equal("invalid_key", ex.Code);
    }

    [Fact]
    public void InvalidActorKeyOver256CharsThrowsInvalidKey()
    {
        var cluster = TestCluster.Create(1, new ClusterOptions(TimeSpan.FromSeconds(60)));
        var tooLong = new string('k', 257);
        var ex = Assert.Throws<ActorException>(() => cluster.GetActor<CounterActor>(tooLong));
        Assert.Equal("invalid_key", ex.Code);
    }

    private sealed class CounterActor : Actor
    {
        private static int _activateCount;
        private static int _currentRunning;
        private static int _maxConcurrentObserved;
        private int _count;

        public static int ActivateCount => Volatile.Read(ref _activateCount);

        public static int MaxConcurrentObserved => Volatile.Read(ref _maxConcurrentObserved);

        public static void ResetMetrics()
        {
            Interlocked.Exchange(ref _activateCount, 0);
            Interlocked.Exchange(ref _currentRunning, 0);
            Interlocked.Exchange(ref _maxConcurrentObserved, 0);
        }

        public override Task OnActivateAsync()
        {
            Interlocked.Increment(ref _activateCount);
            return Task.CompletedTask;
        }

        public async Task<int> Increment()
        {
            var nowRunning = Interlocked.Increment(ref _currentRunning);
            UpdateMax(nowRunning);
            try
            {
                await Task.Yield();
                _count++;
                return _count;
            }
            finally
            {
                Interlocked.Decrement(ref _currentRunning);
            }
        }

        public Task<int> GetCount() => Task.FromResult(_count);

        private static void UpdateMax(int candidate)
        {
            while (true)
            {
                var currentMax = Volatile.Read(ref _maxConcurrentObserved);
                if (candidate <= currentMax)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref _maxConcurrentObserved, candidate, currentMax) == currentMax)
                {
                    return;
                }
            }
        }
    }

    private sealed class ActivationLedger
    {
        private readonly object _gate = new();
        private readonly Dictionary<ActorId, HashSet<string>> _liveSilosById = new();

        public int OverlapViolations { get; private set; }

        public int MaxSimultaneousForSameId { get; private set; }

        public void RecordActivate(ActorId id, string siloId)
        {
            lock (_gate)
            {
                if (!_liveSilosById.TryGetValue(id, out var set))
                {
                    set = new HashSet<string>(StringComparer.Ordinal);
                    _liveSilosById[id] = set;
                }

                set.Add(siloId);
                if (set.Count > MaxSimultaneousForSameId)
                {
                    MaxSimultaneousForSameId = set.Count;
                }

                if (set.Count > 1)
                {
                    OverlapViolations++;
                }
            }
        }

        public void RecordDeactivate(ActorId id, string siloId)
        {
            lock (_gate)
            {
                if (_liveSilosById.TryGetValue(id, out var set))
                {
                    set.Remove(siloId);
                }
            }
        }

        public void RecordSiloKill(string siloId)
        {
            lock (_gate)
            {
                foreach (var set in _liveSilosById.Values)
                {
                    set.Remove(siloId);
                }
            }
        }
    }

    private sealed class LedgerActor : Actor
    {
        public static ActivationLedger? CurrentLedger { get; set; }

        public override Task OnActivateAsync()
        {
            CurrentLedger?.RecordActivate(Context.Id, Context.SiloId);
            return Task.CompletedTask;
        }

        public override Task OnDeactivateAsync()
        {
            CurrentLedger?.RecordDeactivate(Context.Id, Context.SiloId);
            return Task.CompletedTask;
        }

        public Task<string> GetSiloIdAsync() => Task.FromResult(Context.SiloId);

        public Task PingAsync()
        {
            _ = Context.Id;
            return Task.CompletedTask;
        }
    }

    private sealed class StatefulActor : Actor
    {
        public async Task SetValueAsync(string value)
        {
            await Context.WriteStateAsync(value);
        }

        public async Task<string?> GetValueAsync()
        {
            return await Context.ReadStateAsync<string>();
        }

        public async Task OverwriteWithoutReadAsync(string value)
        {
            await Context.WriteStateAsync(value);
        }

        public Task<string> GetHostingSiloAsync() => Task.FromResult(Context.SiloId);
    }

    private sealed class ReminderTestActor : Actor
    {
        private static int _tickCount;
        private static int _deactivateCount;

        public static int TickCount => Volatile.Read(ref _tickCount);

        public static int DeactivateCount => Volatile.Read(ref _deactivateCount);

        public static void Reset()
        {
            Interlocked.Exchange(ref _tickCount, 0);
            Interlocked.Exchange(ref _deactivateCount, 0);
        }

        public Task ScheduleTickAsync(string name, TimeSpan due, TimeSpan period)
            => Context.RegisterReminderAsync(name, due, period);

        public Task CancelTickAsync(string name)
            => Context.UnregisterReminderAsync(name);

        public override Task OnReminderAsync(string name)
        {
            if (name == "tick")
            {
                Interlocked.Increment(ref _tickCount);
            }

            return Task.CompletedTask;
        }

        public override Task OnDeactivateAsync()
        {
            Interlocked.Increment(ref _deactivateCount);
            return Task.CompletedTask;
        }
    }

    private sealed class IdleTrackingActor : Actor
    {
        private static int _deactivateCount;

        public static int DeactivateCount => Volatile.Read(ref _deactivateCount);

        public static void Reset() => Interlocked.Exchange(ref _deactivateCount, 0);

        public Task TouchAsync()
        {
            _ = Context.Id;
            return Task.CompletedTask;
        }

        public override Task OnDeactivateAsync()
        {
            Interlocked.Increment(ref _deactivateCount);
            return Task.CompletedTask;
        }
    }

    private sealed class ReentrantActor : Actor
    {
        public Task CallSelfAsync()
        {
            var self = Context.GetActor<ReentrantActor>(Context.Id.Key);
            return self.CallAsync(a => a.NoOpAsync());
        }

        public Task CallViaPeerAsync(string peerKey)
        {
            var peer = Context.GetActor<PeerRelayActor>(peerKey);
            return peer.CallAsync(p => p.CallbackOriginalAsync(Context.Id.Key));
        }

        public Task NoOpAsync()
        {
            _ = Context.Id;
            return Task.CompletedTask;
        }
    }

    private sealed class PeerRelayActor : Actor
    {
        public Task CallbackOriginalAsync(string originalKey)
        {
            var original = Context.GetActor<ReentrantActor>(originalKey);
            return original.CallAsync(a => a.NoOpAsync());
        }
    }

    private sealed class BlockingActor : Actor
    {
        public static TaskCompletionSource<bool> EnteredGate { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static TaskCompletionSource<bool> ReleaseGate { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static void ResetGates()
        {
            EnteredGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ReleaseGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public async Task WaitUntilReleasedAsync()
        {
            _ = Context.Id;
            EnteredGate.TrySetResult(true);
            await ReleaseGate.Task;
        }

        public Task<int> NoOpAsync(int value) => Task.FromResult(value + Context.Id.Key.Length - Context.Id.Key.Length);
    }

    private sealed class FaultyActor : Actor
    {
        private static int _activations;
        private int _counter;

        public override Task OnActivateAsync()
        {
            Interlocked.Increment(ref _activations);
            return Task.CompletedTask;
        }

        public Task<int> IncrementAndGetAsync() => Task.FromResult(++_counter);

        public Task FailWithAsync(string message)
        {
            _ = Context.Id;
            throw new InvalidOperationException(message);
        }

        public Task<int> GetActivationOrderAsync() => Task.FromResult(Volatile.Read(ref _activations) + Context.Id.Key.Length - Context.Id.Key.Length);
    }
}
