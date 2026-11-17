using Xunit;

namespace Trysac.VirtualActorsLite.Tests;

/// <summary>Tests verifying ActorId and ActorException basics.</summary>
public class TextTests
{
    [Theory]
    [InlineData("k1")]
    [InlineData("device-42")]
    [InlineData("a")]
    public void AcceptsValidKeys(string key)
    {
        ActorId.ValidateKey(key);
        var id = new ActorId("DeviceActor", key);
        Assert.Equal(key, id.Key);
    }

    [Theory]
    [InlineData("")]
    public void RejectsEmptyKey(string input)
    {
        var error = Assert.Throws<ActorException>(() => ActorId.ValidateKey(input));
        Assert.Equal(ActorException.InvalidKey, error.Code);
    }
}
