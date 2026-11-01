using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.Tests.Configuration;

/// <summary>Backfill options are refused when they cannot name a real pass (gh#899).</summary>
public sealed class BarBackfillOptionsValidatorTests
{
    private static readonly DateTimeOffset _now = new(2026, 8, 18, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AnUnknownInstrument_IsRefused()
    {
        BarBackfillOptionsValidator validator = Validator();
        ValidateOptionsResult result = validator.Validate(
            null,
            new BarBackfillOptions { Instruments = "NOTREAL" });

        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void AKnownInstrument_IsAccepted()
    {
        BarBackfillOptionsValidator validator = Validator();
        ValidateOptionsResult result = validator.Validate(
            null,
            new BarBackfillOptions { Instruments = "ES" });

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void AFutureNotBefore_IsRefused()
    {
        BarBackfillOptionsValidator validator = Validator();
        ValidateOptionsResult result = validator.Validate(
            null,
            new BarBackfillOptions { NotBeforeUtc = _now.AddDays(1) });

        result.Failed.Should().BeTrue();
    }

    private static BarBackfillOptionsValidator Validator() =>
        new(
            new InstrumentRegistry(Options.Create(new MarketDataOptions { Instruments = "ES" })),
            new FakeTimeProvider(_now));
}
