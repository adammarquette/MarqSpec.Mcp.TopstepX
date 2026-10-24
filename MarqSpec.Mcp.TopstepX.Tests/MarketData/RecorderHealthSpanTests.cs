using System.Diagnostics;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using MarqSpec.Mcp.TopstepX.Tools;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// The recorder-health span and the outcome it writes onto the HTTP span (gh#756).
/// </summary>
[Collection(HostTelemetryCollection.Name)]
public sealed class RecorderHealthSpanTests
{
    private static readonly DateTimeOffset _tuesday =
        MarketClock.FromMarket(new DateOnly(2026, 8, 18), new TimeOnly(9, 30)).ToUniversalTime();

    [Fact]
    public async Task ReadAsync_WhenTheReportIsBuilt_TagsTheReportSpanAndTheServerSpan()
    {
        List<Activity> stopped = [];
        using HostTelemetry telemetry = new();
        using ActivityListener listener = Listen(stopped, telemetry.Activities);
        using ActivitySource http = new("test.recorder.health.http");
        using ActivityListener httpListener = Listen(stopped, http);
        RecorderHealthService service = Service(telemetry);

        string outcome;
        using (Activity? server = http.StartActivity("GET /health/recorder", ActivityKind.Server))
        {
            await service.ReadAsync(CancellationToken.None);
            outcome = server!.GetTagItem(HostTelemetry.OutcomeTag) as string
                ?? throw new InvalidOperationException("The server span has no outcome.");
        }

        Activity report = stopped.Should().ContainSingle(activity => activity.OperationName == "recorder.health").Which;
        report.GetTagItem(HostTelemetry.OutcomeTag).Should().Be(outcome);
        outcome.Should().Be(RecorderHealthOutcome.Degraded);
    }

    [Fact]
    public async Task ReadAsync_WhenTheReportIsCached_StillTagsTheServerSpan()
    {
        List<Activity> stopped = [];
        using HostTelemetry telemetry = new();
        using ActivityListener listener = Listen(stopped, telemetry.Activities);
        using ActivitySource http = new("test.recorder.health.http");
        using ActivityListener httpListener = Listen(stopped, http);
        RecorderHealthService service = Service(telemetry);

        string? first;
        using (Activity? server = http.StartActivity("GET /health/recorder", ActivityKind.Server))
        {
            await service.ReadAsync(CancellationToken.None);
            first = server!.GetTagItem(HostTelemetry.OutcomeTag) as string;
        }

        string? second;
        using (Activity? server = http.StartActivity("GET /health/recorder", ActivityKind.Server))
        {
            await service.ReadAsync(CancellationToken.None);
            second = server!.GetTagItem(HostTelemetry.OutcomeTag) as string;
        }

        second.Should().Be(first);
        stopped.Where(activity => activity.OperationName == "recorder.health").Should().HaveCount(2);
    }

    private static RecorderHealthService Service(HostTelemetry telemetry)
    {
        IOptions<MarketDataOptions> options = Options.Create(new MarketDataOptions
        {
            Instruments = "ES,NQ",
            MaxRows = 5_000,
            SessionCloseCentral = "16:00",
        });
        return new RecorderHealthService(
            new QuietStore(),
            new TapeAvailabilityHolder(),
            new InstrumentRegistry(options),
            BarSessionCalendar.Parse("16:00", []),
            new FakeTimeProvider(_tuesday),
            telemetry,
            new RecorderHealthMemo());
    }

    private static ActivityListener Listen(List<Activity> into, ActivitySource source)
    {
        ActivityListener listener = new()
        {
            ShouldListenTo = candidate => ReferenceEquals(candidate, source),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = into.Add,
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private sealed class QuietStore : IRecorderHealthStore
    {
        public Task<bool> DatabaseConnectedAsync(CancellationToken cancellationToken) =>
            Task.FromResult(true);

        public Task<IReadOnlyList<ContractLastTick>> LastTicksAsync(
            IReadOnlyCollection<string> instruments,
            DateTimeOffset windowStart,
            DateTimeOffset windowEnd,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ContractLastTick>>([]);

        public Task<IReadOnlyList<CoverageSlice>> CoverageAsync(
            IReadOnlyCollection<string> instruments,
            DateTimeOffset windowStart,
            DateTimeOffset windowEnd,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<CoverageSlice>>([]);
    }
}
