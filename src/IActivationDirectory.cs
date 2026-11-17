using System.Collections.Concurrent;

namespace Trysac.VirtualActorsLite;

/// <summary>
/// Pluggable single-activation directory mapping each active <see cref="ActorId"/> to its owning silo.
/// </summary>
public interface IActivationDirectory
{
    /// <summary>
    /// Attempts to register <paramref name="siloId"/> as the owner of <paramref name="id"/>.
    /// If <paramref name="id"/> is already registered, returns <see langword="false"/> (or <see langword="true"/> if owned by the same silo)
    /// and outputs the current <paramref name="owner"/>.
    /// </summary>
    /// <param name="id">The actor identifier.</param>
    /// <param name="siloId">The silo attempting to own the actor.</param>
    /// <param name="owner">The silo that owns the actor after the operation.</param>
    /// <returns><see langword="true"/> if <paramref name="siloId"/> is the owner; otherwise <see langword="false"/>.</returns>
    bool TryRegister(ActorId id, string siloId, out string owner);

    /// <summary>
    /// Unregisters <paramref name="id"/> if and only if it is currently owned by <paramref name="siloId"/>.
    /// </summary>
    /// <param name="id">The actor identifier.</param>
    /// <param name="siloId">The expected owning silo identifier.</param>
    void Unregister(ActorId id, string siloId);

    /// <summary>
    /// Removes all directory entries owned by <paramref name="siloId"/>.
    /// </summary>
    /// <param name="siloId">The silo identifier to purge.</param>
    void RemoveSilo(string siloId);
}

/// <summary>
/// Thread-safe in-memory implementation of <see cref="IActivationDirectory"/> with a maximum entry bound.
/// </summary>
public sealed class InMemoryActivationDirectory : IActivationDirectory
{
    /// <summary>Default maximum number of active directory entries.</summary>
    public const int DefaultMaxEntries = 100_000;

    private readonly ConcurrentDictionary<ActorId, string> _owners = new();
    private readonly int _maxEntries;

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryActivationDirectory"/> class.
    /// </summary>
    /// <param name="maxEntries">Maximum number of simultaneous entries allowed.</param>
    public InMemoryActivationDirectory(int maxEntries = DefaultMaxEntries)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxEntries, 0);
        _maxEntries = maxEntries;
    }

    /// <summary>Gets the current number of registered actors in the directory.</summary>
    public int Count => _owners.Count;

    /// <summary>Checks whether the specified <paramref name="id"/> currently has an owner in the directory.</summary>
    /// <param name="id">The actor identifier.</param>
    /// <param name="owner">The owning silo if found.</param>
    /// <returns><see langword="true"/> if registered; otherwise <see langword="false"/>.</returns>
    public bool TryGetOwner(ActorId id, out string? owner)
    {
        ActorId.Validate(id);
        return _owners.TryGetValue(id, out owner);
    }

    /// <inheritdoc />
    public bool TryRegister(ActorId id, string siloId, out string owner)
    {
        ActorId.Validate(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(siloId);

        if (_owners.TryGetValue(id, out var existing))
        {
            owner = existing;
            return string.Equals(existing, siloId, StringComparison.Ordinal);
        }

        if (_owners.Count >= _maxEntries)
        {
            throw new ActorException(ActorException.SiloUnavailable, "Activation directory capacity exceeded.");
        }

        owner = _owners.GetOrAdd(id, siloId);
        return string.Equals(owner, siloId, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public void Unregister(ActorId id, string siloId)
    {
        ActorId.Validate(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(siloId);
        ((ICollection<KeyValuePair<ActorId, string>>)_owners).Remove(new KeyValuePair<ActorId, string>(id, siloId));
    }

    /// <inheritdoc />
    public void RemoveSilo(string siloId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(siloId);
        foreach (var pair in _owners)
        {
            if (string.Equals(pair.Value, siloId, StringComparison.Ordinal))
            {
                ((ICollection<KeyValuePair<ActorId, string>>)_owners).Remove(pair);
            }
        }
    }
}
