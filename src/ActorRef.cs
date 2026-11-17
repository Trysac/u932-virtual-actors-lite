namespace Trysac.VirtualActorsLite;

/// <summary>
/// A lightweight, location-transparent reference to a virtual actor of type <typeparamref name="TActor"/>.
/// </summary>
/// <typeparam name="TActor">The concrete <see cref="Actor"/> type.</typeparam>
public sealed class ActorRef<TActor>
    where TActor : Actor, new()
{
    private readonly IActorClusterDispatcher _dispatcher;

    internal ActorRef(ActorId id, IActorClusterDispatcher dispatcher)
    {
        Id = id;
        _dispatcher = dispatcher;
    }

    /// <summary>Gets the target actor identifier.</summary>
    public ActorId Id { get; }

    /// <summary>
    /// Sends an asynchronous turn-based invocation to the virtual actor and awaits its result.
    /// </summary>
    /// <typeparam name="TResult">The return type of the call.</typeparam>
    /// <param name="call">The asynchronous delegate to execute inside the actor's turn.</param>
    /// <returns>The result produced by the actor method.</returns>
    public Task<TResult> CallAsync<TResult>(Func<TActor, Task<TResult>> call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return _dispatcher.DispatchCallAsync(Id, call);
    }

    /// <summary>
    /// Sends an asynchronous turn-based invocation to the virtual actor with no return value.
    /// </summary>
    /// <param name="call">The asynchronous delegate to execute inside the actor's turn.</param>
    /// <returns>A task that completes when the actor turn finishes.</returns>
    public async Task CallAsync(Func<TActor, Task> call)
    {
        ArgumentNullException.ThrowIfNull(call);
        await _dispatcher.DispatchCallAsync<TActor, bool>(Id, async actor =>
        {
            await call(actor).ConfigureAwait(false);
            return true;
        }).ConfigureAwait(false);
    }
}
