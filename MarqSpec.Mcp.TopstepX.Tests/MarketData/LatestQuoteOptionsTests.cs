using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// The latest-quote switches fail startup when the flush or the stale window is out of range.
/// </summary>
public sealed class LatestQuoteOptionsTests
{
    [Fact]
    public void TheShippedFlushAndStaleWindow_AreAccepted()
    {
        MarketDataOptions options = new() { Instruments = "ES", SessionCloseCentral = "16:00" };

        options.RecordLatestQuotes.Should().BeFalse();
        options.LatestQuoteFlush.Should().Be(MarketDataOptions.DefaultLatestQuoteFlush);
        options.Validate(new ValidationContext(options)).Should().NotContain(result =>
            result.MemberNames.Contains(nameof(MarketDataOptions.LatestQuoteFlush))
            || result.MemberNames.Contains(nameof(MarketDataOptions.LatestQuoteStaleAfter)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(199)]
    [InlineData(30_001)]
    public void AFlushOutsideTheRange_IsRefusedAndNamesTheKey(int milliseconds)
    {
        MarketDataOptions options = new()
        {
            Instruments = "ES",
            SessionCloseCentral = "16:00",
            LatestQuoteFlush = TimeSpan.FromMilliseconds(milliseconds),
        };

        options.Validate(new ValidationContext(options)).Should().Contain(result =>
            result.ErrorMessage != null
            && result.ErrorMessage.Contains("MarketData:LatestQuoteFlush", StringComparison.Ordinal)
            && result.MemberNames.Contains(nameof(MarketDataOptions.LatestQuoteFlush)));
    }

    [Theory]
    [InlineData(200)]
    [InlineData(30_000)]
    public void AFlushAtEitherBound_IsAccepted(int milliseconds)
    {
        MarketDataOptions options = new()
        {
            Instruments = "ES",
            SessionCloseCentral = "16:00",
            LatestQuoteFlush = TimeSpan.FromMilliseconds(milliseconds),
        };

        options.Validate(new ValidationContext(options)).Should().NotContain(result =>
            result.MemberNames.Contains(nameof(MarketDataOptions.LatestQuoteFlush)));
    }

    [Fact]
    public void ANegativeStaleWindow_IsRefusedAndNamesTheKey()
    {
        MarketDataOptions options = new()
        {
            Instruments = "ES",
            SessionCloseCentral = "16:00",
            LatestQuoteStaleAfter = TimeSpan.FromSeconds(-1),
        };

        options.Validate(new ValidationContext(options)).Should().Contain(result =>
            result.ErrorMessage != null
            && result.ErrorMessage.Contains("MarketData:LatestQuoteStaleAfter", StringComparison.Ordinal)
            && result.MemberNames.Contains(nameof(MarketDataOptions.LatestQuoteStaleAfter)));
    }
}
