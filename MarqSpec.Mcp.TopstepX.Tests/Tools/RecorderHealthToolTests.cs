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

    [Fact(Timeout = 5_000)]
    public async Task GetRecorderHealth_WhenTheCallerCancels_DoesNotCountARead()
    {
        await using Fixture fixture = Fixture.Open();
        using MetricCollector<long> reads = Collect(fixture.Telemetry);
        fixture.Service.ProbeTimeout = TimeSpan.FromSeconds(2);
        fixture.Store.HoldProbe = true;
        using CancellationTokenSource cancelled = new(TimeSpan.FromMilliseconds(50));

        Func<Task> call = () => fixture.Tools.GetRecorderHealth(cancelled.Token);

        await call.Should().ThrowAsync<OperationCanceledException>();
        reads.GetMeasurementSnapshot().Should().BeEmpty();
    }

    [Fact]
    public async Task ProbeTimeout_WhenUnset_IsTwoSeconds()
    {
        await using Fixture fixture = Fixture.Open();

        fixture.Service.ProbeTimeout.Should().Be(RecorderHealthService.DefaultProbeTimeout);
        RecorderHealthService.DefaultProbeTimeout.Should().Be(TimeSpan.FromSeconds(2));
    }

    [Fact(Timeout = 5_000)]
    public async Task GetRecorderHealth_WhenTheDatabaseCheckExceedsTheDeadline_CountsATimeout()
    {
        await using Fixture fixture = Fixture.Open();
        using MetricCollector<long> reads = Collect(fixture.Telemetry);
        fixture.Service.ProbeTimeout.Should().Be(TimeSpan.FromSeconds(2));
        fixture.Store.HoldProbe = true;

        Task<RecorderHealthReport.Report> pending = fixture.Tools.GetRecorderHealth(CancellationToken.None);
        DateTimeOffset giveUp = DateTimeOffset.UtcNow.AddSeconds(2);
        while (fixture.Store.Probes < 1 && DateTimeOffset.UtcNow < giveUp)
        {
            await Task.Delay(10);
        }

        fixture.Store.Probes.Should().Be(1);
        fixture.Clock.Advance(fixture.Service.ProbeTimeout);
        Task finished = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromMilliseconds(500)));
        finished.Should().BeSameAs(pending);

        RecorderHealthReport.Report report = await pending;
        report.Database.Should().Be(RecorderHealthReport.StoreConnectivity.Unavailable);
        report.Gaps.Should().BeNull();
        report.Contracts.Should().OnlyContain(row => row.ContractId == null);
        reads.GetMeasurementSnapshot().Should().ContainSingle()
            .Which.Tags[HostTelemetry.OutcomeTag].Should().Be(RecorderHealthOutcome.Timeout);
    }

    [Fact(Timeout = 5_000)]
    public async Task GetRecorderHealth_WhenTheCallerCancelsDuringTheBuild_DoesNotReuseACachedReport()
    {
        FakeTimeProvider clock = new(_tuesday);
        ScriptedHealthStore store = new() { HoldProbe = true };
        RecorderHealthMemo memo = new();
        await using Fixture first = Fixture.Sharing(store, memo, clock);
        await using Fixture second = Fixture.Sharing(store, memo, clock);
        using CancellationTokenSource cancelled = new();

        Task<RecorderHealthReport.Report> call = first.Tools.GetRecorderHealth(cancelled.Token);
        DateTimeOffset giveUp = DateTimeOffset.UtcNow.AddSeconds(2);
        while (store.Probes < 1 && DateTimeOffset.UtcNow < giveUp)
        {
            await Task.Delay(10);
        }

        store.Probes.Should().Be(1);
        cancelled.Cancel();
        Func<Task> wait = async () => await call.ConfigureAwait(false);
        await wait.Should().ThrowAsync<OperationCanceledException>();

        store.HoldProbe = false;
        await second.Tools.GetRecorderHealth(CancellationToken.None);

        store.Probes.Should().Be(2);
    }

    [Fact]
    public async Task GetRecorderHealth_WhenARolledOffContractHasOnlyOldPrints_OmitsItAndStaysOk()
    {
        await using Fixture fixture = Fixture.Listening();
        using MetricCollector<long> reads = Collect(fixture.Telemetry);
        const string june = "CON.F.US.EP.M26";
        fixture.Store.Ticks.Add(new ContractLastTick("ES", june, At(2026, 6, 15, 10, 0)));
        fixture.Store.Ticks.Add(new ContractLastTick("ES", Es, _tuesday));
        fixture.Store.Ticks.Add(new ContractLastTick("NQ", Nq, _tuesday));
        fixture.Store.Coverage.Add(new CoverageSlice("ES", june, At(2026, 6, 15, 9, 0), At(2026, 6, 15, 11, 0)));
        fixture.Store.Coverage.Add(Open(_open, "ES", Es));
        fixture.Store.Coverage.Add(Open(_open, "NQ", Nq));

        RecorderHealthReport.Report report = await fixture.Tools.GetRecorderHealth(CancellationToken.None);

        report.Contracts.Should().NotContain(row => row.ContractId == june);
        report.Gaps.Should().BeEmpty();
        reads.LastMeasurement!.Tags[HostTelemetry.OutcomeTag].Should().Be(RecorderHealthOutcome.Ok);
    }

    [Fact]
    public async Task GetRecorderHealth_WhenAnUnconfiguredSymbolHasOldPrints_OmitsItAndStaysOk()
    {
        await using Fixture fixture = Fixture.Listening();
        using MetricCollector<long> reads = Collect(fixture.Telemetry);
        fixture.Store.Ticks.Add(new ContractLastTick("RTY", "CON.F.US.RTY.M26", At(2026, 6, 15, 10, 0)));
        fixture.Store.Ticks.Add(new ContractLastTick("RTY", "CON.F.US.RTY.U26", _tuesday));
        fixture.Store.Ticks.Add(new ContractLastTick("ES", Es, _tuesday));
        fixture.Store.Ticks.Add(new ContractLastTick("NQ", Nq, _tuesday));
        fixture.Store.Coverage.Add(Open(_open, "ES", Es));
        fixture.Store.Coverage.Add(Open(_open, "NQ", Nq));

        RecorderHealthReport.Report report = await fixture.Tools.GetRecorderHealth(CancellationToken.None);

        report.Contracts.Should().NotContain(row => row.Symbol == "RTY");
        report.Gaps.Should().BeEmpty();
        reads.LastMeasurement!.Tags[HostTelemetry.OutcomeTag].Should().Be(RecorderHealthOutcome.Ok);
    }

    [Fact]
    public async Task GetRecorderHealth_WhenTwoContractsTradedInsideTheSession_ReportsBoth()
    {
        await using Fixture fixture = Fixture.Listening();
        const string june = "CON.F.US.EP.M26";
        fixture.Store.Ticks.Add(new ContractLastTick("ES", june, At(2026, 8, 18, 9, 0)));
        fixture.Store.Ticks.Add(new ContractLastTick("ES", Es, _tuesday));
        fixture.Store.Coverage.Add(Open(_open, "ES", june));
        fixture.Store.Coverage.Add(Open(_open, "ES", Es));
        fixture.Store.Coverage.Add(Open(_open, "NQ", Nq));

        RecorderHealthReport.Report report = await fixture.Tools.GetRecorderHealth(CancellationToken.None);

        report.Contracts.Should().Contain(row => row.Symbol == "ES" && row.ContractId == june);
        report.Contracts.Should().Contain(row => row.Symbol == "ES" && row.ContractId == Es);
    }

    [Fact]
    public async Task GetRecorderHealth_WhenTheSessionIsOpen_AsksOnlyForThatWindow()
    {
        await using Fixture fixture = Fixture.Open();

        await fixture.Tools.GetRecorderHealth(CancellationToken.None);

        fixture.Store.RequestedStart.Should().Be(_open);
        fixture.Store.RequestedEnd.Should().Be(_tuesday);
        fixture.Store.RequestedInstruments.Should().BeEquivalentTo("ES", "NQ");
    }

    [Fact]
    public async Task GetRecorderHealth_WhenCalledTwiceInsideTheMemo_DoesNotScanAgain()
    {
        FakeTimeProvider clock = new(_tuesday);
        ScriptedHealthStore store = new();
        RecorderHealthMemo memo = new();
        await using Fixture first = Fixture.Sharing(store, memo, clock, listening: true);
        await using Fixture second = Fixture.Sharing(store, memo, clock, listening: true);
        using MetricCollector<long> reads = Collect(first.Telemetry);
        store.Ticks.Add(new ContractLastTick("ES", Es, _tuesday));
        store.Ticks.Add(new ContractLastTick("NQ", Nq, _tuesday));
        store.Coverage.Add(Open(_open, "ES", Es));
        store.Coverage.Add(Open(_open, "NQ", Nq));

        await first.Tools.GetRecorderHealth(CancellationToken.None);
        await second.Tools.GetRecorderHealth(CancellationToken.None);

        store.Probes.Should().Be(1);
        store.TickReads.Should().Be(1);
        reads.GetMeasurementSnapshot().Should().ContainSingle();

        clock.Advance(RecorderHealthMemo.Ttl);
        await first.Tools.GetRecorderHealth(CancellationToken.None);

        store.Probes.Should().Be(2);
    }

    [Fact]
    public async Task GetRecorderHealth_WhenTheReportIsReused_ReportsWhenItWasCachedAndHowOldItIs()
    {
        FakeTimeProvider clock = new(_tuesday);
        ScriptedHealthStore store = new();
        RecorderHealthMemo memo = new();
        await using Fixture first = Fixture.Sharing(store, memo, clock, listening: true);
        await using Fixture second = Fixture.Sharing(store, memo, clock, listening: true);
        store.Ticks.Add(new ContractLastTick("ES", Es, _tuesday));
        store.Ticks.Add(new ContractLastTick("NQ", Nq, _tuesday));
        store.Coverage.Add(Open(_open, "ES", Es));
        store.Coverage.Add(Open(_open, "NQ", Nq));

        RecorderHealthReport.Report built = await first.Tools.GetRecorderHealth(CancellationToken.None);
        built.CachedAtUtc.Should().BeNull();
        built.AgeMs.Should().BeNull();
        Wire(built).TryGetProperty("cachedAtUtc", out _).Should().BeFalse();
        Wire(built).TryGetProperty("ageMs", out _).Should().BeFalse();

        clock.Advance(TimeSpan.FromMilliseconds(1500));
        RecorderHealthReport.Report reused = await second.Tools.GetRecorderHealth(CancellationToken.None);

        store.Probes.Should().Be(1);
        reused.CachedAtUtc.Should().Be(_tuesday);
        reused.AgeMs.Should().Be(1500);
        Wire(reused).GetProperty("ageMs").GetInt64().Should().Be(1500);
        Wire(reused).GetProperty("cachedAtUtc").GetDateTimeOffset().Should().Be(_tuesday);
    }

    [Fact(Timeout = 5_000)]
    public async Task GetRecorderHealth_WhenTwoCallsOverlap_ScansOnce()
    {
        FakeTimeProvider clock = new(_tuesday);
        ScriptedHealthStore store = new();
        RecorderHealthMemo memo = new();
        await using Fixture first = Fixture.Sharing(store, memo, clock);
        await using Fixture second = Fixture.Sharing(store, memo, clock);
        store.HoldProbe = true;

        Task<RecorderHealthReport.Report> left = first.Tools.GetRecorderHealth(CancellationToken.None);
        Task<RecorderHealthReport.Report> right = second.Tools.GetRecorderHealth(CancellationToken.None);
        DateTimeOffset giveUp = DateTimeOffset.UtcNow.AddSeconds(2);
        while (store.Probes < 1 && DateTimeOffset.UtcNow < giveUp)
        {
            await Task.Delay(10);
        }

        await Task.Delay(50);
        store.Probes.Should().Be(1);
        store.ReleaseProbe();
        await Task.WhenAll(left, right);
        store.Probes.Should().Be(1);
        store.TickReads.Should().Be(1);
    }

    [Fact]
    public async Task GetRecorderHealth_WhenTheSessionClosedEarly_DoesNotTreatTheAfternoonAsAGap()
    {
        await using Fixture fixture = Fixture.Listening(
            At(2026, 11, 27, 13, 0),
            earlyCloses: ["2026-11-27 12:00"]);
        DateTimeOffset earlyOpen = At(2026, 11, 26, 17, 0);
        DateTimeOffset earlyClose = At(2026, 11, 27, 12, 0);
        fixture.Store.Ticks.Add(new ContractLastTick("ES", Es, At(2026, 11, 27, 11, 0)));
        fixture.Store.Ticks.Add(new ContractLastTick("NQ", Nq, At(2026, 11, 27, 11, 0)));
        fixture.Store.Coverage.Add(new CoverageSlice("ES", Es, earlyOpen, earlyClose));
        fixture.Store.Coverage.Add(new CoverageSlice("NQ", Nq, earlyOpen, earlyClose));

        RecorderHealthReport.Report report = await fixture.Tools.GetRecorderHealth(CancellationToken.None);

        report.Session!.InSession.Should().BeFalse();
        report.Session.CloseUtc.Should().Be(earlyClose);
        report.Gaps.Should().BeEmpty();
        report.Gaps.Should().NotContain(gap => gap.ToUtc == At(2026, 11, 27, 16, 0));
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
            RecorderHealthService service,
            HostTelemetry telemetry,
            TapeAvailabilityHolder tape,
            ScriptedHealthStore store,
            FakeTimeProvider clock)
        {
            Tools = tools;
            Service = service;
            Telemetry = telemetry;
            Tape = tape;
            Store = store;
            Clock = clock;
        }

        public RecorderHealthTools Tools { get; }

        public RecorderHealthService Service { get; }

        public HostTelemetry Telemetry { get; }

        public TapeAvailabilityHolder Tape { get; }

        public ScriptedHealthStore Store { get; }

        public FakeTimeProvider Clock { get; }

        public static Fixture Open(
            DateTimeOffset? now = null,
            IReadOnlyList<string>? holidays = null,
            IReadOnlyList<string>? earlyCloses = null) =>
            Build(now ?? _tuesday, holidays, earlyCloses, listening: false, store: null, memo: null, clock: null);

        public static Fixture Listening(
            DateTimeOffset? now = null,
            IReadOnlyList<string>? earlyCloses = null) =>
            Build(now ?? _tuesday, holidays: null, earlyCloses, listening: true, store: null, memo: null, clock: null);

        public static Fixture Sharing(
            ScriptedHealthStore store,
            RecorderHealthMemo memo,
            FakeTimeProvider clock,
            bool listening = false) =>
            Build(clock.GetUtcNow(), holidays: null, earlyCloses: null, listening, store, memo, clock);

        public ValueTask DisposeAsync()
        {
            Telemetry.Dispose();
            return ValueTask.CompletedTask;
        }

        private static Fixture Build(
            DateTimeOffset now,
            IReadOnlyList<string>? holidays,
            IReadOnlyList<string>? earlyCloses,
            bool listening,
            ScriptedHealthStore? store,
            RecorderHealthMemo? memo,
            FakeTimeProvider? clock)
        {
            HostTelemetry telemetry = new();
            TapeAvailabilityHolder tape = new();
            store ??= new ScriptedHealthStore();
            clock ??= new FakeTimeProvider(now);
            memo ??= new RecorderHealthMemo();
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
                BarSessionCalendar.Parse("16:00", holidays ?? [], earlyCloses: earlyCloses ?? []),
                clock,
                telemetry,
                memo);
            return new Fixture(new RecorderHealthTools(service), service, telemetry, tape, store, clock);
        }
    }

    private sealed class ScriptedHealthStore : IRecorderHealthStore
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _probes;
        private int _tickReads;

        public bool Connected { get; set; } = true;

        public bool HoldProbe { get; set; }

        public Exception? ProbeFailure { get; set; }

        public Exception? ReadFailure { get; set; }

        public int Probes => Volatile.Read(ref _probes);

        public int TickReads => Volatile.Read(ref _tickReads);

        public IReadOnlyCollection<string>? RequestedInstruments { get; private set; }

        public DateTimeOffset RequestedStart { get; private set; }

        public DateTimeOffset RequestedEnd { get; private set; }

        public List<ContractLastTick> Ticks { get; } = [];

        public List<CoverageSlice> Coverage { get; } = [];

        public void ReleaseProbe() => _release.TrySetResult();

        public async Task<bool> DatabaseConnectedAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _probes);
            cancellationToken.ThrowIfCancellationRequested();
            if (HoldProbe)
            {
                await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (ProbeFailure is not null)
            {
                throw ProbeFailure;
            }

            return Connected;
        }

        public Task<IReadOnlyList<ContractLastTick>> LastTicksAsync(
            IReadOnlyCollection<string> instruments,
            DateTimeOffset windowStart,
            DateTimeOffset windowEnd,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _tickReads);
            RequestedInstruments = instruments;
            RequestedStart = windowStart;
            RequestedEnd = windowEnd;
            cancellationToken.ThrowIfCancellationRequested();
            if (ReadFailure is not null)
            {
                throw ReadFailure;
            }

            return Task.FromResult<IReadOnlyList<ContractLastTick>>(Ticks);
        }

        public Task<IReadOnlyList<CoverageSlice>> CoverageAsync(
            IReadOnlyCollection<string> instruments,
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
