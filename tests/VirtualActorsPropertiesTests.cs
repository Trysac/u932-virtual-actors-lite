using FsCheck;
using FsCheck.Xunit;
using Xunit;

namespace Trysac.VirtualActorsLite.Tests;

/// <summary>
/// Property-based tests verifying invariants of <see cref="TestCluster"/>, <see cref="ActorId"/>, and <see cref="InMemoryActivationDirectory"/>.
/// </summary>
public class VirtualActorsPropertiesTests
{
    [Property(MaxTest = 30)]
    public bool ValidKeyLengthsAreAcceptedAndOutOfBoundsRejected(NonNull<string> raw)
    {
        var key = raw.Get;
        if (key.Length is >= 1 and <= 256)
        {
            ActorId.ValidateKey(key);
            return true;
        }

        var ex = Assert.Throws<ActorException>(() => ActorId.ValidateKey(key));
        return ex.Code == ActorException.InvalidKey;
    }

    [Property(MaxTest = 25)]
    public bool DirectoryAlwaysElectsExactlyOneOwner(PositiveInt siloCountSeed)
    {
        var siloCount = (siloCountSeed.Get % 7) + 2;
        var dir = new InMemoryActivationDirectory();
        var id = new ActorId("PropActor", "k1");
        var silos = Enumerable.Range(1, siloCount).Select(i => $"s{i}").ToArray();

        var winners = new List<string>();
        var observedOwners = new HashSet<string>(StringComparer.Ordinal);
        foreach (var silo in silos)
        {
            if (dir.TryRegister(id, silo, out var owner))
            {
                winners.Add(silo);
            }

            observedOwners.Add(owner);
        }

        return winners.Count == 1 && observedOwners.Count == 1 && winners[0] == observedOwners.Single();
    }
}
