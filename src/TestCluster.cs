using System.Globalization;

namespace Trysac.VirtualActorsLite;

/// <summary>
/// An in-process multi-silo virtual actor cluster sharing directory, state, and reminder stores for deterministic testing and execution.
/// </summary>
public sealed class TestCluster : IActorClusterDispatcher
{
    private readonly ClusterOptions _options;
    private readonly ManualClock _clock;
    private readonly IActivationDirectory _directory;
    private readonly IStateStore _stateStore;
    private readonly IReminderStore _reminderStore;
    private readonly Dictionary<string, Silo> _silosById = new(StringComparer.Ordinal);
    private readonly List<string> _liveSiloIds = [];
    private readonly object _clusterLock = new();
    private long _roundRobinCounter = -1;

    private TestCluster(int siloCount, ClusterOptions options)
    {
        _options = options;
        _clock = options.Clock ?? new ManualClock();
        _directory = options.Directory ?? new InMemoryActivationDirectory();
        _stateStore = options.StateStore ?? new InMemoryStateStore();
        _reminderStore = options.ReminderStore ?? new InMemoryReminderStore();

        for (var i = 1; i <= siloCount; i++)
        {
            var siloId = $"s{i.ToString(CultureInfo.InvariantCulture)}";
            var silo = new Silo(
                siloId,
                _directory,
                _stateStore,
                _reminderStore,
                _clock,
                options.MailboxCapacity,
                options.IdleTimeout,
                this,
                ResolveSilo);
            _silosById[siloId] = silo;
            _liveSiloIds.Add(siloId);
        }
    }

    /// <summary>
    /// Gets the currently live silo identifiers in the cluster (e.g. <c>"s1"</c>, <c>"s2"</c>, ...).
    /// </summary>
    public IReadOnlyList<string> Silos
    {
        get
        {
            lock (_clusterLock)
            {
                return _liveSiloIds.ToArray();
            }
        }
    }

    /// <summary>Gets the manual clock driving this cluster.</summary>
    public ManualClock Clock => _clock;

    /// <summary>Gets the shared activation directory of this cluster.</summary>
    public IActivationDirectory Directory => _directory;

    /// <summary>Gets the shared persistent state store of this cluster.</summary>
    public IStateStore StateStore => _stateStore;

    /// <summary>Gets the shared durable reminder store of this cluster.</summary>
    public IReminderStore ReminderStore => _reminderStore;

