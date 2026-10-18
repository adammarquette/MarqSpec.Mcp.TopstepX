using FluentAssertions;
using MarqSpec.Client.ProjectX.Api.Models;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.Tests.Venue;

/// <summary>
/// Walking a window wider than the vendor's silent 1000-bar cap, against a scripted retrieveBars.
/// </summary>
/// <remarks>
/// The gateway already pages (R-1.4) and paces (R-1.10). Until this suite, that walk was proven only by
/// grepping the source — a fake <c>IProjectXApiClient</c> was refused because the interface carries the
/// order surface. <see cref="RetrieveBarsClient"/> answers retrieveBars without spelling those names, so the
/// walk itself can fail.
/// </remarks>
[Collection(HostTelemetryCollection.Name)]
public sealed class HistoryPagingTests : IDisposable
{
    private static readonly DateTimeOffset _start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private const string Contract = "CON.F.US.MES.Z26";
    private readonly HostTelemetry _telemetry = new();

    public void Dispose() => _telemetry.Dispose();

    [Fact]
    public async Task AWindowWiderThanThePageCap_IsWalkedInPagesOfAThousandBars()
    {
        // 1500 one-minute buckets is one full page plus a remainder. Without paging the vendor would
        // silently keep the first 1000 and the rest would look like a market that did not trade.
        (RetrieveBarsClient fake, ProjectXMarketDataGateway gateway) = Gateway(TruncatingVendor());
        TimeSpan bar = TimeSpan.FromMinutes(1);
        BarRange window = new(_start, _start.AddMinutes(1_500));

        IReadOnlyList<Bar> bars = await gateway.GetBarsAsync(Contract, window, bar, CancellationToken.None);

        fake.Requests.Should().HaveCount(2);
        fake.Requests[0].Start.Should().Be(_start.UtcDateTime);
        fake.Requests[0].End.Should().Be(_start.AddMinutes(1_000).UtcDateTime);
        fake.Requests[1].Start.Should().Be(_start.AddMinutes(1_000).UtcDateTime);
        fake.Requests[1].End.Should().Be(window.End.UtcDateTime);
        fake.Requests.Should().AllSatisfy(page =>
        {
            page.ContractId.Should().Be(Contract);
            page.Limit.Should().Be(ProjectXMarketDataGateway.MaxBarsPerRequest);
            page.Unit.Should().Be(AggregateBarUnit.Minute);
            page.UnitNumber.Should().Be(1);
            page.Live.Should().BeFalse("Simulated maps to live: false");
            page.IncludePartialBar.Should().BeFalse();
        });

        bars.Should().HaveCount(1_500);
        bars[0].OpenTime.Should().Be(_start);
        bars[^1].OpenTime.Should().Be(_start.AddMinutes(1_499));
        bars.Select(b => b.ContractId).Should().AllBe(Contract);
    }

    [Fact]
    public async Task PagesPastTheVendorsDepth_ReturnEmpty_AndTheBarsThatExistAreStillReturned()
    {
        // Simulated-tier minute bars are a rolling ~63 days (wiki, measured 2026-09-06). Asking for more
        // returns empty pages with no error — the symptom gh#695 filed. Paging still has to walk the
        // window; the caller learns the edge from the bars that came back, not from a vendor error.
        DateTimeOffset depth = _start.AddMinutes(800);
        (RetrieveBarsClient fake, ProjectXMarketDataGateway gateway) = Gateway(TruncatingVendor(depth));
        BarRange window = new(_start, _start.AddMinutes(1_500));

        IReadOnlyList<Bar> bars = await gateway.GetBarsAsync(
            Contract, window, TimeSpan.FromMinutes(1), CancellationToken.None);

        fake.Requests.Should().HaveCount(2, "empty prefix pages are still issued; they are how depth is found");
        bars.Should().HaveCount(700);
        bars[0].OpenTime.Should().Be(depth);
        bars[^1].OpenTime.Should().Be(_start.AddMinutes(1_499));
    }

    [Fact]
    public async Task AWindowTheVendorAnswersEntirelyEmpty_IsStillPaged_AndReturnsNoBars()
    {
        (RetrieveBarsClient fake, ProjectXMarketDataGateway gateway) = Gateway(static _ => []);
        BarRange window = new(_start, _start.AddMinutes(2_000));

        IReadOnlyList<Bar> bars = await gateway.GetBarsAsync(
            Contract, window, TimeSpan.FromMinutes(1), CancellationToken.None);

        fake.Requests.Should().HaveCount(2);
        bars.Should().BeEmpty();
    }

