using System.Collections.Immutable;

namespace Trysac.VirtualActorsLite;

/// <summary>
/// Tracks the logical call chain across asynchronous continuations to detect reentrant actor calls.
/// </summary>
internal static class CallChainContext
{
    private static readonly AsyncLocal<ImmutableHashSet<ActorId>?> CurrentChain = new();

    public static bool Contains(ActorId id)
    {
        var chain = CurrentChain.Value;
        return chain is not null && chain.Contains(id);
    }

    public static ImmutableHashSet<ActorId> CaptureWith(ActorId id)
    {
        var chain = CurrentChain.Value ?? ImmutableHashSet<ActorId>.Empty;
        return chain.Add(id);
    }

    public static ImmutableHashSet<ActorId>? GetCurrent() => CurrentChain.Value;

    public static void SetCurrent(ImmutableHashSet<ActorId>? chain)
    {
        CurrentChain.Value = chain;
    }
}
