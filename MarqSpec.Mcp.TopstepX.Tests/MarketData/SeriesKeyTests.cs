using FluentAssertions;
using MarqSpec.Mcp.TopstepX.MarketData;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// The one key shape both series kinds share: a resolution series and a session series named the same way.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="SeriesKey.Describe"/> is the <c>SeriesUnitOfWork</c> label</b>, and three call sites built
/// that string by hand before this type existed — the indicator cache's replay, <c>rebuild-indicators</c>,
/// and the session-bar write. Replacing them is only safe if the strings are identical, so they are asserted
/// here character for character rather than described.
/// </para>
/// <para>
/// <b>Value equality is the point of the record.</b> The cache memoises "this series is complete" in a set,
/// so a key that compared by reference would memoise nothing and replay every read.
/// </para>
/// </remarks>
public sealed class SeriesKeyTests
{
    [Fact]
    public void Describe_MatchesTheUnitOfWorkLabels()
    {
        // The exact strings the three RunAsync call sites built before SeriesKey existed:
        //   instrument.Symbol + " " + resolutionMinutes + "m"   and   instrument.Symbol + " " + definition.Name
        new SeriesKey.Resolution("test", "ES", 5).Describe().Should().Be("ES 5m");
        new SeriesKey.Session("test", "ES", "rth").Describe().Should().Be("ES rth");
    }

    [Fact]
    public void Equality_IsByValue_SoAKeyCanMemoise()
    {
        Dictionary<SeriesKey, string> memo = new()
        {
            [new SeriesKey.Resolution("test", "ES", 5)] = "five",
            [new SeriesKey.Session("test", "ES", "rth")] = "regular",
        };

        memo[new SeriesKey.Resolution("test", "ES", 5)].Should().Be("five");
        memo[new SeriesKey.Session("test", "ES", "rth")].Should().Be("regular");

        memo.ContainsKey(new SeriesKey.Resolution("test", "ES", 15)).Should().BeFalse();
        memo.ContainsKey(new SeriesKey.Resolution("other", "ES", 5)).Should().BeFalse();
        memo.ContainsKey(new SeriesKey.Session("test", "ES", "globex")).Should().BeFalse();
    }

    [Fact]
    public void AResolutionKey_AndASessionKey_AreNeverEqual()
    {
        // Different shapes over the same instrument. A record's equality contract carries the runtime type,
        // so these can never collide in the memo however their fields line up.
        SeriesKey resolution = new SeriesKey.Resolution("test", "ES", 5);
        SeriesKey session = new SeriesKey.Session("test", "ES", "rth");

        resolution.Should().NotBe(session);
        session.Should().NotBe(resolution);
    }
}