    [Fact]
    public async Task AnEmptyWindow_IssuesNoRetrieveBarsCall()
    {
        (RetrieveBarsClient fake, ProjectXMarketDataGateway gateway) = Gateway(TruncatingVendor());

        IReadOnlyList<Bar> bars = await gateway.GetBarsAsync(
            Contract, new BarRange(_start, _start), TimeSpan.FromMinutes(1), CancellationToken.None);

        fake.Requests.Should().BeEmpty();
        bars.Should().BeEmpty();
    }

    [Fact]
    public async Task EachPageTakesAPacerSlot_BeforeTheVendorCall()
    {
        // A two-slot pacer over a three-page window is the same shape as the documented 50 / 30 s cap
        // over a 51-page burst (R-1.10): with a frozen clock the last reservation sits in the future, so
        // the loop is waiting, not hung. The fake answers instantly, so latency cannot be the limiter.
        // Capacity 2 rather than 50 so the proof does not allocate fifty thousand bars; ForHistory's
        // 50-per-30s numbers stay pinned on VenueRequestPacerTests.
        FakeTimeProvider clock = new(_start);
        VenueRequestPacer pacer = new(2, VenueRequestPacer.HistoryWindow, clock);
        (RetrieveBarsClient fake, ProjectXMarketDataGateway gateway) =
            Gateway(TruncatingVendor(), clock, pacer);
        BarRange window = new(_start, _start.AddMinutes(3_000));

        Task<IReadOnlyList<Bar>> pending = gateway.GetBarsAsync(
            Contract, window, TimeSpan.FromMinutes(1), CancellationToken.None);

        await WaitUntilAsync(() => fake.Requests.Count >= 2);
        pending.IsCompleted.Should().BeFalse(
            "the 3rd page must wait on the pacer; finishing here would mean the loop is unpaced");
        fake.Requests.Should().HaveCount(2);

        clock.Advance(VenueRequestPacer.HistoryWindow + VenueRequestPacer.BoundaryMargin);
        IReadOnlyList<Bar> bars = await pending;

        fake.Requests.Should().HaveCount(3);
        bars.Should().HaveCount(3_000);
    }

    private static Func<HistoryPageRequest, IReadOnlyList<AggregateBar>> TruncatingVendor(
        DateTimeOffset? depth = null) =>
        request =>
        {
            TimeSpan size = request.Unit switch
            {
                AggregateBarUnit.Minute => TimeSpan.FromMinutes(request.UnitNumber),
                AggregateBarUnit.Hour => TimeSpan.FromHours(request.UnitNumber),
                AggregateBarUnit.Day => TimeSpan.FromDays(request.UnitNumber),
                _ => TimeSpan.FromSeconds(request.UnitNumber),
            };

            List<AggregateBar> bars = [];
            for (DateTime at = request.Start; at < request.End && bars.Count < request.Limit; at += size)
            {
                if (depth is { } edge && at < edge.UtcDateTime)
                {
                    continue;
                }

                bars.Add(new AggregateBar
                {
                    Timestamp = DateTime.SpecifyKind(at, DateTimeKind.Unspecified),
                    Open = 5000m,
                    High = 5001m,
                    Low = 4999m,
                    Close = 5000m,
                    Volume = 1,
                });
            }

            return bars;
        };

    private (RetrieveBarsClient Fake, ProjectXMarketDataGateway Gateway) Gateway(
        Func<HistoryPageRequest, IReadOnlyList<AggregateBar>> answer,
        TimeProvider? clock = null,
        VenueRequestPacer? pacer = null)
    {
        (RetrieveBarsClient fake, MarqSpec.Client.ProjectX.IProjectXApiClient client) =
            RetrieveBarsClient.Create();
        fake.Answer = answer;

        TimeProvider time = clock ?? new FakeTimeProvider(_start);
        ProjectXMarketDataGateway gateway = new(
            client,
            new InstrumentRegistry(Options.Create(new MarketDataOptions
            {
                Instruments = "ES",
                SessionCloseCentral = "16:00",
            })),
            pacer ?? VenueRequestPacer.ForHistory(time),
            Options.Create(new VenueOptions
            {
                DataTier = ProjectXDataTier.Simulated,
                ApiKey = "user",
                ApiSecret = "key",
            }),
            NullLogger<ProjectXMarketDataGateway>.Instance,
            _telemetry);

        return (fake, gateway);
    }

    /// <summary>Polls until <paramref name="condition"/> holds, or ten seconds of wall clock elapse.</summary>
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, timeout.Token);
        }
    }
}