    /// <summary>
    /// Creates a new <see cref="TestCluster"/> with <paramref name="silos"/> silos and the specified <paramref name="options"/>.
    /// </summary>
    /// <param name="silos">The number of silos to start (at least 1).</param>
    /// <param name="options">The cluster configuration options.</param>
    /// <returns>The initialized <see cref="TestCluster"/>.</returns>
    public static TestCluster Create(int silos, ClusterOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(silos, 0);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.IdleTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.MailboxCapacity, 0);
        return new TestCluster(silos, options);
    }

    /// <summary>
    /// Obtains a typed <see cref="ActorRef{TActor}"/> for the actor of type <typeparamref name="TActor"/> and <paramref name="key"/>.
    /// </summary>
    /// <typeparam name="TActor">The actor class type.</typeparam>
    /// <param name="key">The actor instance key (1 to 256 characters).</param>
    /// <returns>An <see cref="ActorRef{TActor}"/> bound to this cluster.</returns>
    /// <exception cref="ActorException">Thrown with code <c>invalid_key</c> when <paramref name="key"/> is invalid.</exception>
    public ActorRef<TActor> GetActor<TActor>(string key)
        where TActor : Actor, new()
    {
        ActorId.ValidateKey(key);
        var id = new ActorId(typeof(TActor).Name, key);
        return new ActorRef<TActor>(id, this);
    }

    /// <summary>
    /// Advances the cluster's <see cref="ManualClock"/> by <paramref name="by"/>, firing due reminders in chronological order
    /// and deactivating activations that exceed <see cref="ClusterOptions.IdleTimeout"/>.
    /// </summary>
    /// <param name="by">The non-negative duration to advance the clock.</param>
    /// <returns>A task that completes when all due reminders and idle deactivations have been processed.</returns>
    public async Task AdvanceAsync(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);
        var targetTime = _clock.UtcNow.Add(by);

        while (true)
        {
            var allReminders = await _reminderStore.GetAllAsync().ConfigureAwait(false);
            ReminderEntry? nextDue = null;
            foreach (var r in allReminders)
            {
                if (r.NextDueAt <= targetTime)
                {
                    if (nextDue is null ||
                        r.NextDueAt < nextDue.NextDueAt ||
                        (r.NextDueAt == nextDue.NextDueAt && string.CompareOrdinal(r.Name, nextDue.Name) < 0))
                    {
                        nextDue = r;
                    }
                }
            }

            if (nextDue is null)
            {
                break;
            }

            var delta = nextDue.NextDueAt - _clock.UtcNow;
            if (delta > TimeSpan.Zero)
            {
                _clock.Advance(delta);
            }

            await DeactivateAllIdleAsync(_clock.UtcNow).ConfigureAwait(false);

            var updatedEntry = nextDue with { NextDueAt = nextDue.NextDueAt.Add(nextDue.Period) };
            await _reminderStore.UpsertAsync(updatedEntry).ConfigureAwait(false);

            var targetSilo = PickSilo(nextDue.ActorId);
            var chain = CallChainContext.CaptureWith(nextDue.ActorId);
            await targetSilo.ExecuteReminderAsync(nextDue.ActorId, nextDue.Name, nextDue.ActorFactory, chain).ConfigureAwait(false);
        }

        var remaining = targetTime - _clock.UtcNow;
        if (remaining > TimeSpan.Zero)
        {
            _clock.Advance(remaining);
        }

        await DeactivateAllIdleAsync(_clock.UtcNow).ConfigureAwait(false);
    }

    /// <summary>
    /// Gracefully deactivates the specified actor across whichever silo currently hosts it, running <c>OnDeactivateAsync</c> and unregistering it from the directory.
    /// </summary>
    /// <typeparam name="TActor">The actor type.</typeparam>
    /// <param name="key">The actor key.</param>
    /// <returns>A task that completes once the actor has deactivated.</returns>
    public async Task DeactivateAsync<TActor>(string key)
        where TActor : Actor, new()
    {
        ActorId.ValidateKey(key);
        var id = new ActorId(typeof(TActor).Name, key);
        Silo[] snapshot;
        lock (_clusterLock)
        {
            snapshot = _silosById.Values.ToArray();
        }

        foreach (var silo in snapshot)
        {
            await silo.DeactivateActorAsync(id).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Abruptly terminates the silo identified by <paramref name="siloId"/>, removing its directory registrations
    /// and failing any in-flight queued calls on that silo with <see cref="ActorException.SiloUnavailable"/>.
    /// </summary>
    /// <param name="siloId">The silo identifier to kill (e.g. <c>"s1"</c>).</param>
    public void KillSilo(string siloId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siloId);
        Silo? silo;
        lock (_clusterLock)
        {
            _liveSiloIds.Remove(siloId);
            _silosById.TryGetValue(siloId, out silo);
        }

        silo?.Kill();
    }

    /// <inheritdoc />
    async Task<TResult> IActorClusterDispatcher.DispatchCallAsync<TActor, TResult>(
        ActorId id,
        Func<TActor, Task<TResult>> call)
    {
        ActorId.Validate(id);
        if (CallChainContext.Contains(id))
        {
            throw new ActorException(
                ActorException.ReentrantCall,
                $"Reentrant call detected for actor '{id}'.");
        }

        var nextChain = CallChainContext.CaptureWith(id);
        var silo = PickSilo(id);
        return await silo.ExecuteCallAsync(id, call, nextChain).ConfigureAwait(false);
    }

    private Silo PickSilo(ActorId id)
    {
        string[] liveSnapshot;
        lock (_clusterLock)
        {
            if (_liveSiloIds.Count == 0)
            {
                throw new ActorException(ActorException.SiloUnavailable, "No live silos available in the cluster.");
            }

            liveSnapshot = _liveSiloIds.ToArray();
        }

        string chosenId;
        if (_options.Router is not null)
        {
            chosenId = _options.Router(id, liveSnapshot);
        }
        else
        {
            var index = (int)(Interlocked.Increment(ref _roundRobinCounter) % liveSnapshot.Length);
            if (index < 0)
            {
                index += liveSnapshot.Length;
            }

            chosenId = liveSnapshot[index];
        }

        var silo = ResolveSilo(chosenId);
        if (silo is null || silo.IsKilled)
        {
            throw new ActorException(ActorException.SiloUnavailable, $"Silo '{chosenId}' is unavailable.");
        }

        return silo;
    }

    private Silo? ResolveSilo(string siloId)
    {
        lock (_clusterLock)
        {
            return _silosById.TryGetValue(siloId, out var silo) ? silo : null;
        }
    }

    private async Task DeactivateAllIdleAsync(DateTimeOffset now)
    {
        Silo[] silos;
        lock (_clusterLock)
        {
            silos = _liveSiloIds.Select(id => _silosById[id]).ToArray();
        }

        foreach (var silo in silos)
        {
            await silo.DeactivateIdleActorsAsync(now).ConfigureAwait(false);
        }
    }
}
