using System.Collections.Concurrent;

namespace Trysac.VirtualActorsLite;

/// <summary>
/// Represents a durable periodic reminder entry associated with a virtual actor.
/// </summary>
/// <param name="ActorId">The target actor identifier.</param>
/// <param name="Name">The reminder name (1 to 256 characters).</param>
/// <param name="NextDueAt">The UTC timestamp at which the reminder next fires.</param>
/// <param name="Period">The recurrence interval (at least 1 second).</param>
/// <param name="ActorFactory">Delegate used by a silo to instantiate the actor when not yet active.</param>
public sealed record ReminderEntry(
    ActorId ActorId,
    string Name,
    DateTimeOffset NextDueAt,
    TimeSpan Period,
    Func<Actor> ActorFactory);

/// <summary>
/// Pluggable store for durable reminders.
/// </summary>
public interface IReminderStore
{
    /// <summary>
    /// Creates or replaces a durable reminder.
    /// </summary>
    /// <param name="entry">The reminder configuration to store.</param>
    /// <returns>A task that completes when stored.</returns>
    Task UpsertAsync(ReminderEntry entry);

    /// <summary>
    /// Removes a durable reminder if present.
    /// </summary>
    /// <param name="actorId">The owning actor identifier.</param>
    /// <param name="name">The reminder name.</param>
    /// <returns>A task that completes when removed.</returns>
    Task RemoveAsync(ActorId actorId, string name);

    /// <summary>
    /// Returns a snapshot of all currently registered reminders across all actors.
    /// </summary>
    /// <returns>A read-only list of reminder entries.</returns>
    Task<IReadOnlyList<ReminderEntry>> GetAllAsync();
}

/// <summary>
/// Thread-safe in-memory implementation of <see cref="IReminderStore"/>.
/// </summary>
public sealed class InMemoryReminderStore : IReminderStore
{
    /// <summary>Default maximum number of reminders allowed in the store.</summary>
    public const int DefaultMaxEntries = 100_000;

    private readonly ConcurrentDictionary<(ActorId ActorId, string Name), ReminderEntry> _reminders = new();
    private readonly int _maxEntries;

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryReminderStore"/> class.
    /// </summary>
    /// <param name="maxEntries">Maximum number of reminders allowed.</param>
    public InMemoryReminderStore(int maxEntries = DefaultMaxEntries)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxEntries, 0);
        _maxEntries = maxEntries;
    }

    /// <inheritdoc />
    public Task UpsertAsync(ReminderEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ActorId.Validate(entry.ActorId);
        ActorId.ValidateKey(entry.Name);

        var key = (entry.ActorId, entry.Name);
        if (!_reminders.ContainsKey(key) && _reminders.Count >= _maxEntries)
        {
            throw new ActorException(ActorException.SiloUnavailable, "Reminder store capacity exceeded.");
        }

        _reminders[key] = entry;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task RemoveAsync(ActorId actorId, string name)
    {
        ActorId.Validate(actorId);
        ActorId.ValidateKey(name);
        _reminders.TryRemove((actorId, name), out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ReminderEntry>> GetAllAsync()
    {
        IReadOnlyList<ReminderEntry> snapshot = _reminders.Values.ToArray();
        return Task.FromResult(snapshot);
    }
}
