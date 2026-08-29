using FluentAssertions;
using MarqSpec.Mcp.TopstepX.MarketData;
using ModelContextProtocol;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// Closed tape-health vocabulary: every non-listening reason names a fix, and
/// <see cref="TapeAvailability.Require"/> refuses rather than looking quiet (gh#218).
/// </summary>
public sealed class TapeAvailabilityTests
{
    [Fact]
    public void None_IsZero_LikeEmbeddingAvailability()
    {
        ((int)TapeUnavailableReason.None).Should().Be(0);
    }

    [Fact]
    public void Listening_HasNoExplanation_AndRequireDoesNotThrow()
    {
        TapeAvailability listening = TapeAvailability.Listening();

        listening.IsListening.Should().BeTrue();
        listening.Reason.Should().Be(TapeUnavailableReason.None);
        listening.Explanation.Should().BeNull();
        listening.Invoking(static a => a.Require()).Should().NotThrow();
    }

    [Theory]
    [MemberData(nameof(UnhealthyStates))]
    public void EveryUnhealthyReason_CarriesAnExplanation_NamingAFix(TapeAvailability availability)
    {
        availability.IsListening.Should().BeFalse();
        availability.Explanation.Should().NotBeNullOrWhiteSpace();
        availability.Explanation.Should().MatchRegex(
            "(?i)set |restart|run the http|wait for|marketdata__recordtape|projectx");

        availability.Invoking(static a => a.Require())
            .Should().Throw<McpException>()
            .WithMessage(availability.Explanation);
    }

    [Fact]
    public void TheHolderDefaultsToNeverStarted_NotListening()
    {
        // Conservative, like EmbeddingAvailabilityHolder's no-key default. A tool
        // resolved before the recorder writes must refuse, not look healthy.
        TapeAvailabilityHolder holder = new();

        holder.Value.IsListening.Should().BeFalse();
        holder.Value.Reason.Should().Be(TapeUnavailableReason.NeverStarted);
        holder.Value.Explanation.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void TheHolder_IsMutable_AndRequireReadsTheCurrentValue()
    {
        TapeAvailabilityHolder holder = new();
        holder.Set(TapeAvailability.Listening());
        holder.Value.Invoking(static a => a.Require()).Should().NotThrow();

        holder.Set(TapeAvailability.Reconnecting());
        holder.Value.Invoking(static a => a.Require())
            .Should().Throw<McpException>()
            .WithMessage("*reconnect*");
    }

    [Fact]
    public void ListeningOnOneInstrument_DoesNotMakeAnotherInstrumentListening()
    {
        TapeAvailabilityHolder holder = new();
        holder.Set(TapeAvailability.ConnectedButNotSubscribed());
        holder.Set("ES", TapeAvailability.Listening());

        holder.For("ES").IsListening.Should().BeTrue();
        holder.For("NQ").IsListening.Should().BeFalse();
        holder.For("NQ").Reason.Should().Be(TapeUnavailableReason.ConnectedButNotSubscribed);
    }

    public static TheoryData<TapeAvailability> UnhealthyStates() =>
    [
        TapeAvailability.NeverStartedBecauseStdio(),
        TapeAvailability.NeverStartedBecauseSwitchOff(),
        TapeAvailability.NeverStartedBecauseNoVenueClient(),
        TapeAvailability.Reconnecting(),
        TapeAvailability.ConnectedButNotSubscribed(),
        TapeAvailability.Stopped(),
    ];
}
