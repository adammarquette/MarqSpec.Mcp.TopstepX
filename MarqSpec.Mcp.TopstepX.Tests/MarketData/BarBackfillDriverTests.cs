using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// One backfill pass: off by default, stop on 429, empty is coverage, a second pass refetches nothing.
/// </summary>
public sealed class BarBackfillDriverTests
{
    private static readonly DateTimeOffset _now = new(2026, 8, 18, 15, 0, 0, TimeSpan.Zero);
    private static readonly InstrumentId _es = new("ES");

    [Fact]
    public async Task Disabled_DoesNotRead()
    {
        ScriptedCache cache = new();
        BarBackfillDriver driver = Driver(cache, enabled: false);

        BarBackfillReport report = await driver.RunAsync(CancellationToken.None);

        report.Ran.Should().BeFalse();
        report.StopReason.Should().Be(BarBackfillStop.Disabled);
        cache.Calls.Should().Be(0);
        BarBackfillLane.IsActive.Should().BeFalse();
    }

    [Fact]
    public void Options_DefaultToOff()
    {
        BarBackfillOptions options = new();

        options.Enabled.Should().BeFalse();
        options.Resolutions.Should().Equal(1, 60);
    }

    [Fact]
    public async Task ARun_HoldsTheBackfillLane_WhileItReads()
    {
        ScriptedCache cache = new();
        BarBackfillDriver driver = Driver(cache, enabled: true);

        BarBackfillReport report = await driver.RunAsync(CancellationToken.None);

        report.StopReason.Should().Be(BarBackfillStop.Completed);
        cache.SawLane.Should().BeTrue();
        BarBackfillLane.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Http429_StopsTheRun()
    {
        ScriptedCache cache = new() { FailAtCall = 2 };
        BarBackfillDriver driver = Driver(cache, enabled: true, minutes: 2_500);

        BarBackfillReport report = await driver.RunAsync(CancellationToken.None);

        report.StopReason.Should().Be(BarBackfillStop.RateLimited);
        report.Pages.Should().Be(1);
        cache.Calls.Should().Be(2);
    }

    [Fact]
    public async Task AnEmptyPage_IsNotAFailure()
    {
        ScriptedCache cache = new() { Bars = 0 };
        BarBackfillDriver driver = Driver(cache, enabled: true, minutes: 30);

        BarBackfillReport report = await driver.RunAsync(CancellationToken.None);

        report.StopReason.Should().Be(BarBackfillStop.Completed);
        report.EmptyPages.Should().Be(1);
        report.VenueRequests.Should().Be(1);
    }

    [Fact]
    public async Task ASecondRun_RecomputesAndDoesNotFetchAgain()
    {
        ScriptedCache cache = new();
        BarBackfillDriver driver = Driver(cache, enabled: true, minutes: 30);

        BarBackfillReport first = await driver.RunAsync(CancellationToken.None);
        int calls = cache.Calls;
        BarBackfillReport second = await driver.RunAsync(CancellationToken.None);

        first.VenueRequests.Should().BeGreaterThan(0);
        second.Pages.Should().Be(first.Pages);
        second.VenueRequests.Should().Be(0);
        cache.Calls.Should().Be(calls * 2);
    }

    private static BarBackfillDriver Driver(ScriptedCache cache, bool enabled, int minutes = 30)
    {
        BarBackfillOptions options = new()
        {
            Enabled = enabled,
            Instruments = "ES",
            ResolutionsMinutes = "1",
            NotBeforeUtc = _now - TimeSpan.FromMinutes(minutes),
        };
        return new BarBackfillDriver(
            cache,
            Options.Create(options),
            new InstrumentRegistry(Options.Create(new MarketDataOptions { Instruments = "ES" })),
            new FakeTimeProvider(_now),
            NullLogger<BarBackfillDriver>.Instance);
    }

    private sealed class ScriptedCache : IBarBackfillCache
    {
        private readonly HashSet<DateTimeOffset> _covered = [];

        public int Calls { get; private set; }

        public bool SawLane { get; private set; }

        public int FailAtCall { get; init; }

        public int Bars { get; init; } = 1;

        public Task<BarBackfillPageResult> ReadAsync(
            InstrumentId instrument,
            int resolutionMinutes,
            BarRange window,
            CancellationToken cancellationToken)
        {
            Calls++;
            SawLane = BarBackfillLane.IsActive;
            instrument.Should().Be(_es);
            if (FailAtCall == Calls)
            {
                throw new VenueException("The gateway refused while retrieving bars.", 429);
            }

            bool covered = _covered.Contains(window.Start);
            _covered.Add(window.Start);
            return Task.FromResult(new BarBackfillPageResult(Bars, covered ? 0 : 1));
        }
    }
}
