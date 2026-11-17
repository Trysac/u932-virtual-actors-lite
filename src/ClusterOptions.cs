namespace Trysac.VirtualActorsLite;

/// <summary>
/// Configuration options for a <see cref="TestCluster"/>.
/// </summary>
/// <param name="IdleTimeout">The duration of inactivity after which an activation is deactivated.</param>
/// <param name="MailboxCapacity">The maximum number of queued messages in an actor's mailbox before throwing <c>mailbox_full</c>.</param>
/// <param name="Clock">Optional <see cref="ManualClock"/> instance; if <see langword="null"/>, a new clock is created.</param>
public sealed record ClusterOptions(
    TimeSpan IdleTimeout,
    int MailboxCapacity = 1_000,
    ManualClock? Clock = null)
{
    /// <summary>
    /// Gets or inits an optional custom router function <c>(ActorId, IReadOnlyList&lt;string&gt; liveSilos) =&gt; siloId</c>.
    /// When <see langword="null"/>, calls are routed round-robin across live silos.
    /// </summary>
    public Func<ActorId, IReadOnlyList<string>, string>? Router { get; init; }

    /// <summary>
    /// Gets or inits an optional custom <see cref="IActivationDirectory"/> implementation.
    /// </summary>
    public IActivationDirectory? Directory { get; init; }

    /// <summary>
    /// Gets or inits an optional custom <see cref="IStateStore"/> implementation.
    /// </summary>
    public IStateStore? StateStore { get; init; }

    /// <summary>
    /// Gets or inits an optional custom <see cref="IReminderStore"/> implementation.
    /// </summary>
    public IReminderStore? ReminderStore { get; init; }
}
