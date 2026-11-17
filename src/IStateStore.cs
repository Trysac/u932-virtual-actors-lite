using System.Collections.Concurrent;
using System.Globalization;

namespace Trysac.VirtualActorsLite;

/// <summary>
/// Pluggable persistent state store with optimistic concurrency control via opaque etags.
/// </summary>
public interface IStateStore
{
    /// <summary>
    /// Reads the serialized state and current etag for <paramref name="id"/>.
    /// Returns <c>(null, null)</c> if no state has been written yet.
    /// </summary>
    /// <param name="id">The actor identifier.</param>
    /// <returns>A tuple containing the raw state bytes and opaque etag.</returns>
    Task<(byte[]? Data, string? Etag)> ReadAsync(ActorId id);

    /// <summary>
    /// Writes serialized state for <paramref name="id"/> checking <paramref name="expectedEtag"/>.
    /// </summary>
    /// <param name="id">The actor identifier.</param>
    /// <param name="data">The serialized state bytes.</param>
    /// <param name="expectedEtag">The etag from the previous read or write, or <see langword="null"/> if writing initial state.</param>
    /// <returns>The newly assigned opaque etag.</returns>
    /// <exception cref="ActorException">Thrown with code <c>etag_conflict</c> when <paramref name="expectedEtag"/> does not match the current store etag.</exception>
    Task<string> WriteAsync(ActorId id, byte[] data, string? expectedEtag);
}

/// <summary>
/// Thread-safe in-memory implementation of <see cref="IStateStore"/> enforcing payload size and entry limits.
/// </summary>
public sealed class InMemoryStateStore : IStateStore
{
    /// <summary>Maximum allowed serialized state size in bytes (1 MiB).</summary>
    public const int MaxStateBytes = 1_048_576;

    /// <summary>Default maximum number of distinct actor states stored.</summary>
    public const int DefaultMaxEntries = 100_000;

    private readonly ConcurrentDictionary<ActorId, Entry> _entries = new();
    private readonly object _writeGate = new();
    private readonly int _maxEntries;
    private long _etagSequence;

    /// <summary>
    /// Initializes a new instance of the <see cref="InMemoryStateStore"/> class.
    /// </summary>
    /// <param name="maxEntries">Maximum number of distinct actors stored before least-recently-updated eviction.</param>
    public InMemoryStateStore(int maxEntries = DefaultMaxEntries)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxEntries, 0);
        _maxEntries = maxEntries;
    }

    /// <inheritdoc />
    public Task<(byte[]? Data, string? Etag)> ReadAsync(ActorId id)
    {
        ActorId.Validate(id);
        if (_entries.TryGetValue(id, out var entry))
        {
            var copy = new byte[entry.Data.Length];
            Buffer.BlockCopy(entry.Data, 0, copy, 0, entry.Data.Length);
            return Task.FromResult<(byte[]? Data, string? Etag)>((copy, entry.Etag));
        }

        return Task.FromResult<(byte[]? Data, string? Etag)>((null, null));
    }

    /// <inheritdoc />
    public Task<string> WriteAsync(ActorId id, byte[] data, string? expectedEtag)
    {
        ActorId.Validate(id);
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length > MaxStateBytes)
        {
            throw new ActorException(ActorException.InvalidKey, $"Serialized state exceeds maximum limit of {MaxStateBytes} bytes.");
        }

        lock (_writeGate)
        {
            if (_entries.TryGetValue(id, out var current))
            {
                if (!string.Equals(current.Etag, expectedEtag, StringComparison.Ordinal))
                {
                    throw new ActorException(
                        ActorException.EtagConflict,
                        $"Etag conflict for actor '{id}': expected '{expectedEtag ?? "<null>"}', actual '{current.Etag}'.");
                }
            }
            else if (expectedEtag is not null)
            {
                throw new ActorException(
                    ActorException.EtagConflict,
                    $"Etag conflict for actor '{id}': expected '{expectedEtag}', but no state exists.");
            }
            else if (_entries.Count >= _maxEntries)
            {
                EvictOldest();
            }

            var nextSeq = ++_etagSequence;
            var nextEtag = $"v{nextSeq.ToString(CultureInfo.InvariantCulture)}";
            var copy = new byte[data.Length];
            Buffer.BlockCopy(data, 0, copy, 0, data.Length);
            _entries[id] = new Entry(copy, nextEtag, nextSeq);
            return Task.FromResult(nextEtag);
        }
    }

    private void EvictOldest()
    {
        ActorId? oldestKey = null;
        var oldestOrder = long.MaxValue;
        foreach (var pair in _entries)
        {
            if (pair.Value.Sequence < oldestOrder)
            {
                oldestOrder = pair.Value.Sequence;
                oldestKey = pair.Key;
            }
        }

        if (oldestKey.HasValue)
        {
            _entries.TryRemove(oldestKey.Value, out _);
        }
    }

    private sealed record Entry(byte[] Data, string Etag, long Sequence);
}
