namespace Trysac.VirtualActorsLite;

/// <summary>
/// Internal contract used by <see cref="ActorRef{TActor}"/> and <see cref="ActorContext"/> to dispatch calls across the cluster.
/// </summary>
internal interface IActorClusterDispatcher
{
    ActorRef<TActor> GetActor<TActor>(string key)
        where TActor : Actor, new();

    Task<TResult> DispatchCallAsync<TActor, TResult>(ActorId id, Func<TActor, Task<TResult>> call)
        where TActor : Actor, new();
}
