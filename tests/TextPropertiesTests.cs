using FsCheck;
using FsCheck.Xunit;

namespace Trysac.VirtualActorsLite.Tests;

/// <summary>Property tests for ManualClock monotonicity.</summary>
public class TextPropertiesTests
{
    [Property]
    public bool ManualClockAdvancesMonotonically(PositiveInt seconds)
    {
        var clock = new ManualClock();
        var before = clock.UtcNow;
        var delta = TimeSpan.FromSeconds(seconds.Get % 10_000);
        var after = clock.Advance(delta);
        return after == before + delta && clock.UtcNow == after;
    }
}
