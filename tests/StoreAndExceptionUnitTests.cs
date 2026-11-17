using Xunit;

namespace Trysac.VirtualActorsLite.Tests;

/// <summary>
/// Unit tests covering edge cases in stores, <see cref="ManualClock"/>, <see cref="ActorId"/>, and <see cref="ActorException"/>.
/// </summary>
public class StoreAndExceptionUnitTests
{
    [Fact]
    public void ActorExceptionConstructorsAndTruncationPreserveCodeAndCapLength()
    {
        var defaultEx = new ActorException();
        Assert.Equal("actor_error", defaultEx.Code);

        var msgEx = new ActorException("simple message");
        Assert.Equal("simple message", msgEx.Message);

        var cause = new InvalidOperationException("root");
        var wrapped = new ActorException("outer", cause);
        Assert.Same(cause, wrapped.InnerException);

        var longMessage = new string('x', 260);
        var coded = new ActorException(ActorException.InvalidKey, longMessage);
        Assert.Equal(ActorException.InvalidKey, coded.Code);
        Assert.Equal(200, coded.Message.Length);
        Assert.EndsWith("...", coded.Message, StringComparison.Ordinal);

        var emptyMsg = new ActorException(ActorException.InvalidKey, string.Empty);
        Assert.Equal("actor error", emptyMsg.Message);
    }

    [Fact]
    public void ActorIdValidateRejectsInvalidTypeAndFormatsToString()
    {
        var valid = new ActorId("Counter", "k1");
        ActorId.Validate(valid);
        Assert.Equal("Counter/k1", valid.ToString());

        var badType = new ActorId("", "k1");
        var ex = Assert.Throws<ActorException>(() => ActorId.Validate(badType));
        Assert.Equal(ActorException.InvalidKey, ex.Code);
    }

    [Fact]
    public async Task InMemoryStateStoreEvictsOldestWhenCapacityReachedAndRejectsOversizedPayload()
    {
        var store = new InMemoryStateStore(maxEntries: 2);
        var id1 = new ActorId("T", "1");
        var id2 = new ActorId("T", "2");
        var id3 = new ActorId("T", "3");

        var etag1 = await store.WriteAsync(id1, [1], null);
        Assert.Equal("v1", etag1);
        await store.WriteAsync(id2, [2], null);
        await store.WriteAsync(id3, [3], null);

        var (evictedData, evictedEtag) = await store.ReadAsync(id1);
        Assert.Null(evictedData);
        Assert.Null(evictedEtag);

        var (keptData, _) = await store.ReadAsync(id3);
        Assert.Equal(new byte[] { 3 }, keptData);

        var conflictMissing = await Assert.ThrowsAsync<ActorException>(() =>
            store.WriteAsync(id1, [9], expectedEtag: "v99"));
        Assert.Equal(ActorException.EtagConflict, conflictMissing.Code);

        var oversized = new byte[InMemoryStateStore.MaxStateBytes + 1];
        var sizeEx = await Assert.ThrowsAsync<ActorException>(() =>
            store.WriteAsync(id2, oversized, null));
        Assert.Equal(ActorException.InvalidKey, sizeEx.Code);
    }

    [Fact]
    public void InMemoryActivationDirectoryCapacityLimitThrowsSiloUnavailable()
    {
        var dir = new InMemoryActivationDirectory(maxEntries: 1);
        var id1 = new ActorId("T", "1");
        var id2 = new ActorId("T", "2");

        Assert.True(dir.TryRegister(id1, "s1", out var owner1));
        Assert.Equal("s1", owner1);
        Assert.True(dir.TryRegister(id1, "s1", out _));
        Assert.False(dir.TryRegister(id1, "s2", out var stillS1));
        Assert.Equal("s1", stillS1);

        var ex = Assert.Throws<ActorException>(() => dir.TryRegister(id2, "s1", out _));
        Assert.Equal(ActorException.SiloUnavailable, ex.Code);
    }

    [Fact]
    public async Task InMemoryReminderStoreCapacityLimitThrowsSiloUnavailable()
    {
        var store = new InMemoryReminderStore(maxEntries: 1);
        var clock = new ManualClock();
        var entry1 = new ReminderEntry(new ActorId("T", "1"), "r1", clock.UtcNow, TimeSpan.FromSeconds(5), () => new DummyActor());
        var entry2 = new ReminderEntry(new ActorId("T", "2"), "r2", clock.UtcNow, TimeSpan.FromSeconds(5), () => new DummyActor());

        await store.UpsertAsync(entry1);
        var ex = await Assert.ThrowsAsync<ActorException>(() => store.UpsertAsync(entry2));
        Assert.Equal(ActorException.SiloUnavailable, ex.Code);
    }

    [Fact]
    public async Task AllSilosKilledDispatchThrowsSiloUnavailable()
    {
        var cluster = TestCluster.Create(1, new ClusterOptions(TimeSpan.FromSeconds(60)));
        cluster.KillSilo("s1");

        var ex = await Assert.ThrowsAsync<ActorException>(() =>
            cluster.GetActor<DummyActor>("k1").CallAsync(a => a.OnActivateAsync()));
        Assert.Equal(ActorException.SiloUnavailable, ex.Code);
    }

    [Fact]
    public void AccessingActorContextBeforeActivationThrowsInvalidOperationException()
    {
        var unactivated = new DummyActor();
        Assert.Throws<InvalidOperationException>(() => _ = unactivated.GetContext());
    }

    private sealed class DummyActor : Actor
    {
        public ActorContext GetContext() => Context;
    }
}
