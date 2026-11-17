namespace Trysac.VirtualActorsLite;

/// <summary>
/// Base class for all virtual actors. Each active actor processes one turn at a time.
/// </summary>
public abstract class Actor
{
    private ActorContext? _context;

    /// <summary>
    /// Gets the execution context for this actor activation.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if accessed before the actor is attached to a silo context.</exception>
    protected internal ActorContext Context
    {
        get => _context ?? throw new InvalidOperationException("ActorContext is not initialized until activation.");
        internal set => _context = value;
    }

    /// <summary>
    /// Invoked once when the actor is activated on a silo, before any call or reminder is processed.
    /// </summary>
    /// <returns>A task representing the asynchronous activation lifecycle hook.</returns>
    public virtual Task OnActivateAsync() => Task.CompletedTask;

    /// <summary>
    /// Invoked once when the actor is deactivating after draining queued calls.
    /// </summary>
    /// <returns>A task representing the asynchronous deactivation lifecycle hook.</returns>
    public virtual Task OnDeactivateAsync() => Task.CompletedTask;

    /// <summary>
    /// Invoked through the actor's turn-based mailbox whenever a registered reminder fires.
    /// </summary>
    /// <param name="name">The name of the reminder that fired.</param>
    /// <returns>A task representing the asynchronous reminder handler.</returns>
    public virtual Task OnReminderAsync(string name) => Task.CompletedTask;
}
