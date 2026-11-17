using System.Text.Json;

namespace Trysac.VirtualActorsLite;

/// <summary>
/// Provides an active <see cref="Actor"/> with access to its identity, hosting silo, persistent state, reminders, and peer actors.
/// </summary>
public sealed class ActorContext
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        MaxDepth = 32,
    };

    private readonly IStateStore _stateStore;
    private readonly IReminderStore _reminderStore;
    private readonly ManualClock _clock;
    private readonly IActorClusterDispatcher _cluster;
    private readonly Func<Actor> _actorFactory;
    private string? _currentEtag;
    private bool _hasLoadedEtag;

    internal ActorContext(
        ActorId id,
        string siloId,
        IStateStore stateStore,
        IReminderStore reminderStore,
        ManualClock clock,
        IActorClusterDispatcher cluster,
        Func<Actor> actorFactory)
    {
        Id = id;
        SiloId = siloId;
        _stateStore = stateStore;
        _reminderStore = reminderStore;
        _clock = clock;
        _cluster = cluster;
        _actorFactory = actorFactory;
    }

    /// <summary>Gets the unique identifier of this actor.</summary>
    public ActorId Id { get; }

    /// <summary>Gets the identifier of the silo hosting this activation.</summary>
    public string SiloId { get; }

    /// <summary>Gets or sets the last known state etag for this activation.</summary>
    internal string? CurrentEtag
    {
        get => _currentEtag;
        set
        {
            _currentEtag = value;
            _hasLoadedEtag = true;
        }
    }

    /// <summary>
    /// Reads the actor's persisted state from the configured <see cref="IStateStore"/> and updates the context's tracked etag.
    /// </summary>
    /// <typeparam name="T">The state type to deserialize.</typeparam>
    /// <returns>The deserialized state, or <c>default</c> if no state has been saved yet.</returns>
    public async Task<T?> ReadStateAsync<T>()
    {
        var (data, etag) = await _stateStore.ReadAsync(Id).ConfigureAwait(false);
        _currentEtag = etag;
        _hasLoadedEtag = true;
        if (data is null || data.Length == 0)
        {
            return default;
        }

        return JsonSerializer.Deserialize<T>(data, SerializerOptions);
    }

    /// <summary>
    /// Serializes and writes <paramref name="state"/> to the configured <see cref="IStateStore"/> using the etag from the last read or write.
    /// </summary>
    /// <typeparam name="T">The state type to serialize.</typeparam>
    /// <param name="state">The state value to persist.</param>
    /// <returns>A task that completes when the state is persisted and the tracked etag is updated.</returns>
    /// <exception cref="ActorException">Thrown with code <c>etag_conflict</c> when the stored etag does not match the context's tracked etag.</exception>
    public async Task WriteStateAsync<T>(T state)
    {
        if (!_hasLoadedEtag)
        {
            var (_, existingEtag) = await _stateStore.ReadAsync(Id).ConfigureAwait(false);
            _currentEtag = existingEtag;
            _hasLoadedEtag = true;
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, SerializerOptions);
        var newEtag = await _stateStore.WriteAsync(Id, bytes, _currentEtag).ConfigureAwait(false);
        _currentEtag = newEtag;
    }

    /// <summary>
    /// Registers or updates a durable periodic reminder for this actor.
    /// </summary>
    /// <param name="name">The reminder name (1 to 256 characters).</param>
    /// <param name="dueTime">The non-negative delay before the first firing.</param>
    /// <param name="period">The recurrence period (must be at least 1 second).</param>
    /// <returns>A task that completes when the reminder is saved.</returns>
    /// <exception cref="ActorException">Thrown with code <c>invalid_period</c> if <paramref name="period"/> is less than 1 second or <paramref name="dueTime"/> is negative, or <c>invalid_key</c> if <paramref name="name"/> is invalid.</exception>
    public Task RegisterReminderAsync(string name, TimeSpan dueTime, TimeSpan period)
    {
        ActorId.ValidateKey(name);
        if (dueTime < TimeSpan.Zero || period < TimeSpan.FromSeconds(1))
        {
            throw new ActorException(
                ActorException.InvalidPeriod,
                "Reminder period must be at least 1 second and dueTime must be non-negative.");
        }

        var nextDue = _clock.UtcNow.Add(dueTime);
        var entry = new ReminderEntry(Id, name, nextDue, period, _actorFactory);
        return _reminderStore.UpsertAsync(entry);
    }

    /// <summary>
    /// Unregisters a previously registered reminder by name.
    /// </summary>
    /// <param name="name">The reminder name to remove.</param>
    /// <returns>A task that completes when the reminder is removed.</returns>
    public Task UnregisterReminderAsync(string name)
    {
        ActorId.ValidateKey(name);
        return _reminderStore.RemoveAsync(Id, name);
    }

    /// <summary>
    /// Gets a typed reference to another virtual actor in the cluster.
    /// </summary>
    /// <typeparam name="TActor">The target actor class.</typeparam>
    /// <param name="key">The target actor key (1 to 256 characters).</param>
    /// <returns>An <see cref="ActorRef{TActor}"/> that routes calls through the cluster.</returns>
    public ActorRef<TActor> GetActor<TActor>(string key)
        where TActor : Actor, new()
    {
        return _cluster.GetActor<TActor>(key);
    }
}
