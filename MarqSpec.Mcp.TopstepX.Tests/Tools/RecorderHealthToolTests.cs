using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using MarqSpec.Mcp.TopstepX.Tools;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MarqSpec.Mcp.TopstepX.Tests.Tools;

/// <summary>
/// What <c>get_recorder_health</c> reports, and what it refuses to invent (gh#756).
/// </summary>
public sealed class RecorderHealthToolTests
{
    private const string Es = "CON.F.US.EP.U26";
    private const string Nq = "CON.F.US.ENQ.U26";
    private const string PasswordSentinel = "Password=sentinel-db-password-b91c";

    private static readonly DateTimeOffset _tuesday = At(2026, 8, 18, 9, 30);
    private static readonly DateTimeOffset _open = At(2026, 8, 17, 17, 0);
    private static readonly DateTimeOffset _close = At(2026, 8, 18, 16, 0);
    private static readonly JsonSerializerOptions _wire = McpJsonUtilities.DefaultOptions;

    private static DateTimeOffset At(int year, int month, int day, int hour, int minute) =>
        MarketClock.FromMarket(new DateOnly(year, month, day), new TimeOnly(hour, minute)).ToUniversalTime();

    [Fact]
    public async Task GetRecorderHealth_WhenHubIsConnectedButNotSubscribed_ReportsFeedConnected()
    {
        await using Fixture fixture = Fixture.Open();
        fixture.Tape.Set(TapeAvailability.ConnectedButNotSubscribed());

        RecorderHealthReport.Report report = await fixture.Tools.GetRecorderHealth(CancellationToken.None);

        report.FeedConnected.Should().BeTrue();
        report.Feed.Should().Be(TapeUnavailableReason.ConnectedButNotSubscribed);
        Wire(report).GetProperty("feed").GetString().Should().Be("ConnectedButNotSubscribed");
    }

    [Fact]
    public async Task GetRecorderHealth_WhenTheProcessIsListening_ReportsFeedConnected()
    {
        await using Fixture fixture = Fixture.Open();
        fixture.Tape.Set(TapeAvailability.Listening());

        RecorderHealthReport.Report report = await fixture.Tools.GetRecorderHealth(CancellationToken.None);

        report.FeedConnected.Should().BeTrue();
        report.Feed.Should().Be(TapeUnavailableReason.None);
    }

    [Fact]
    public async Task GetRecorderHealth_WhenHubIsReconnecting_ReportsFeedDisconnected()
    {
        await using Fixture fixture = Fixture.Open();
        fixture.Tape.Set(TapeAvailability.Reconnecting());

        RecorderHealthReport.Report report = await fixture.Tools.GetRecorderHealth(CancellationToken.None);

        report.FeedConnected.Should().BeFalse();
        report.Feed.Should().Be(TapeUnavailableReason.Reconnecting);
    }

    [Fact]
    public async Task GetRecorderHealth_WhenTheRecorderNeverStarted_ReportsFeedDisconnected()
    {
        await using Fixture fixture = Fixture.Open();

        RecorderHealthReport.Report report = await fixture.Tools.GetRecorderHealth(CancellationToken.None);

        report.FeedConnected.Should().BeFalse();
        report.Feed.Should().Be(TapeUnavailableReason.NeverStarted);
    }

    [Fact]
    public async Task GetRecorderHealth_WhenTheDatabaseAnswers_ReportsAvailable()
    {
        await using Fixture fixture = Fixture.Open();

        RecorderHealthReport.Report report = await fixture.Tools.GetRecorderHealth(CancellationToken.None);

        report.Database.Should().Be(RecorderHealthReport.StoreConnectivity.Available);
        Wire(report).GetProperty("database").GetString().Should().Be("Available");
    }

