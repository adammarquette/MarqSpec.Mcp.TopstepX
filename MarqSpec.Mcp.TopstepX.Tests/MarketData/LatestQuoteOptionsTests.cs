using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// The latest-quote switches fail startup when the flush or the front refresh is out of range.
/// </summary>
public sealed class LatestQuoteOptionsTests
{
    [Fact]
    public void TheShippedFlushAndFrontRefresh_AreAccepted()
    {
        MarketDataOptions options = new() { Instruments = "ES", SessionCloseCentral = "16:00" };

        options.RecordLatestQuotes.Should().BeFalse();
        options.GetQuoteEnabled.Should().BeFalse();
        options.LatestQuoteFlush.Should().Be(MarketDataOptions.DefaultLatestQuoteFlush);
        options.LatestQuoteFrontRefresh.Should().Be(MarketDataOptions.DefaultLatestQuoteFrontRefresh);
        options.LatestQuoteSubscribeBackoff.Should().Be(MarketDataOptions.DefaultLatestQuoteSubscribeBackoff);
        options.LatestQuoteStaleAfter.Should().Be(MarketDataOptions.DefaultLatestQuoteStaleAfter);
        options.Validate(new ValidationContext(options)).Should().NotContain(result =>
            result.MemberNames.Contains(nameof(MarketDataOptions.LatestQuoteFlush))
            || result.MemberNames.Contains(nameof(MarketDataOptions.LatestQuoteFrontRefresh))
            || result.MemberNames.Contains(nameof(MarketDataOptions.LatestQuoteSubscribeBackoff))
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

    [Theory]
    [InlineData(0)]
    [InlineData(59)]
    public void AFrontRefreshUnderAMinute_IsRefusedAndNamesTheKey(int seconds)
    {
        MarketDataOptions options = new()
        {
            Instruments = "ES",
            SessionCloseCentral = "16:00",
            LatestQuoteFrontRefresh = TimeSpan.FromSeconds(seconds),
        };

        options.Validate(new ValidationContext(options)).Should().Contain(result =>
            result.ErrorMessage != null
            && result.ErrorMessage.Contains("MarketData:LatestQuoteFrontRefresh", StringComparison.Ordinal)
            && result.MemberNames.Contains(nameof(MarketDataOptions.LatestQuoteFrontRefresh)));
    }

    [Fact]
    public void AFrontRefreshOverSixHours_IsRefusedAndNamesTheKey()
    {
        MarketDataOptions options = new()
        {
            Instruments = "ES",
            SessionCloseCentral = "16:00",
            LatestQuoteFrontRefresh = TimeSpan.FromHours(6) + TimeSpan.FromSeconds(1),
        };

        options.Validate(new ValidationContext(options)).Should().Contain(result =>
            result.ErrorMessage != null
            && result.ErrorMessage.Contains("MarketData:LatestQuoteFrontRefresh", StringComparison.Ordinal)
            && result.MemberNames.Contains(nameof(MarketDataOptions.LatestQuoteFrontRefresh)));
    }

    [Theory]
    [InlineData(60)]
    [InlineData(6 * 60 * 60)]
    public void AFrontRefreshAtEitherBound_IsAccepted(int seconds)
    {
        MarketDataOptions options = new()
        {
            Instruments = "ES",
            SessionCloseCentral = "16:00",
            LatestQuoteFrontRefresh = TimeSpan.FromSeconds(seconds),
        };

        options.Validate(new ValidationContext(options)).Should().NotContain(result =>
            result.MemberNames.Contains(nameof(MarketDataOptions.LatestQuoteFrontRefresh)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5 * 60 + 1)]
    public void ASubscribeBackoffOutsideTheRange_IsRefusedAndNamesTheKey(int seconds)
    {
        MarketDataOptions options = new()
        {
            Instruments = "ES",
            SessionCloseCentral = "16:00",
            LatestQuoteSubscribeBackoff = TimeSpan.FromSeconds(seconds),
        };

        options.Validate(new ValidationContext(options)).Should().Contain(result =>
            result.ErrorMessage != null
            && result.ErrorMessage.Contains(
                "MarketData:LatestQuoteSubscribeBackoff",
                StringComparison.Ordinal)
            && result.MemberNames.Contains(nameof(MarketDataOptions.LatestQuoteSubscribeBackoff)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5 * 60)]
    public void ASubscribeBackoffAtEitherBound_IsAccepted(int seconds)
    {
        MarketDataOptions options = new()
        {
            Instruments = "ES",
            SessionCloseCentral = "16:00",
            LatestQuoteSubscribeBackoff = TimeSpan.FromSeconds(seconds),
        };

        options.Validate(new ValidationContext(options)).Should().NotContain(result =>
            result.MemberNames.Contains(nameof(MarketDataOptions.LatestQuoteSubscribeBackoff)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(30 * 60 + 1)]
    public void AStaleWindowOutsideTheRange_IsRefusedAndNamesTheKey(int seconds)
    {
        MarketDataOptions options = new()
        {
            Instruments = "ES",
            SessionCloseCentral = "16:00",
            LatestQuoteStaleAfter = TimeSpan.FromSeconds(seconds),
        };

        options.Validate(new ValidationContext(options)).Should().Contain(result =>
            result.ErrorMessage != null
            && result.ErrorMessage.Contains("MarketData:LatestQuoteStaleAfter", StringComparison.Ordinal)
            && result.MemberNames.Contains(nameof(MarketDataOptions.LatestQuoteStaleAfter)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30 * 60)]
    public void AStaleWindowAtEitherBound_IsAccepted(int seconds)
    {
        MarketDataOptions options = new()
        {
            Instruments = "ES",
            SessionCloseCentral = "16:00",
            LatestQuoteStaleAfter = TimeSpan.FromSeconds(seconds),
        };

        options.Validate(new ValidationContext(options)).Should().NotContain(result =>
            result.MemberNames.Contains(nameof(MarketDataOptions.LatestQuoteStaleAfter)));
    }
}
