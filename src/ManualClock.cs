namespace Trysac.VirtualActorsLite;

/// <summary>
/// A deterministic, manually controlled clock used by the virtual actor runtime for idle timeouts and reminders.
/// </summary>
public sealed class ManualClock
{
    private readonly object _gate = new();
    private DateTimeOffset _utcNow;

    /// <summary>
    /// Initializes a new instance of the <see cref="ManualClock"/> class.
    /// </summary>
    /// <param name="initialUtc">Optional starting UTC timestamp; defaults to 2025-01-01T00:00:00Z.</param>
    public ManualClock(DateTimeOffset? initialUtc = null)
    {
        _utcNow = initialUtc?.ToUniversalTime() ?? new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    /// <summary>Gets the current UTC time of the manual clock.</summary>
    public DateTimeOffset UtcNow
    {
        get
        {
            lock (_gate)
            {
                return _utcNow;
            }
        }
    }

    /// <summary>
    /// Advances the clock forward by <paramref name="by"/>.
    /// </summary>
    /// <param name="by">The non-negative duration to advance.</param>
    /// <returns>The updated UTC timestamp.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="by"/> is negative.</exception>
    public DateTimeOffset Advance(TimeSpan by)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(by, TimeSpan.Zero);
        lock (_gate)
        {
            _utcNow = _utcNow.Add(by);
            return _utcNow;
        }
    }
}