    [Fact]
    public async Task GetRecorderHealth_WhenTheDatabaseProbeThrows_ReportsUnavailableAndOmitsGaps()
    {
        await using Fixture fixture = Fixture.Open();
        fixture.Store.ProbeFailure = new InvalidOperationException(PasswordSentinel);
        fixture.Store.Ticks.Add(new ContractLastTick("ES", Es, _tuesday));

        RecorderHealthReport.Report report = await fixture.Tools.GetRecorderHealth(CancellationToken.None);

        report.Database.Should().Be(RecorderHealthReport.StoreConnectivity.Unavailable);
        report.Gaps.Should().BeNull();
        report.Contracts.Should().OnlyContain(row => row.LastTickUtc == null && row.ContractId == null);
        Wire(report).TryGetProperty("gaps", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetRecorderHealth_WhenTheProbeThrows_DoesNotEchoTheConnectionTarget()
    {
        await using Fixture fixture = Fixture.Open();
        fixture.Store.ProbeFailure = new InvalidOperationException(
            "Host=sentinel-db-host-b91c; " + PasswordSentinel);

        RecorderHealthReport.Report report = await fixture.Tools.GetRecorderHealth(CancellationToken.None);
        string json = JsonSerializer.Serialize(report, _wire);

        json.Should().NotContain(PasswordSentinel);
        json.Should().NotContain("sentinel-db-host-b91c");
    }

    [Fact]
    public async Task GetRecorderHealth_WhenTheLedgerReadThrows_ReportsUnavailableAndOmitsGaps()
    {
        await using Fixture fixture = Fixture.Open();
        fixture.Store.ReadFailure = new InvalidOperationException(PasswordSentinel);
        fixture.Store.Ticks.Add(new ContractLastTick("ES", Es, _tuesday));

        RecorderHealthReport.Report report = await fixture.Tools.GetRecorderHealth(CancellationToken.None);

        report.Database.Should().Be(RecorderHealthReport.StoreConnectivity.Unavailable);
        report.Gaps.Should().BeNull();
        report.Contracts.Should().OnlyContain(row => row.LastTickUtc == null);
        JsonSerializer.Serialize(report, _wire).Should().NotContain(PasswordSentinel);
    }

    [Fact]
    public async Task GetRecorderHealth_WhenAContractHasAPrint_ReportsThatPrintTime()
    {
        await using Fixture fixture = Fixture.Open();
        DateTimeOffset printed = At(2026, 8, 18, 9, 15);
        fixture.Store.Ticks.Add(new ContractLastTick("ES", Es, printed));

        RecorderHealthReport.Report report = await fixture.Tools.GetRecorderHealth(CancellationToken.None);

        report.Contracts.Should().ContainSingle(row => row.Symbol == "ES")
            .Which.LastTickUtc.Should().Be(printed);
        Wire(report).GetProperty("contracts").EnumerateArray()
            .Single(row => row.GetProperty("symbol").GetString() == "ES")
            .GetProperty("lastTickUtc").GetDateTimeOffset().Should().Be(printed);
    }

    [Fact]
    public async Task GetRecorderHealth_WhenAContractHasNoPrint_OmitsLastTick()
    {
        await using Fixture fixture = Fixture.Open();

        RecorderHealthReport.Report report = await fixture.Tools.GetRecorderHealth(CancellationToken.None);

        RecorderHealthReport.ContractStatus es = report.Contracts.Should().ContainSingle(row => row.Symbol == "ES").Which;
        es.ContractId.Should().BeNull();
        es.LastTickUtc.Should().BeNull();
        JsonElement wire = Wire(report).GetProperty("contracts").EnumerateArray()
            .Single(row => row.GetProperty("symbol").GetString() == "ES");
        wire.TryGetProperty("contractId", out _).Should().BeFalse();
        wire.TryGetProperty("lastTickUtc", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetRecorderHealth_WhenTheListenIsStillOpen_ReportsNoGap()
    {
        await using Fixture fixture = Fixture.Listening();
        fixture.Store.Ticks.Add(new ContractLastTick("ES", Es, _tuesday));
        fixture.Store.Ticks.Add(new ContractLastTick("NQ", Nq, _tuesday));
        fixture.Store.Coverage.Add(Open(_open, "ES", Es));
        fixture.Store.Coverage.Add(Open(_open, "NQ", Nq));

        RecorderHealthReport.Report report = await fixture.Tools.GetRecorderHealth(CancellationToken.None);

        report.Gaps.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRecorderHealth_WhenAnOpenListenOutlivesTheRecorder_ReportsTheElapsedSessionAsAGap()
    {
        // A still-open row is coverage only while the symbol is listening. After a crash it is a leftover,
        // and counting it would report a dead recorder as fully covered.
        await using Fixture fixture = Fixture.Open();
        fixture.Tape.Set(TapeAvailability.ConnectedButNotSubscribed());
        fixture.Store.Coverage.Add(Open(_open, "ES", Es));
        fixture.Store.Ticks.Add(new ContractLastTick("ES", Es, _open));

        RecorderHealthReport.Report report = await fixture.Tools.GetRecorderHealth(CancellationToken.None);

        report.Gaps.Should().ContainSingle(gap => gap.Symbol == "ES" && gap.ContractId == Es)
            .Which.Should().Be(new RecorderHealthReport.Gap("ES", Es, _open, _tuesday));
    }

    [Fact]
    public async Task GetRecorderHealth_WhenListeningStoppedMidSession_ReportsTheGapAfterTheClose()
    {
        await using Fixture fixture = Fixture.Open();
        fixture.Tape.Set(TapeAvailability.Stopped());
        DateTimeOffset stopped = At(2026, 8, 18, 9, 0);
        fixture.Store.Coverage.Add(new CoverageSlice("ES", Es, _open, stopped));
        fixture.Store.Ticks.Add(new ContractLastTick("ES", Es, stopped));

        RecorderHealthReport.Report report = await fixture.Tools.GetRecorderHealth(CancellationToken.None);

        report.Gaps.Should().Contain(gap => gap.Symbol == "ES" && gap.ContractId == Es)
            .Which.Should().Be(new RecorderHealthReport.Gap("ES", Es, stopped, _tuesday));
    }

    [Fact]
    public async Task GetRecorderHealth_WhenOneContractIsCoveredAndAnotherIsNot_ReportsOnlyTheUncoveredGap()
    {
        await using Fixture fixture = Fixture.Listening();
        string other = "CON.F.US.EP.Z26";
        fixture.Store.Ticks.Add(new ContractLastTick("ES", Es, _tuesday));
        fixture.Store.Ticks.Add(new ContractLastTick("ES", other, _tuesday));
        fixture.Store.Ticks.Add(new ContractLastTick("NQ", Nq, _tuesday));
        fixture.Store.Coverage.Add(Open(_open, "ES", Es));
        fixture.Store.Coverage.Add(Open(_open, "NQ", Nq));

        RecorderHealthReport.Report report = await fixture.Tools.GetRecorderHealth(CancellationToken.None);

        report.Gaps.Should().ContainSingle()
            .Which.Should().Be(new RecorderHealthReport.Gap("ES", other, _open, _tuesday));
    }

    [Fact]
    public async Task GetRecorderHealth_WhenTheMarketIsShut_ReportsGapsInTheSessionThatClosed()
    {
        DateTimeOffset saturday = At(2026, 8, 22, 12, 0);
        DateTimeOffset fridayOpen = At(2026, 8, 20, 17, 0);
        DateTimeOffset fridayClose = At(2026, 8, 21, 16, 0);
        await using Fixture fixture = Fixture.Open(saturday);

        RecorderHealthReport.Report report = await fixture.Tools.GetRecorderHealth(CancellationToken.None);

        report.Session.Should().NotBeNull();
        report.Session!.TradeDate.Should().Be(new DateOnly(2026, 8, 21));
        report.Session.InSession.Should().BeFalse();
        report.Gaps.Should().Contain(new RecorderHealthReport.Gap("ES", null, fridayOpen, fridayClose));
        report.Gaps.Should().Contain(new RecorderHealthReport.Gap("NQ", null, fridayOpen, fridayClose));
    }

    [Fact]
    public async Task GetRecorderHealth_WhenNoSessionCanBeNamed_OmitsTheSessionAndTheGaps()
    {
        List<string> holidays = [];
        DateOnly lastHoliday = new(2026, 9, 15);
        for (DateOnly day = new(2026, 7, 1); day <= lastHoliday; day = day.AddDays(1))
        {
            holidays.Add(day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        }

        await using Fixture fixture = Fixture.Open(holidays: holidays);
        fixture.Tape.Set(TapeAvailability.Listening());

        RecorderHealthReport.Report report = await fixture.Tools.GetRecorderHealth(CancellationToken.None);

        report.Session.Should().BeNull();
        report.Gaps.Should().BeNull();
        Wire(report).TryGetProperty("session", out _).Should().BeFalse();
        Wire(report).TryGetProperty("gaps", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetRecorderHealth_WhenAnInstrumentNeverSubscribed_ReportsTheElapsedSessionAsAGap()
    {
        await using Fixture fixture = Fixture.Open();
        fixture.Tape.Set(TapeAvailability.ConnectedButNotSubscribed());

        RecorderHealthReport.Report report = await fixture.Tools.GetRecorderHealth(CancellationToken.None);

        report.Gaps.Should().Contain(new RecorderHealthReport.Gap("ES", null, _open, _tuesday));
        report.Gaps.Should().Contain(new RecorderHealthReport.Gap("NQ", null, _open, _tuesday));
        report.Gaps.Should().NotContain(gap => gap.ToUtc == _close);
    }

    [Fact]
    public async Task GetRecorderHealth_WhenTheSessionHasJustOpened_ReportsNoGap()
    {
        await using Fixture fixture = Fixture.Open(At(2026, 8, 17, 17, 0));
        fixture.Tape.Set(TapeAvailability.ConnectedButNotSubscribed());

        RecorderHealthReport.Report report = await fixture.Tools.GetRecorderHealth(CancellationToken.None);

        report.Session!.InSession.Should().BeTrue();
        report.Gaps.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRecorderHealth_WhenOneInstrumentIsHeldByAnotherRecorder_ReportsThatInstrumentNotListening()
    {
        // Closed coverage so the gap list is empty. The degraded count is then the listening bit alone:
        // a row this process is not subscribed to is not a healthy recorder.
        await using Fixture fixture = Fixture.Open();
        using MetricCollector<long> reads = Collect(fixture.Telemetry);
        fixture.Tape.Set(TapeAvailability.ConnectedButNotSubscribed());
        fixture.Tape.Set("ES", TapeAvailability.Listening());
        fixture.Tape.SetUnclaimed(
            "NQ",
            TapeAvailability.HeldByAnotherRecorder("other-host", _close));
        fixture.Store.Ticks.Add(new ContractLastTick("ES", Es, _tuesday));
        fixture.Store.Ticks.Add(new ContractLastTick("NQ", Nq, _tuesday));
        fixture.Store.Coverage.Add(Open(_open, "ES", Es));
        fixture.Store.Coverage.Add(new CoverageSlice("NQ", Nq, _open, _close));

        RecorderHealthReport.Report report = await fixture.Tools.GetRecorderHealth(CancellationToken.None);

        report.FeedConnected.Should().BeTrue();
        RecorderHealthReport.ContractStatus nq = report.Contracts.Should().ContainSingle(row => row.Symbol == "NQ").Which;
        nq.Listening.Should().BeFalse();
        nq.Tape.Should().Be(TapeUnavailableReason.HeldByAnotherRecorder);
        report.Contracts.Should().ContainSingle(row => row.Symbol == "ES").Which.Listening.Should().BeTrue();
        report.Gaps.Should().BeEmpty();
        reads.LastMeasurement!.Tags[HostTelemetry.OutcomeTag].Should().Be(RecorderHealthOutcome.Degraded);
    }

    [Fact]
    public async Task GetRecorderHealth_WhenTheStoreHoldsAPrintForAnUnconfiguredSymbol_ReportsThatContract()
    {
        await using Fixture fixture = Fixture.Open();
        DateTimeOffset printed = At(2026, 8, 18, 8, 0);
        fixture.Store.Ticks.Add(new ContractLastTick("RTY", "CON.F.US.RTY.U26", printed));

        RecorderHealthReport.Report report = await fixture.Tools.GetRecorderHealth(CancellationToken.None);

        report.Contracts.Should().Contain(row =>
            row.Symbol == "RTY" && row.ContractId == "CON.F.US.RTY.U26" && row.LastTickUtc == printed);
        report.Contracts.Select(row => row.Symbol).Should().ContainInOrder("ES", "NQ", "RTY");
    }

    [Fact]
    public async Task GetRecorderHealth_WhenTheReportIsDegraded_CountsOneDegradedRead()
    {
        await using Fixture fixture = Fixture.Open();
        using MetricCollector<long> reads = Collect(fixture.Telemetry);
        fixture.Tape.Set(TapeAvailability.Reconnecting());

        await fixture.Tools.GetRecorderHealth(CancellationToken.None);

        CollectedMeasurement<long> measurement = reads.GetMeasurementSnapshot().Should().ContainSingle().Subject;
        measurement.Value.Should().Be(1);
        measurement.Tags.Should().HaveCount(1);
        measurement.Tags[HostTelemetry.OutcomeTag].Should().Be(RecorderHealthOutcome.Degraded);
        measurement.Tags.Keys.Should().NotContain(HostTelemetry.SymbolTag);
    }

    [Fact]
    public async Task GetRecorderHealth_WhenTheFeedIsUpAndTheSessionIsCovered_CountsTheReadAsOk()
    {
        await using Fixture fixture = Fixture.Listening();
        using MetricCollector<long> reads = Collect(fixture.Telemetry);
        fixture.Store.Ticks.Add(new ContractLastTick("ES", Es, _tuesday));
        fixture.Store.Ticks.Add(new ContractLastTick("NQ", Nq, _tuesday));
        fixture.Store.Coverage.Add(Open(_open, "ES", Es));
        fixture.Store.Coverage.Add(Open(_open, "NQ", Nq));

        await fixture.Tools.GetRecorderHealth(CancellationToken.None);

        reads.GetMeasurementSnapshot().Should().ContainSingle()
            .Which.Tags[HostTelemetry.OutcomeTag].Should().Be(RecorderHealthOutcome.Ok);
    }

    [Fact]
    public async Task GetRecorderHealth_WhenTheCallerCancels_DoesNotCountARead()
    {
        await using Fixture fixture = Fixture.Open();
        using MetricCollector<long> reads = Collect(fixture.Telemetry);
        fixture.Store.ProbeFailure = new OperationCanceledException();

        Func<Task> call = () => fixture.Tools.GetRecorderHealth(CancellationToken.None);

        await call.Should().ThrowAsync<OperationCanceledException>();
        reads.GetMeasurementSnapshot().Should().BeEmpty();
    }

    [Fact]
    public void GetRecorderHealth_WhenPublished_IsReadOnlyAndNamedGetRecorderHealth()
    {
        MethodInfo method = typeof(RecorderHealthTools).GetMethod(nameof(RecorderHealthTools.GetRecorderHealth))!;
        McpServerTool tool = McpServerTool.Create(
            method,
            static _ => throw new InvalidOperationException("The tool is not invoked."),
            new McpServerToolCreateOptions());

        tool.ProtocolTool.Name.Should().Be("get_recorder_health");
        tool.ProtocolTool.Annotations!.ReadOnlyHint.Should().BeTrue();
        tool.ProtocolTool.Annotations.IdempotentHint.Should().BeTrue();
    }

    private static CoverageSlice Open(DateTimeOffset start, string symbol, string contractId) =>
        new(symbol, contractId, start, TapeCoverageRecord.StillListeningEnd);

    private static MetricCollector<long> Collect(HostTelemetry telemetry) =>
        new(telemetry, HostTelemetry.Name, HostTelemetry.RecorderHealthReadsInstrument);

    private static JsonElement Wire(object value) =>
        JsonDocument.Parse(JsonSerializer.Serialize(value, _wire)).RootElement;

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(
            RecorderHealthTools tools,
            HostTelemetry telemetry,
            TapeAvailabilityHolder tape,
            ScriptedHealthStore store)
        {
            Tools = tools;
            Telemetry = telemetry;
            Tape = tape;
            Store = store;
        }

        public RecorderHealthTools Tools { get; }

        public HostTelemetry Telemetry { get; }

        public TapeAvailabilityHolder Tape { get; }

        public ScriptedHealthStore Store { get; }

        public static Fixture Open(DateTimeOffset? now = null, IReadOnlyList<string>? holidays = null) =>
            Build(now ?? _tuesday, holidays, listening: false);

        public static Fixture Listening() => Build(_tuesday, holidays: null, listening: true);

        public ValueTask DisposeAsync()
        {
            Telemetry.Dispose();
            return ValueTask.CompletedTask;
        }

        private static Fixture Build(DateTimeOffset now, IReadOnlyList<string>? holidays, bool listening)
        {
            HostTelemetry telemetry = new();
            TapeAvailabilityHolder tape = new();
            ScriptedHealthStore store = new();
            if (listening)
            {
                tape.Set(TapeAvailability.ConnectedButNotSubscribed());
                tape.Set("ES", TapeAvailability.Listening());
                tape.Set("NQ", TapeAvailability.Listening());
            }

            IOptions<MarketDataOptions> options = Options.Create(new MarketDataOptions
            {
                Instruments = "ES,NQ",
                MaxRows = 5_000,
                SessionCloseCentral = "16:00",
            });
            RecorderHealthService service = new(
                store,
                tape,
                new InstrumentRegistry(options),
                BarSessionCalendar.Parse("16:00", holidays ?? []),
                new FakeTimeProvider(now),
                telemetry);
            return new Fixture(new RecorderHealthTools(service), telemetry, tape, store);
        }
    }

    private sealed class ScriptedHealthStore : IRecorderHealthStore
    {
        public bool Connected { get; set; } = true;

        public Exception? ProbeFailure { get; set; }

        public Exception? ReadFailure { get; set; }

        public List<ContractLastTick> Ticks { get; } = [];

        public List<CoverageSlice> Coverage { get; } = [];

        public Task<bool> DatabaseConnectedAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ProbeFailure is not null)
            {
                throw ProbeFailure;
            }

            return Task.FromResult(Connected);
        }

        public Task<IReadOnlyList<ContractLastTick>> LastTicksAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReadFailure is not null)
            {
                throw ReadFailure;
            }

            return Task.FromResult<IReadOnlyList<ContractLastTick>>(Ticks);
        }

        public Task<IReadOnlyList<CoverageSlice>> CoverageAsync(
            DateTimeOffset windowStart,
            DateTimeOffset windowEnd,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ReadFailure is not null)
            {
                throw ReadFailure;
            }

            return Task.FromResult<IReadOnlyList<CoverageSlice>>(Coverage);
        }
    }
}
