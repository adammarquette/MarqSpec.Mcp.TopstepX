using FluentAssertions;
using MarqSpec.Client.ProjectX.Api.Models;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Tools;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// The first first-party background service: prints land, once, attributed and UTC, and it
/// cannot start under stdio.
/// </summary>
public sealed class TradeTapeRecorderTests
{
    private static readonly DateTimeOffset _receipt =
        new(2026, 8, 28, 14, 30, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(McpTransport.Stdio, true)]
    [InlineData(McpTransport.Http, false)]
    [InlineData(McpTransport.Stdio, false)]
    public async Task TheRecorderDoesNotStart_WhenTheTransportIsStdioOrTheSwitchIsOff(
        McpTransport transport,
        bool recordTape)
    {
        (TradeTapeRecorder recorder, FakeMarketHub hub, TopstepXDbContext database, ServiceProvider services, _) =
            Build(transport, recordTape);

        await using (services)
        await using (database)
        {
            await recorder.StartAsync(CancellationToken.None);

            hub.MarketConnects.Should().Be(0);
            hub.TradeSubscriptions.Should().BeEmpty();
            database.Trades.Should().BeEmpty();

            await recorder.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task TheRecorderWritesAnAttributedUtcPrint_WhenHttpAndRecordTapeAreOn()
    {
        (TradeTapeRecorder recorder, FakeMarketHub hub, TopstepXDbContext database, ServiceProvider services, _) =
            Build(McpTransport.Http, recordTape: true);

        await using (services)
        await using (database)
        {
            await recorder.StartAsync(CancellationToken.None);
            await WaitUntil(() => hub.TradeSubscriptions.Count > 0);

            hub.Raise(Print(
                new DateTime(2026, 8, 28, 13, 45, 0, DateTimeKind.Unspecified),
                TradeLogType.Buy,
                price: 5000.25m));

            await WaitUntil(() => recorder.RecordedPrints == 1);

            TradeRecord row = database.Trades.Should().ContainSingle().Subject;
            row.Venue.Should().Be("test");
            row.Instrument.Should().Be("ES");
            row.ContractId.Should().Be("CON.F.US.TEST.Z26");
            row.TradeTimeUtc.Should().Be(new DateTimeOffset(2026, 8, 28, 13, 45, 0, TimeSpan.Zero));
            row.TradeTimeUtc.Offset.Should().Be(TimeSpan.Zero);
            row.RecordedAt.Should().Be(_receipt);
            row.Price.Should().Be(5000.25m);
            row.Size.Should().Be(3);
            row.Direction.Should().Be(TradeDirection.Buy);
            row.Sequence.Should().Be(1);

            hub.UserConnects.Should().Be(0, "the user hub is still out of scope (ADR-0016)");
            hub.PriceSubscriptions.Should().Be(0);
            hub.OrderBookSubscriptions.Should().Be(0);

            await recorder.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(DateTimeKind.Utc)]
    [InlineData(DateTimeKind.Local)]
    [InlineData(DateTimeKind.Unspecified)]
    public async Task APrintTimestamp_IsStoredUtc_ForEveryKindTheVenueSends(DateTimeKind kind)
    {
        // ProjectXMapping.ToUtc already pins each Kind. This proves the recorder uses that path
        // rather than new DateTimeOffset(timestamp) which would treat Local as the machine offset
        // and Unspecified as local-by-inference.
        DateTime utcInstant = new(2026, 8, 28, 18, 0, 0, DateTimeKind.Utc);
        DateTime stamped = kind switch
        {
            DateTimeKind.Utc => utcInstant,
            DateTimeKind.Local => DateTime.SpecifyKind(utcInstant.ToLocalTime(), DateTimeKind.Local),
            _ => DateTime.SpecifyKind(utcInstant, DateTimeKind.Unspecified),
        };

        (TradeTapeRecorder recorder, FakeMarketHub hub, TopstepXDbContext database, ServiceProvider services, _) =
            Build(McpTransport.Http, recordTape: true);

        await using (services)
        await using (database)
        {
            await recorder.StartAsync(CancellationToken.None);
            await WaitUntil(() => hub.TradeSubscriptions.Count > 0);

            hub.Raise(Print(stamped, TradeLogType.Sell, price: 4999m));
            await WaitUntil(() => recorder.RecordedPrints == 1);

            TradeRecord row = database.Trades.Should().ContainSingle().Subject;
            row.TradeTimeUtc.Offset.Should().Be(TimeSpan.Zero);
            row.TradeTimeUtc.UtcDateTime.Should().Be(utcInstant);

            await recorder.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ANullTradeType_IsStoredAsUnknown_NotBuy()
    {
        (TradeTapeRecorder recorder, FakeMarketHub hub, TopstepXDbContext database, ServiceProvider services, _) =
            Build(McpTransport.Http, recordTape: true);

        await using (services)
        await using (database)
        {
            await recorder.StartAsync(CancellationToken.None);
            await WaitUntil(() => hub.TradeSubscriptions.Count > 0);

            hub.Raise(Print(
                new DateTime(2026, 8, 28, 13, 45, 0, DateTimeKind.Utc),
                type: null,
                price: 5001m));

            await WaitUntil(() => recorder.RecordedPrints == 1);

            database.Trades.Single().Direction.Should().Be(TradeDirection.Unknown);

            await recorder.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AnUnrecognisedTradeType_IsStoredAsUnknown_NotBuy()
    {
        (TradeTapeRecorder recorder, FakeMarketHub hub, TopstepXDbContext database, ServiceProvider services, _) =
            Build(McpTransport.Http, recordTape: true);

        await using (services)
        await using (database)
        {
            await recorder.StartAsync(CancellationToken.None);
            await WaitUntil(() => hub.TradeSubscriptions.Count > 0);

            hub.Raise(Print(
                new DateTime(2026, 8, 28, 13, 45, 0, DateTimeKind.Utc),
                (TradeLogType)99,
                price: 5002m));

            await WaitUntil(() => recorder.RecordedPrints == 1);

            database.Trades.Single().Direction.Should().Be(TradeDirection.Unknown);

            await recorder.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AFullChannel_RecordsTheDrop_RatherThanDiscardingSilently()
    {
        TaskCompletionSource hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource persistStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        (TradeTapeRecorder recorder, FakeMarketHub hub, TopstepXDbContext database, ServiceProvider services, _) =
            Build(
                McpTransport.Http,
                recordTape: true,
                channelCapacity: 1,
                persistHold: hold,
                persistStarted: persistStarted);

        await using (services)
        await using (database)
        {
            await recorder.StartAsync(CancellationToken.None);
            await WaitUntil(() => hub.TradeSubscriptions.Count > 0);

            hub.Raise(Print(Utc(13, 45, 0), TradeLogType.Buy, price: 1m));
            await persistStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            // First print is being persisted (held). Second fills the one-slot channel. Third drops.
            hub.Raise(Print(Utc(13, 45, 1), TradeLogType.Buy, price: 2m));
            hub.Raise(Print(Utc(13, 45, 2), TradeLogType.Buy, price: 3m));

            await WaitUntil(() => recorder.DroppedPrints == 1);

            recorder.DroppedPrints.Should().Be(1);
            recorder.RecordedPrints.Should().Be(0);

            hold.SetResult();
            await WaitUntil(() => recorder.RecordedPrints == 2);

            database.Trades.Select(t => t.Price).Should().BeEquivalentTo([1m, 2m]);

            await recorder.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task APrintOnTheFirstContract_IsStillRecorded_WhenALaterSubscribeThrows()
    {
        // Shipped default is ES,NQ. The connect-throws test never reaches subscribe, and the
        // rest of this suite configures ES alone, so a throw on instrument 1 used to leave the
        // ES subscription live with no drain: prints TryWrite into an unread channel, then
        // log as full-channel drops, and Trades stays empty while ExecuteTask looks clean.
        (TradeTapeRecorder recorder, FakeMarketHub hub, TopstepXDbContext database, ServiceProvider services, _) =
            Build(
                McpTransport.Http,
                recordTape: true,
                instruments: "ES,NQ",
                gateway: new PerInstrumentGateway());

        hub.SubscribeThrowsAfterFirst =
            new InvalidOperationException("the venue refused the NQ trade subscribe");

        await using (services)
        await using (database)
        {
            await recorder.StartAsync(CancellationToken.None);
            await WaitUntil(() => hub.SubscribeAttempts >= 2);

            recorder.ExecuteTask.Should().NotBeNull();
            recorder.ExecuteTask!.IsFaulted.Should().BeFalse(
                "a faulted ExecuteTask is what Program.AnyFaulted reads");

            hub.Raise(Print(
                new DateTime(2026, 8, 28, 13, 45, 0, DateTimeKind.Utc),
                TradeLogType.Buy,
                price: 5000.25m,
                contractId: "CON.F.US.EP.Z26"));

            await WaitUntil(() => recorder.RecordedPrints == 1);

            TradeRecord row = database.Trades.Should().ContainSingle().Subject;
            row.Instrument.Should().Be("ES");
            row.ContractId.Should().Be("CON.F.US.EP.Z26");
            hub.TradeSubscriptions.Should().Equal("CON.F.US.EP.Z26");

            await recorder.StopAsync(CancellationToken.None);

            recorder.ExecuteTask.IsFaulted.Should().BeFalse();
        }
    }

    [Fact]
    public async Task GetFootprintAndGetVolumeProfile_ForNq_Refuse_WhenOnlyEsIsSubscribed()
    {
        // Shipped default is ES,NQ. ES subscribe succeeds; NQ is refused. Process-wide
        // Listening would let get_footprint("NQ") return stored cells while NQ's tape is silent.
        (TradeTapeRecorder recorder, FakeMarketHub hub, TopstepXDbContext database, ServiceProvider services, _) =
            Build(
                McpTransport.Http,
                recordTape: true,
                instruments: "ES,NQ",
                gateway: new PerInstrumentGateway());

        hub.SubscribeThrowsAfterFirst =
            new InvalidOperationException("the venue refused the NQ trade subscribe");

        await using (services)
        await using (database)
        {
            TapeAvailabilityHolder tape = services.GetRequiredService<TapeAvailabilityHolder>();
            await SeedNqCellsAsync(database);

            await recorder.StartAsync(CancellationToken.None);
            await WaitUntil(() => hub.SubscribeAttempts >= 2 && hub.TradeSubscriptions.Contains("CON.F.US.EP.Z26"));

            hub.TradeSubscriptions.Should().Equal("CON.F.US.EP.Z26");
            recorder.ExecuteTask.Should().NotBeNull();
            recorder.ExecuteTask!.IsFaulted.Should().BeFalse(
                "a faulted ExecuteTask is what Program.AnyFaulted reads");

            MarketDataTools tools = Tools(database, tape);
            DateTimeOffset from = new(2026, 8, 18, 14, 0, 0, TimeSpan.Zero);
            DateTimeOffset to = new(2026, 8, 18, 16, 0, 0, TimeSpan.Zero);

            Func<Task> footprint = () => tools.GetFootprint("NQ", 5, from, to, CancellationToken.None);
            Func<Task> profile = () => tools.GetVolumeProfile("NQ", 5, from, to, CancellationToken.None);

            (await footprint.Should().ThrowAsync<ModelContextProtocol.McpException>())
                .WithMessage("*not restored*");
            (await profile.Should().ThrowAsync<ModelContextProtocol.McpException>())
                .WithMessage("*not restored*");

            await recorder.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task TheRecorderCompletesWithoutFaulting_WhenTheHubThrows()
    {
        (TradeTapeRecorder recorder, FakeMarketHub hub, TopstepXDbContext database, ServiceProvider services, _) =
            Build(McpTransport.Http, recordTape: true);

        hub.ConnectThrows = new InvalidOperationException("the market hub refused the handshake");

        await using (services)
        await using (database)
        {
            await recorder.StartAsync(CancellationToken.None);

            recorder.ExecuteTask.Should().NotBeNull();
            await recorder.ExecuteTask!;

            recorder.ExecuteTask.IsFaulted.Should().BeFalse(
                "a faulted ExecuteTask is what Program.AnyFaulted reads, and would turn a stdio EOF into a crash");
            hub.TradeSubscriptions.Should().BeEmpty();

            await recorder.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AForcedDisconnectAndReconnect_ResumesPrints_OnlyWhenSubscribeRunsAgain()
    {
        CollectingLogger logger = new();
        (TradeTapeRecorder recorder, FakeMarketHub hub, TopstepXDbContext database, ServiceProvider services, _) =
            Build(McpTransport.Http, recordTape: true, logger: logger);

        await using (services)
        await using (database)
        {
            await recorder.StartAsync(CancellationToken.None);
            await WaitUntil(() => hub.TradeSubscriptions.Count > 0);

            int subscribesBeforeOutage = hub.SubscribeAttempts;
            hub.TransitionsIntoConnected.Should().Be(1, "the first connect must be a real Connected transition");

            hub.Raise(Print(Utc(13, 45, 0), TradeLogType.Buy, price: 5000m));
            await WaitUntil(() => recorder.RecordedPrints == 1);

            hub.SimulateMarketDisconnect();
            hub.TradeSubscriptions.Should().BeEmpty();
            hub.Raise(Print(Utc(13, 45, 1), TradeLogType.Buy, price: 5001m));
            recorder.RecordedPrints.Should().Be(1, "a print during the outage must not land — the tape is silent");

            hub.SimulateMarketReconnect();
            hub.TransitionsIntoConnected.Should().Be(2, "the test must drive a second Connected, not inspect a path that never ran");
            await WaitUntil(() => hub.SubscribeAttempts > subscribesBeforeOutage
                && hub.TradeSubscriptions.Contains("CON.F.US.TEST.Z26"));

            hub.Raise(Print(Utc(13, 46, 0), TradeLogType.Buy, price: 5002m));
            hub.RaisedToListeners.Should().Be(2, "the post-reconnect print must reach listeners, or subscribe did not restore the set");

            await WaitUntil(
                () => recorder.RecordedPrints == 2,
                because: $"prints={recorder.RecordedPrints} dropped={recorder.DroppedPrints} "
                    + $"faulted={recorder.ExecuteTask?.IsFaulted} "
                    + $"errors={string.Join(" | ", logger.Errors.Select(e => e.Message))}");

            database.Trades.Select(t => t.Price).Should().BeEquivalentTo([5000m, 5002m]);
            recorder.ExecuteTask.Should().NotBeNull();
            recorder.ExecuteTask!.IsFaulted.Should().BeFalse(
                "a faulted ExecuteTask is what Program.AnyFaulted reads");

            await recorder.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task GetFootprint_WhileListeningWithNoClosedRow_DoesNotTakeTheEmptyLedgerRefusal()
    {
        // First HTTP listen: subscribe is confirmed, TapeAvailability is Listening, and no
        // TapeCoverage row has been closed yet. A window that overlaps that listen is not "no tape"
        // (gh#365). A quiet market — zero Trades — must not look like a dead subscription.
        (TradeTapeRecorder recorder, FakeMarketHub hub, TopstepXDbContext database, ServiceProvider services, FakeTimeProvider clock) =
            Build(McpTransport.Http, recordTape: true);

        await using (services)
        await using (database)
        {
            TapeAvailabilityHolder tape = services.GetRequiredService<TapeAvailabilityHolder>();

            await recorder.StartAsync(CancellationToken.None);
            await WaitUntil(() => hub.TradeSubscriptions.Count > 0 && tape.For("ES").IsListening);

            DateTimeOffset listenStart = clock.GetUtcNow();
            database.Trades.Should().BeEmpty("lifecycle writes the ledger; rows are not inferred from Trades");
            CoverageRows(database).Should().NotBeEmpty(
                "a confirmed subscribe must open a TapeCoverage row the tools can read");

            await SeedEsCellAsync(database, listenStart);

            MarketDataTools tools = Tools(database, tape);
            DateTimeOffset to = listenStart.AddHours(2);

            ToolPayloads.FootprintSeries payload = await tools.GetFootprint(
                "ES", 5, listenStart, to, CancellationToken.None);

            payload.Covered.Start.Should().Be(listenStart);
            payload.Cells.Should().NotBeEmpty();

            await recorder.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AfterReconnect_GetFootprint_ConfinesToTheNewOpenRange_NotOnlyTheClosedOne()
    {
        // Disconnect closes A. Re-subscribe opens B. Confine of a window that spans both must
        // include B (the newest run), not stop at A (gh#365).
        (TradeTapeRecorder recorder, FakeMarketHub hub, TopstepXDbContext database, ServiceProvider services, FakeTimeProvider clock) =
            Build(McpTransport.Http, recordTape: true);

        await using (services)
        await using (database)
        {
            TapeAvailabilityHolder tape = services.GetRequiredService<TapeAvailabilityHolder>();

            await recorder.StartAsync(CancellationToken.None);
            await WaitUntil(() => hub.TradeSubscriptions.Count > 0 && tape.For("ES").IsListening);

            DateTimeOffset listenStart = clock.GetUtcNow();
            clock.Advance(TimeSpan.FromSeconds(1));
            hub.SimulateMarketDisconnect();
            await WaitUntil(() => CoverageRows(database).Count == 1);

            clock.Advance(TimeSpan.FromSeconds(1));
            DateTimeOffset outageEnd = clock.GetUtcNow();

            hub.SimulateMarketReconnect();
            await WaitUntil(() => hub.SubscribeAttempts >= 2 && tape.For("ES").IsListening);

            await SeedEsCellAsync(database, listenStart);

            MarketDataTools tools = Tools(database, tape);
            DateTimeOffset to = listenStart.AddHours(2);

            ToolPayloads.FootprintSeries payload = await tools.GetFootprint(
                "ES", 5, listenStart, to, CancellationToken.None);

            payload.Covered.Start.Should().Be(outageEnd, "the newest listening run is B, not the closed A");
            payload.Covered.End.Should().Be(to);

            await recorder.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ALeftoverStillListeningRow_IsDiscarded_WhenTheRecorderStarts()
    {
        DateTimeOffset leftoverStart = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        (TradeTapeRecorder recorder, FakeMarketHub hub, TopstepXDbContext database, ServiceProvider services, FakeTimeProvider clock) =
            Build(McpTransport.Http, recordTape: true);

        await using (services)
        await using (database)
        {
            database.TapeCoverage.Add(new TapeCoverageRecord
            {
                Venue = "test",
                Instrument = "ES",
                ContractId = "CON.F.US.TEST.Z26",
                RangeStart = leftoverStart,
                RangeEnd = TapeCoverageRecord.StillListeningEnd,
                RecordedAt = leftoverStart,
            });
            await database.SaveChangesAsync();

            await recorder.StartAsync(CancellationToken.None);
            await WaitUntil(() => hub.TradeSubscriptions.Count > 0);

            DateTimeOffset listenStart = clock.GetUtcNow();
            IReadOnlyList<TapeCoverageRecord> rows = CoverageRows(database);
            rows.Should().NotContain(row => row.RangeStart == leftoverStart);
            rows.Should().ContainSingle(row =>
                row.RangeStart == listenStart
                && row.RangeEnd == TapeCoverageRecord.StillListeningEnd);

            await recorder.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ASecondRecordersStart_DoesNotDeleteTheOpenRowOfAnInstrumentItDoesNotRecord()
    {
        // Two HTTP recorders with RecordTape on, split by MarketData__Instruments, against one
        // store. A is listening on ES; B records only NQ. B's start must discard its own NQ
        // leftover and leave A's ES row alone — an unscoped discard deletes every open row in
        // the table, and A then takes the empty-ledger refusal gh#365 closed while it is still
        // writing prints (gh#382). The coverage range is not recoverable: there is no
        // market-tape backfill (ADR-0016).
        string sharedStore = Guid.NewGuid().ToString();
        DateTimeOffset nqLeftoverStart = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        (TradeTapeRecorder recorderA, FakeMarketHub hubA, TopstepXDbContext database, ServiceProvider servicesA, FakeTimeProvider clock) =
            Build(
                McpTransport.Http,
                recordTape: true,
                instruments: "ES",
                gateway: new PerInstrumentGateway(),
                sharedDatabaseName: sharedStore);

        await using (servicesA)
        await using (database)
        {
            TapeAvailabilityHolder tapeA = servicesA.GetRequiredService<TapeAvailabilityHolder>();

            database.TapeCoverage.Add(new TapeCoverageRecord
            {
                Venue = "test",
                Instrument = "NQ",
                ContractId = "CON.F.US.ENQ.Z26",
                RangeStart = nqLeftoverStart,
                RangeEnd = TapeCoverageRecord.StillListeningEnd,
                RecordedAt = nqLeftoverStart,
            });
            await database.SaveChangesAsync();

            await recorderA.StartAsync(CancellationToken.None);
            await WaitUntil(() => hubA.TradeSubscriptions.Count > 0 && tapeA.For("ES").IsListening);

            DateTimeOffset listenStart = clock.GetUtcNow();
            CoverageRows(database).Should().ContainSingle(row =>
                row.Instrument == "ES"
                && row.RangeStart == listenStart
                && row.RangeEnd == TapeCoverageRecord.StillListeningEnd);

            await SeedEsCellAsync(database, listenStart);

            (TradeTapeRecorder recorderB, FakeMarketHub hubB, _, ServiceProvider servicesB, _) =
                Build(
                    McpTransport.Http,
                    recordTape: true,
                    instruments: "NQ",
                    gateway: new PerInstrumentGateway(),
                    sharedDatabaseName: sharedStore);

            await using (servicesB)
            {
                TapeAvailabilityHolder tapeB = servicesB.GetRequiredService<TapeAvailabilityHolder>();

                await recorderB.StartAsync(CancellationToken.None);
                await WaitUntil(() => hubB.TradeSubscriptions.Count > 0 && tapeB.For("NQ").IsListening);

                IReadOnlyList<TapeCoverageRecord> rows = CoverageRows(database);

                rows.Should().ContainSingle(
                    row => row.Instrument == "ES"
                        && row.RangeStart == listenStart
                        && row.RangeEnd == TapeCoverageRecord.StillListeningEnd,
                    "a recorder that does not record ES must not delete the ES recorder's open listen");

                rows.Should().NotContain(
                    row => row.Instrument == "NQ" && row.RangeStart == nqLeftoverStart,
                    "B's own leftover from a previous run is still a crash leftover it supersedes");

                rows.Should().ContainSingle(
                    row => row.Instrument == "NQ"
                        && row.RangeStart == listenStart
                        && row.RangeEnd == TapeCoverageRecord.StillListeningEnd);

                tapeA.For("ES").IsListening.Should().BeTrue();

                MarketDataTools tools = Tools(database, tapeA);
                ToolPayloads.FootprintSeries payload = await tools.GetFootprint(
                    "ES", 5, listenStart, listenStart.AddHours(2), CancellationToken.None);

                payload.Covered.Start.Should().Be(listenStart);
                payload.Cells.Should().NotBeEmpty();

                await recorderB.StopAsync(CancellationToken.None);
            }

            await recorderA.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(McpTransport.Stdio, true)]
    [InlineData(McpTransport.Http, false)]
    public async Task ALeftoverStillListeningRow_IsNotDeleted_WhenTheStartDoesNotRecord(
        McpTransport transport,
        bool recordTape)
    {
        // A Cowork stdio child, or HTTP with the switch off, still serves tools against the
        // same store a live HTTP recorder may be writing. Discard must not run on a start
        // that will not record, or that child deletes the HTTP process's still-open row
        // (gh#378). IsListening is the confine guard: a leftover sentinel is not ordinary
        // coverage for this process's own reads.
        DateTimeOffset leftoverStart = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        (TradeTapeRecorder recorder, _, TopstepXDbContext database, ServiceProvider services, _) =
            Build(transport, recordTape);

        await using (services)
        await using (database)
        {
            TapeAvailabilityHolder tape = services.GetRequiredService<TapeAvailabilityHolder>();
            await SeedStillListeningAsync(database, leftoverStart);

            await recorder.StartAsync(CancellationToken.None);
            await WaitUntil(() => recorder.ExecuteTask?.IsCompleted == true);

            CoverageRows(database).Should().ContainSingle(
                row => row.RangeStart == leftoverStart
                    && row.RangeEnd == TapeCoverageRecord.StillListeningEnd,
                "a start that will not record must not delete another process's still-open row");

            tape.Value.IsListening.Should().BeFalse();

            Func<Task> withoutHolder = () => new VolumeProfileService(database).ReadAsync(
                "test",
                new InstrumentId("ES"),
                5,
                leftoverStart.AddHours(1),
                leftoverStart.AddHours(3),
                CancellationToken.None);
            Func<Task> withHolder = () => new VolumeProfileService(database, tape).ReadAsync(
                "test",
                new InstrumentId("ES"),
                5,
                leftoverStart.AddHours(1),
                leftoverStart.AddHours(3),
                CancellationToken.None);

            await withoutHolder.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no tape*");
            await withHolder.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no tape*");

            await recorder.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AStdioStart_DoesNotDeleteALiveHttpListensStillOpenRow()
    {
        // HTTP is Listening with a still-open TapeCoverage row. A Cowork stdio child against
        // the same store must not delete that row — HTTP get_footprint would then take the
        // empty-ledger refusal gh#365 closed (gh#378).
        string sharedStore = Guid.NewGuid().ToString();
        (TradeTapeRecorder http, FakeMarketHub hub, TopstepXDbContext database, ServiceProvider httpServices, FakeTimeProvider clock) =
            Build(McpTransport.Http, recordTape: true, sharedDatabaseName: sharedStore);

        await using (httpServices)
        await using (database)
        {
            TapeAvailabilityHolder httpTape = httpServices.GetRequiredService<TapeAvailabilityHolder>();

            await http.StartAsync(CancellationToken.None);
            await WaitUntil(() => hub.TradeSubscriptions.Count > 0 && httpTape.For("ES").IsListening);

            DateTimeOffset listenStart = clock.GetUtcNow();
            CoverageRows(database).Should().ContainSingle(row =>
                row.RangeStart == listenStart
                && row.RangeEnd == TapeCoverageRecord.StillListeningEnd);

            await SeedEsCellAsync(database, listenStart);

            (TradeTapeRecorder stdio, _, _, ServiceProvider stdioServices, _) =
                Build(McpTransport.Stdio, recordTape: true, sharedDatabaseName: sharedStore);

            await using (stdioServices)
            {
                await stdio.StartAsync(CancellationToken.None);
                await WaitUntil(() => stdio.ExecuteTask?.IsCompleted == true);

                CoverageRows(database).Should().ContainSingle(
                    row => row.RangeStart == listenStart
                        && row.RangeEnd == TapeCoverageRecord.StillListeningEnd,
                    "a Cowork stdio child must not delete the HTTP process's still-open listen");

                httpTape.For("ES").IsListening.Should().BeTrue();

                MarketDataTools tools = Tools(database, httpTape);
                DateTimeOffset to = listenStart.AddHours(2);

                ToolPayloads.FootprintSeries payload = await tools.GetFootprint(
                    "ES", 5, listenStart, to, CancellationToken.None);

                payload.Covered.Start.Should().Be(listenStart);
                payload.Cells.Should().NotBeEmpty();

                await stdio.StopAsync(CancellationToken.None);
            }

            await http.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ALeftoverStillListeningRow_IsNotDeleted_WhenRecordTapeIsOnButThereIsNoVenueClient()
    {
        DateTimeOffset leftoverStart = new(2026, 8, 1, 12, 0, 0, TimeSpan.Zero);
        (TradeTapeRecorder recorder, _, TopstepXDbContext database, ServiceProvider services, _) =
            Build(McpTransport.Http, recordTape: true, registerHub: false);

        await using (services)
        await using (database)
        {
            await SeedStillListeningAsync(database, leftoverStart);

            await recorder.StartAsync(CancellationToken.None);
            await WaitUntil(() => recorder.ExecuteTask?.IsCompleted == true);

            CoverageRows(database).Should().ContainSingle(
                row => row.RangeStart == leftoverStart
                    && row.RangeEnd == TapeCoverageRecord.StillListeningEnd,
                "a start that will not record must not delete a leftover still-open row");

            Func<Task> read = () => new VolumeProfileService(database).ReadAsync(
                "test",
                new InstrumentId("ES"),
                5,
                leftoverStart.AddHours(1),
                leftoverStart.AddHours(3),
                CancellationToken.None);

            await read.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no tape*");

            await recorder.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AnUnreplacedStillOpenRange_DoesNotMergeTheNextListen_AcrossAnOutage()
    {
        // Disconnect at the subscribe instant skips the pending close (end == start) and
        // leaves [A.start, 9999). The next subscribe must not store a second sentinel that
        // MergeAdjacent collapses into one envelope (R-9.5).
        (TradeTapeRecorder recorder, FakeMarketHub hub, TopstepXDbContext database, ServiceProvider services, FakeTimeProvider clock) =
            Build(McpTransport.Http, recordTape: true);

        await using (services)
        await using (database)
        {
            TapeAvailabilityHolder tape = services.GetRequiredService<TapeAvailabilityHolder>();

            await recorder.StartAsync(CancellationToken.None);
            await WaitUntil(() => hub.TradeSubscriptions.Count > 0 && tape.For("ES").IsListening);

            DateTimeOffset listenStart = clock.GetUtcNow();
            hub.SimulateMarketDisconnect();
            await WaitUntil(() => !tape.For("ES").IsListening);

            clock.Advance(TimeSpan.FromSeconds(1));
            DateTimeOffset outageEnd = clock.GetUtcNow();

            hub.SimulateMarketReconnect();
            await WaitUntil(() => hub.SubscribeAttempts >= 2 && tape.For("ES").IsListening);

            IReadOnlyList<TapeCoverageRecord> afterReconnect = CoverageRows(database);
            afterReconnect.Should().NotContain(
                row => row.RangeStart == listenStart && row.RangeEnd == TapeCoverageRecord.StillListeningEnd,
                "the unreplaced A sentinel must be retired so it cannot merge with B");
            afterReconnect.Should().ContainSingle(row =>
                row.RangeStart == outageEnd
                && row.RangeEnd == TapeCoverageRecord.StillListeningEnd);

            await SeedEsCellAsync(database, listenStart);

            MarketDataTools tools = Tools(database, tape);
            DateTimeOffset to = listenStart.AddHours(2);

            ToolPayloads.FootprintSeries payload = await tools.GetFootprint(
                "ES", 5, listenStart, to, CancellationToken.None);

            payload.Covered.Start.Should().Be(outageEnd, "the outage is a hole, not taped by a merged sentinel");

            await recorder.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AFailedClosePersist_DoesNotLeaveAStillOpenRow_AsOrdinaryCoverageDuringTheOutage()
    {
        // Pending closes are taken before SaveChanges. A fault that is only logged must not
        // leave [A.start, 9999) for ConfineAsync: get_key_levels volume-* has no tape Require,
        // and Narrowed is false for that sentinel (gh#221).
        CollectingLogger logger = new();
        (TradeTapeRecorder recorder, FakeMarketHub hub, TopstepXDbContext database, ServiceProvider services, FakeTimeProvider clock) =
            Build(
                McpTransport.Http,
                recordTape: true,
                logger: logger,
                extraInterceptor: new FailingClosedCoverageInterceptor());

        await using (services)
        await using (database)
        {
            TapeAvailabilityHolder tape = services.GetRequiredService<TapeAvailabilityHolder>();

            await recorder.StartAsync(CancellationToken.None);
            await WaitUntil(() => hub.TradeSubscriptions.Count > 0 && tape.For("ES").IsListening);

            DateTimeOffset listenStart = clock.GetUtcNow();
            await SeedEsCellAsync(database, listenStart);

            clock.Advance(TimeSpan.FromSeconds(1));
            hub.SimulateMarketDisconnect();
            await WaitUntil(() =>
                tape.For("ES").Reason == TapeUnavailableReason.Reconnecting
                && logger.Errors.Exists(entry =>
                    entry.Message.Contains("lifecycle", StringComparison.OrdinalIgnoreCase)));

            CoverageRows(database).Should().NotContain(
                row => row.RangeEnd == TapeCoverageRecord.StillListeningEnd,
                "a failed close persist must retire the sentinel so it is not ordinary coverage");

            Func<Task> read = () => new VolumeProfileService(database).ReadAsync(
                "test",
                new InstrumentId("ES"),
                5,
                listenStart,
                listenStart.AddHours(2),
                CancellationToken.None);

            await read.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no tape*");

            recorder.ExecuteTask.Should().NotBeNull();
            recorder.ExecuteTask!.IsFaulted.Should().BeFalse(
                "a faulted ExecuteTask is what Program.AnyFaulted reads");

            await recorder.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AFailedRetirePersist_DoesNotLeaveAStillOpenRow_AsOrdinaryCoverageDuringTheOutage()
    {
        // The retire SaveChanges is the first persist of a close. A throw there leaves
        // [A.start, 9999) in the store. ConfineAsync must not treat that sentinel as a
        // taped window while Reconnecting — get_key_levels volume-* has no tape Require.
        CollectingLogger logger = new();
        (TradeTapeRecorder recorder, FakeMarketHub hub, TopstepXDbContext database, ServiceProvider services, FakeTimeProvider clock) =
            Build(
                McpTransport.Http,
                recordTape: true,
                logger: logger,
                extraInterceptor: new FailingRetireCoverageInterceptor());

        await using (services)
        await using (database)
        {
            TapeAvailabilityHolder tape = services.GetRequiredService<TapeAvailabilityHolder>();

            await recorder.StartAsync(CancellationToken.None);
            await WaitUntil(() => hub.TradeSubscriptions.Count > 0 && tape.For("ES").IsListening);

            DateTimeOffset listenStart = clock.GetUtcNow();
            await SeedEsCellAsync(database, listenStart);

            clock.Advance(TimeSpan.FromSeconds(1));
            hub.SimulateMarketDisconnect();
            await WaitUntil(() =>
                tape.For("ES").Reason == TapeUnavailableReason.Reconnecting
                && logger.Errors.Exists(entry =>
                    entry.Message.Contains("lifecycle", StringComparison.OrdinalIgnoreCase)));

            CoverageRows(database).Should().Contain(
                row => row.RangeEnd == TapeCoverageRecord.StillListeningEnd,
                "this path pins a retire that did not land; the row may still be stored");

            Func<Task> withoutHolder = () => new VolumeProfileService(database).ReadAsync(
                "test",
                new InstrumentId("ES"),
                5,
                listenStart,
                listenStart.AddHours(2),
                CancellationToken.None);
            Func<Task> whileReconnecting = () => new VolumeProfileService(database, tape).ReadAsync(
                "test",
                new InstrumentId("ES"),
                5,
                listenStart,
                listenStart.AddHours(2),
                CancellationToken.None);

            await withoutHolder.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no tape*");
            await whileReconnecting.Should().ThrowAsync<InvalidOperationException>().WithMessage("*no tape*");

            recorder.ExecuteTask.Should().NotBeNull();
            recorder.ExecuteTask!.IsFaulted.Should().BeFalse(
                "a faulted ExecuteTask is what Program.AnyFaulted reads");

            await recorder.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ALaterFlushAfterAFailedRetire_DoesNotDeleteTheRestoredListen()
    {
        // The retire throw requeues the close. Restore writes B and is Listening; a later
        // flush (NQ re-subscribe fail) must retire A by RangeStart, not whatever sentinel
        // is live. Deleting B is the empty-ledger refusal while Listening (gh#365).
        CollectingLogger logger = new();
        (TradeTapeRecorder recorder, FakeMarketHub hub, TopstepXDbContext database, ServiceProvider services, FakeTimeProvider clock) =
            Build(
                McpTransport.Http,
                recordTape: true,
                instruments: "ES,NQ",
                gateway: new PerInstrumentGateway(),
                logger: logger,
                extraInterceptor: new FailingFirstRetireCoverageInterceptor());

        await using (services)
        await using (database)
        {
            TapeAvailabilityHolder tape = services.GetRequiredService<TapeAvailabilityHolder>();

            await recorder.StartAsync(CancellationToken.None);
            await WaitUntil(() =>
                tape.For("ES").IsListening && tape.For("NQ").IsListening);

            DateTimeOffset listenA = clock.GetUtcNow();
            clock.Advance(TimeSpan.FromSeconds(1));
            hub.SimulateMarketDisconnect();
            await WaitUntil(() =>
                tape.For("ES").Reason == TapeUnavailableReason.Reconnecting
                && logger.Errors.Exists(entry =>
                    entry.Message.Contains("lifecycle", StringComparison.OrdinalIgnoreCase)));

            hub.SubscribeThrowsFor = "CON.F.US.ENQ.Z26";
            clock.Advance(TimeSpan.FromSeconds(1));
            DateTimeOffset listenB = clock.GetUtcNow();
            hub.SimulateMarketReconnect();
            await WaitUntil(() =>
                tape.For("ES").IsListening
                && tape.For("NQ").Reason == TapeUnavailableReason.ConnectedButNotSubscribed
                && CoverageRows(database).Exists(row =>
                    row.Instrument == "ES"
                    && row.RangeStart == listenA
                    && row.RangeEnd != TapeCoverageRecord.StillListeningEnd));

            CoverageRows(database).Should().Contain(row =>
                row.Instrument == "ES"
                && row.RangeStart == listenB
                && row.RangeEnd == TapeCoverageRecord.StillListeningEnd);

            await SeedEsCellAsync(database, listenB);
            MarketDataTools tools = Tools(database, tape);
            ToolPayloads.FootprintSeries payload = await tools.GetFootprint(
                "ES", 5, listenB, listenB.AddHours(2), CancellationToken.None);

            payload.Covered.Start.Should().Be(listenB);
            payload.Cells.Should().NotBeEmpty();

            recorder.ExecuteTask.Should().NotBeNull();
            recorder.ExecuteTask!.IsFaulted.Should().BeFalse(
                "a faulted ExecuteTask is what Program.AnyFaulted reads");

            await recorder.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task TapeCoverage_ClosesAcrossAnOutage_WithNoOverlapOrGapAgainstTheRangesEitherSide()
    {
        (TradeTapeRecorder recorder, FakeMarketHub hub, TopstepXDbContext database, ServiceProvider services, FakeTimeProvider clock) =
            Build(McpTransport.Http, recordTape: true);

        await using (services)
        await using (database)
        {
            await recorder.StartAsync(CancellationToken.None);
            await WaitUntil(() => hub.TradeSubscriptions.Count > 0);

            DateTimeOffset listenStart = clock.GetUtcNow();
            clock.Advance(TimeSpan.FromSeconds(1));
            DateTimeOffset outageStart = clock.GetUtcNow();

            hub.SimulateMarketDisconnect();
            await WaitUntil(() => CoverageRows(database).Count == 1);

            clock.Advance(TimeSpan.FromSeconds(1));
            DateTimeOffset outageEnd = clock.GetUtcNow();

            hub.SimulateMarketReconnect();
            await WaitUntil(() => hub.SubscribeAttempts >= 2);

            clock.Advance(TimeSpan.FromSeconds(1));
            DateTimeOffset listenEnd = clock.GetUtcNow();

            await recorder.StopAsync(CancellationToken.None);
            await WaitUntil(() => CoverageRows(database).Count == 2);

            IReadOnlyList<TapeCoverageRecord> ranges = CoverageRows(database);
            ranges.Should().HaveCount(2);

            TapeCoverageRecord before = ranges[0];
            TapeCoverageRecord after = ranges[1];

            before.Venue.Should().Be("test");
            before.Instrument.Should().Be("ES");
            before.ContractId.Should().Be("CON.F.US.TEST.Z26");
            before.RangeStart.Should().Be(listenStart);
            before.RangeEnd.Should().Be(outageStart);

            after.Venue.Should().Be("test");
            after.Instrument.Should().Be("ES");
            after.ContractId.Should().Be("CON.F.US.TEST.Z26");
            after.RangeStart.Should().Be(outageEnd);
            after.RangeEnd.Should().Be(listenEnd);

            // Half-open [Start, End): the outage is exactly [before.End, after.Start).
            before.RangeEnd.Should().BeOnOrBefore(after.RangeStart);
            before.RangeEnd.Should().Be(outageStart, "the closed range must meet the outage with no slack");
            after.RangeStart.Should().Be(outageEnd, "the next range must meet the outage with no slack");
            (after.RangeStart - before.RangeEnd).Should().Be(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task AFailedOpenPersist_AfterAConfirmedSubscribe_DropsTheVenueSubscription_AndDoesNotStorePrints()
    {
        // Subscribe confirmed, then PersistOpenRangeAsync throws. Treating that as a refused
        // subscribe leaves the hub live and Trades filling a hole TapeCoverage never opened
        // (gh#376 / R-5.7). The venue subscription must be dropped; tools must not see a
        // live tape with no ledger row.
        CollectingLogger logger = new();
        (TradeTapeRecorder recorder, FakeMarketHub hub, TopstepXDbContext database, ServiceProvider services, _) =
            Build(
                McpTransport.Http,
                recordTape: true,
                logger: logger,
                extraInterceptor: new FailingOpenCoverageInterceptor());

        await using (services)
        await using (database)
        {
            TapeAvailabilityHolder tape = services.GetRequiredService<TapeAvailabilityHolder>();

            await recorder.StartAsync(CancellationToken.None);
            await WaitUntil(() =>
                hub.SubscribeAttempts >= 1
                && logger.Errors.Exists(entry => entry.Exception is not null));

            recorder.ExecuteTask.Should().NotBeNull();
            recorder.ExecuteTask!.IsFaulted.Should().BeFalse(
                "a faulted ExecuteTask is what Program.AnyFaulted reads");

            hub.TradeSubscriptions.Should().BeEmpty(
                "a store fault after a confirmed subscribe must drop the venue subscription");
            hub.UnsubscribeAttempts.Should().BeGreaterThan(0);

            tape.For("ES").IsListening.Should().BeFalse();
            CoverageRows(database).Should().NotContain(
                row => row.RangeEnd == TapeCoverageRecord.StillListeningEnd,
                "a persist that never landed must not leave a still-open ledger row");

            hub.Raise(Print(Utc(13, 46, 0), TradeLogType.Buy, price: 1m));
            recorder.RecordedPrints.Should().Be(0, "prints must not land after the subscription is dropped");

            await SeedEsCellAsync(database, _receipt);
            MarketDataTools tools = Tools(database, tape);
            Func<Task> footprint = () => tools.GetFootprint(
                "ES", 5, _receipt, _receipt.AddHours(2), CancellationToken.None);

            (await footprint.Should().ThrowAsync<ModelContextProtocol.McpException>())
                .WithMessage("*not restored*");

            await recorder.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ALaterRestore_AfterAFailedOpenPersist_OpensANewRange_AndDoesNotCoverTheHole()
    {
        CollectingLogger logger = new();
        (TradeTapeRecorder recorder, FakeMarketHub hub, TopstepXDbContext database, ServiceProvider services, FakeTimeProvider clock) =
            Build(
                McpTransport.Http,
                recordTape: true,
                logger: logger,
                extraInterceptor: new FailingFirstOpenCoverageInterceptor());

        await using (services)
        await using (database)
        {
            TapeAvailabilityHolder tape = services.GetRequiredService<TapeAvailabilityHolder>();

            await recorder.StartAsync(CancellationToken.None);
            await WaitUntil(() =>
                hub.SubscribeAttempts >= 1
                && logger.Errors.Exists(entry => entry.Exception is not null));

            DateTimeOffset failedListen = clock.GetUtcNow();
            clock.Advance(TimeSpan.FromSeconds(1));
            DateTimeOffset restoreStart = clock.GetUtcNow();

            hub.SimulateMarketDisconnect();
            hub.SimulateMarketReconnect();
            await WaitUntil(() => hub.SubscribeAttempts >= 2 && tape.For("ES").IsListening);

            CoverageRows(database).Should().NotContain(
                row => row.RangeStart == failedListen,
                "the listen that never reached the store is a hole, not a taped window");
            CoverageRows(database).Should().ContainSingle(row =>
                row.RangeStart == restoreStart
                && row.RangeEnd == TapeCoverageRecord.StillListeningEnd);

            await SeedEsCellAsync(database, restoreStart);
            MarketDataTools tools = Tools(database, tape);
            ToolPayloads.FootprintSeries payload = await tools.GetFootprint(
                "ES", 5, failedListen, failedListen.AddHours(2), CancellationToken.None);

            payload.Covered.Start.Should().Be(restoreStart, "the later restore must not backdate over the hole");

            await recorder.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task APrintQueuedBeforeAFailedOpenPersist_IsNotStored_AndALaterFootprintDoesNotCoverItsVolume()
    {
        // PersistOpenRangeAsync holds _store. A print queued then is drained after the
        // throw releases that lock. Writing it leaves volume that ProjectAsync will fold
        // into the next listen's 5-minute bar (gh#376 review).
        CollectingLogger logger = new();
        TaskCompletionSource hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        (TradeTapeRecorder recorder, FakeMarketHub hub, TopstepXDbContext database, ServiceProvider services, FakeTimeProvider clock) =
            Build(
                McpTransport.Http,
                recordTape: true,
                logger: logger,
                extraInterceptor: new FailingFirstHeldOpenCoverageInterceptor(hold, started));

        await using (services)
        await using (database)
        {
            TapeAvailabilityHolder tape = services.GetRequiredService<TapeAvailabilityHolder>();

            await recorder.StartAsync(CancellationToken.None);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

            hub.Raise(Print(Utc(14, 30, 0), TradeLogType.Buy, price: 5000.25m));
            hold.SetResult();
            await WaitUntil(() =>
                hub.UnsubscribeAttempts >= 1
                && logger.Errors.Exists(entry => entry.Exception is not null));

            clock.Advance(TimeSpan.FromSeconds(1));
            DateTimeOffset restoreStart = clock.GetUtcNow();
            hub.SimulateMarketDisconnect();
            hub.SimulateMarketReconnect();
            await WaitUntil(() => hub.SubscribeAttempts >= 2 && tape.For("ES").IsListening);

            recorder.RecordedPrints.Should().Be(0, "a queued print must not land without a ledger row");
            database.ChangeTracker.Clear();
            database.Trades.Should().BeEmpty(
                "the uncovered print must still be absent after the later listen opens");

            CoverageRows(database).Should().ContainSingle(row =>
                row.RangeStart == restoreStart
                && row.RangeEnd == TapeCoverageRecord.StillListeningEnd);

            MarketDataTools tools = Tools(database, tape);
            Func<Task<ToolPayloads.FootprintSeries>> footprint = () => tools.GetFootprint(
                "ES", 5, _receipt, _receipt.AddHours(2), CancellationToken.None);

            (await footprint.Should().ThrowAsync<ModelContextProtocol.McpException>())
                .WithMessage("*", "the uncovered print must not become a covered cell");

            await recorder.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AHubDropDuringUnsubscribeAfterAFailedOpenPersist_DoesNotWriteAClosedRangeForThatListen()
    {
        // _openRanges assigned before PersistOpenRangeAsync, removed after unsubscribe
        // returns. A disconnect mid-await snapshots the never-stored listen into
        // _pendingCloses; PersistPendingClosesAsync writes [T_failed, disconnectEnd)
        // even when no still-open row exists (gh#376 review).
        CollectingLogger logger = new();
        TaskCompletionSource hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        (TradeTapeRecorder recorder, FakeMarketHub hub, TopstepXDbContext database, ServiceProvider services, FakeTimeProvider clock) =
            Build(
                McpTransport.Http,
                recordTape: true,
                logger: logger,
                extraInterceptor: new FailingFirstOpenCoverageInterceptor());

        hub.UnsubscribeHold = hold;
        hub.UnsubscribeStarted = started;

        await using (services)
        await using (database)
        {
            TapeAvailabilityHolder tape = services.GetRequiredService<TapeAvailabilityHolder>();

            await recorder.StartAsync(CancellationToken.None);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

            DateTimeOffset failedListen = clock.GetUtcNow();
            clock.Advance(TimeSpan.FromSeconds(1));
            DateTimeOffset disconnectAt = clock.GetUtcNow();

            hub.SimulateMarketDisconnect();
            hold.SetResult();

            clock.Advance(TimeSpan.FromSeconds(1));
            DateTimeOffset restoreStart = clock.GetUtcNow();
            hub.SimulateMarketReconnect();
            await WaitUntil(() => hub.SubscribeAttempts >= 2 && tape.For("ES").IsListening);

            CoverageRows(database).Should().NotContain(
                row => row.RangeStart == failedListen,
                "a hub drop during the drop-subscribe must not close a listen that never reached the store");
            CoverageRows(database).Should().NotContain(
                row => row.RangeEnd == disconnectAt);
            CoverageRows(database).Should().ContainSingle(row =>
                row.RangeStart == restoreStart
                && row.RangeEnd == TapeCoverageRecord.StillListeningEnd);

            await SeedEsCellAsync(database, restoreStart);
            MarketDataTools tools = Tools(database, tape);
            ToolPayloads.FootprintSeries payload = await tools.GetFootprint(
                "ES", 5, failedListen, failedListen.AddHours(2), CancellationToken.None);

            payload.Covered.Start.Should().Be(restoreStart, "the phantom close must not tape the hole");

            await recorder.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AHubDropWhileTheOpenPersistIsInFlight_ClosesThatListen_WhenThePersistThenLands()
    {
        // The open persist is a query plus SaveChanges. A drop while it runs must still be able
        // to snapshot the listen: lose that snapshot and the still-open row it then writes is
        // closed by the next shutdown, taping straight through the outage (gh#376 review).
        TaskCompletionSource hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        (TradeTapeRecorder recorder, FakeMarketHub hub, TopstepXDbContext database, ServiceProvider services, FakeTimeProvider clock) =
            Build(
                McpTransport.Http,
                recordTape: true,
                extraInterceptor: new HeldFirstOpenCoverageInterceptor(hold, started));

        await using (services)
        await using (database)
        {
            await recorder.StartAsync(CancellationToken.None);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));

            DateTimeOffset listen = clock.GetUtcNow();
            clock.Advance(TimeSpan.FromSeconds(1));
            DateTimeOffset disconnectAt = clock.GetUtcNow();

            hub.SimulateMarketDisconnect();
            hold.SetResult();

            await WaitUntil(
                () => CoverageRows(database).Exists(row => row.RangeEnd == disconnectAt),
                "a listen that reached the store is closed at the drop that interrupted it");

            CoverageRows(database).Should().ContainSingle(row =>
                row.RangeStart == listen && row.RangeEnd == disconnectAt);
            CoverageRows(database).Should().NotContain(
                row => row.RangeEnd == TapeCoverageRecord.StillListeningEnd,
                "a sentinel nobody closes is coverage across an outage");

            await recorder.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task APrintQueuedWhileTheSubscribeIsInFlight_IsNotStored_WhenTheOpenPersistThenFails()
    {
        // The venue can print as soon as it accepts the subscribe, so a print is queued before
        // the confirm the coverage range starts from. Letting it through because it predates
        // that confirm stores volume no ledger row ever covered (gh#376 review).
        CollectingLogger logger = new();
        (TradeTapeRecorder recorder, FakeMarketHub hub, TopstepXDbContext database, ServiceProvider services, FakeTimeProvider clock) =
            Build(
                McpTransport.Http,
                recordTape: true,
                logger: logger,
                extraInterceptor: new FailingOpenCoverageInterceptor());

        hub.WhileSubscribing = () =>
        {
            hub.Raise(Print(Utc(14, 30, 0), TradeLogType.Buy, price: 5000.25m));
            clock.Advance(TimeSpan.FromSeconds(1));
        };

        await using (services)
        await using (database)
        {
            await recorder.StartAsync(CancellationToken.None);
            await WaitUntil(
                () => logger.Warnings.Exists(message =>
                    message.Contains("without a persisted coverage open", StringComparison.Ordinal)),
                "a print queued during the subscribe is suppressed with the listen whose open failed");

            recorder.RecordedPrints.Should().Be(0);
            database.ChangeTracker.Clear();
            database.Trades.Should().BeEmpty("volume with no ledger row is a hole, not a taped print");

            await recorder.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AResubscribeFailure_IsSurfaced_AndLeavesTheOpenRangeClosed()
    {
        CollectingLogger logger = new();
        (TradeTapeRecorder recorder, FakeMarketHub hub, TopstepXDbContext database, ServiceProvider services, FakeTimeProvider clock) =
            Build(McpTransport.Http, recordTape: true, logger: logger);

        await using (services)
        await using (database)
        {
            await recorder.StartAsync(CancellationToken.None);
            await WaitUntil(() => hub.TradeSubscriptions.Count > 0);

            clock.Advance(TimeSpan.FromSeconds(1));
            InvalidOperationException refused = new("the venue refused the trade re-subscribe");
            hub.SubscribeThrowsAfterFirst = refused;

            hub.SimulateMarketDisconnect();
            await WaitUntil(() => CoverageRows(database).Count == 1);

            hub.SimulateMarketReconnect();
            await WaitUntil(() => hub.SubscribeAttempts >= 2);

            recorder.ExecuteTask.Should().NotBeNull();
            recorder.ExecuteTask!.IsFaulted.Should().BeFalse(
                "a faulted ExecuteTask is what Program.AnyFaulted reads");

            logger.Errors.Should().Contain(entry =>
                entry.Exception == refused
                && entry.Message.Contains("re-subscribe", StringComparison.OrdinalIgnoreCase));

            CoverageRows(database).Should().ContainSingle(
                "a failed re-subscribe must not open a new range, and must leave the prior range closed");

            hub.Raise(Print(Utc(13, 46, 0), TradeLogType.Buy, price: 1m));
            recorder.RecordedPrints.Should().Be(0, "a failed restore is not listening");

            await recorder.StopAsync(CancellationToken.None);

            recorder.ExecuteTask.IsFaulted.Should().BeFalse();
            CoverageRows(database).Should().ContainSingle();
        }
    }

    [Fact]
    public async Task TapeHealth_ChangesWhenTheHubDropsAndRestores()
    {
        // Drive the hub. Reading a field the test just wrote is not a test.
        (TradeTapeRecorder recorder, FakeMarketHub hub, TopstepXDbContext database, ServiceProvider services, _) =
            Build(McpTransport.Http, recordTape: true);

        await using (services)
        await using (database)
        {
            TapeAvailabilityHolder tape = services.GetRequiredService<TapeAvailabilityHolder>();

            await recorder.StartAsync(CancellationToken.None);
            await WaitUntil(() => hub.TradeSubscriptions.Count > 0 && tape.For("ES").IsListening);

            tape.For("ES").Reason.Should().Be(TapeUnavailableReason.None);

            hub.SimulateMarketDisconnect();
            await WaitUntil(() => !tape.For("ES").IsListening);

            tape.For("ES").Reason.Should().Be(TapeUnavailableReason.Reconnecting);
            tape.For("ES").Explanation.Should().MatchRegex("(?i)reconnect|restore|restart");

            hub.SimulateMarketReconnect();
            await WaitUntil(() => tape.For("ES").IsListening);

            tape.For("ES").Reason.Should().Be(TapeUnavailableReason.None);

            await recorder.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ConnectedButNotSubscribed_IsNotReportedAsListening()
    {
        (TradeTapeRecorder recorder, FakeMarketHub hub, TopstepXDbContext database, ServiceProvider services, _) =
            Build(McpTransport.Http, recordTape: true);

        await using (services)
        await using (database)
        {
            TapeAvailabilityHolder tape = services.GetRequiredService<TapeAvailabilityHolder>();

            await recorder.StartAsync(CancellationToken.None);
            await WaitUntil(() => hub.TradeSubscriptions.Count > 0 && tape.For("ES").IsListening);

            hub.SubscribeThrowsAfterFirst =
                new InvalidOperationException("the venue refused the trade re-subscribe");

            hub.SimulateMarketDisconnect();
            await WaitUntil(() => tape.For("ES").Reason == TapeUnavailableReason.Reconnecting);

            hub.SimulateMarketReconnect();
            await WaitUntil(() => hub.SubscribeAttempts >= 2);

            tape.For("ES").IsListening.Should().BeFalse(
                "Connected is not listening — a failed restore must not look healthy");
            tape.For("ES").Reason.Should().Be(TapeUnavailableReason.ConnectedButNotSubscribed);
            tape.For("ES").Explanation.Should().MatchRegex("(?i)not restored|subscriptions");

            recorder.ExecuteTask.Should().NotBeNull();
            recorder.ExecuteTask!.IsFaulted.Should().BeFalse(
                "a faulted ExecuteTask is what Program.AnyFaulted reads");

            await recorder.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData(McpTransport.Stdio, true)]
    [InlineData(McpTransport.Http, false)]
    public async Task TapeHealth_IsNeverStarted_WhenStdioOrTheSwitchIsOff(
        McpTransport transport,
        bool recordTape)
    {
        (TradeTapeRecorder recorder, _, TopstepXDbContext database, ServiceProvider services, _) =
            Build(transport, recordTape);

        await using (services)
        await using (database)
        {
            TapeAvailabilityHolder tape = services.GetRequiredService<TapeAvailabilityHolder>();

            await recorder.StartAsync(CancellationToken.None);
            await WaitUntil(() => recorder.ExecuteTask?.IsCompleted == true);

            tape.Value.IsListening.Should().BeFalse();
            tape.Value.Reason.Should().Be(TapeUnavailableReason.NeverStarted);
            tape.Value.Explanation.Should().MatchRegex("(?i)http|recordtape");

            await recorder.StopAsync(CancellationToken.None);
        }
    }

    private static DateTime Utc(int hour, int minute, int second) =>
        new(2026, 8, 28, hour, minute, second, DateTimeKind.Utc);

    private static List<TapeCoverageRecord> CoverageRows(TopstepXDbContext database)
    {
        database.ChangeTracker.Clear();
        return [.. database.TapeCoverage.OrderBy(row => row.RangeStart)];
    }

    private static async Task SeedStillListeningAsync(TopstepXDbContext database, DateTimeOffset start)
    {
        database.TapeCoverage.Add(new TapeCoverageRecord
        {
            Venue = "test",
            Instrument = "ES",
            ContractId = "CON.F.US.TEST.Z26",
            RangeStart = start,
            RangeEnd = TapeCoverageRecord.StillListeningEnd,
            RecordedAt = start,
        });
        await database.SaveChangesAsync();
    }

    private static async Task SeedEsCellAsync(TopstepXDbContext database, DateTimeOffset bucket)
    {
        database.FootprintCells.Add(new FootprintCellRecord
        {
            Venue = "test",
            Instrument = "ES",
            ResolutionMinutes = 5,
            BucketStart = bucket,
            Price = 5000m,
            BuyVolume = 4,
            SellVolume = 1,
            RecordedAt = bucket,
        });
        await database.SaveChangesAsync();
    }

    private static async Task SeedNqCellsAsync(TopstepXDbContext database)
    {
        DateTimeOffset fourteen = new(2026, 8, 18, 14, 0, 0, TimeSpan.Zero);
        DateTimeOffset sixteen = new(2026, 8, 18, 16, 0, 0, TimeSpan.Zero);
        DateTimeOffset bucket = new(2026, 8, 18, 14, 30, 0, TimeSpan.Zero);

        database.TapeCoverage.Add(new TapeCoverageRecord
        {
            Venue = "test",
            Instrument = "NQ",
            ContractId = "CON.F.US.ENQ.Z26",
            RangeStart = fourteen,
            RangeEnd = sixteen,
            RecordedAt = sixteen,
        });
        database.FootprintCells.Add(new FootprintCellRecord
        {
            Venue = "test",
            Instrument = "NQ",
            ResolutionMinutes = 5,
            BucketStart = bucket,
            Price = 18000m,
            BuyVolume = 4,
            SellVolume = 1,
            RecordedAt = sixteen,
        });
        await database.SaveChangesAsync();
    }

    private static MarketDataTools Tools(TopstepXDbContext database, TapeAvailabilityHolder tape)
    {
        IOptions<MarketDataOptions> options = Options.Create(new MarketDataOptions
        {
            Instruments = "ES,NQ",
            MaxRows = 5_000,
            SessionCloseCentral = "16:00",
        });
        BarSessionCalendar calendar = BarSessionCalendar.Parse("16:00", []);
        IndicatorCatalog catalog = new(
            Options.Create(new IndicatorOptions { AtrPeriod = 3, RsiPeriod = 3 }), calendar);
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 8, 18, 16, 0, 0, TimeSpan.Zero));
        CountingGateway gateway = new([]);
        IndicatorProjector projector = new(database, catalog, NullLogger<IndicatorProjector>.Instance);
        BarCacheService cache = new(
            database, gateway, calendar, projector, clock, NullLogger<BarCacheService>.Instance);

        return new MarketDataTools(
            cache,
            database,
            new InstrumentRegistry(options),
            catalog,
            new IndicatorCacheService(
                database, catalog, projector, clock, NullLogger<IndicatorCacheService>.Instance),
            new LevelMethodCatalog(calendar),
            gateway,
            new ToolGuards(options),
            new StoreAvailabilityHolder(),
            clock,
            Options.Create(new KeyLevelDetectionOptions()),
            new VolumeProfileService(database, tape),
            tape);
    }

    private static TradeUpdate Print(
        DateTime timestamp,
        TradeLogType? type,
        decimal price,
        string contractId = "CON.F.US.TEST.Z26") =>
        new()
        {
            ContractId = contractId,
            SymbolId = "F.US.EP",
            Price = price,
            Timestamp = timestamp,
            Type = type,
            Volume = 3m,
        };

    private static (
        TradeTapeRecorder Recorder,
        FakeMarketHub Hub,
        TopstepXDbContext Database,
        ServiceProvider Services,
        FakeTimeProvider Clock)
        Build(
            McpTransport transport,
            bool recordTape,
            int channelCapacity = 16,
            TaskCompletionSource? persistHold = null,
            TaskCompletionSource? persistStarted = null,
            string instruments = "ES",
            IMarketDataGateway? gateway = null,
            ILogger<TradeTapeRecorder>? logger = null,
            bool registerHub = true,
            SaveChangesInterceptor? extraInterceptor = null,
            string? sharedDatabaseName = null)
    {
        FakeMarketHub hub = new();
        FakeTimeProvider clock = new(_receipt);
        string databaseName = sharedDatabaseName ?? Guid.NewGuid().ToString();
        DbContextOptionsBuilder<TopstepXDbContext> builder = new DbContextOptionsBuilder<TopstepXDbContext>()
            .UseInMemoryDatabase(databaseName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));
        if (persistHold is not null)
        {
            builder.AddInterceptors(new HoldingInterceptor(persistHold, persistStarted));
        }

        if (extraInterceptor is not null)
        {
            builder.AddInterceptors(extraInterceptor);
        }

        DbContextOptions<TopstepXDbContext> options = builder.Options;

        MarketDataOptions market = new() { Instruments = instruments, RecordTape = recordTape };
        McpOptions mcp = new()
        {
            Transport = transport,
            HttpBearerToken = transport == McpTransport.Http ? "a-token" : string.Empty,
        };

        TapeAvailabilityHolder tape = new();
        ServiceCollection services = new();
        services.AddSingleton<IOptions<MarketDataOptions>>(Options.Create(market));
        services.AddSingleton<IOptions<McpOptions>>(Options.Create(mcp));
        services.AddSingleton(new InstrumentRegistry(Options.Create(market)));
        services.AddSingleton<TimeProvider>(clock);
        if (registerHub)
        {
            services.AddSingleton(hub);
            services.AddSingleton<MarqSpec.Client.ProjectX.WebSocket.IProjectXWebSocketClient>(hub);
        }

        services.AddSingleton(tape);
        services.AddScoped<IMarketDataGateway>(_ => gateway ?? new CountingGateway([]));
        services.AddScoped(_ => new TopstepXDbContext(options));

        ServiceProvider provider = services.BuildServiceProvider();
        TopstepXDbContext database = new(options);

        TradeTapeRecorder recorder = new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(market),
            Options.Create(mcp),
            provider.GetRequiredService<InstrumentRegistry>(),
            clock,
            logger ?? NullLogger<TradeTapeRecorder>.Instance,
            tape,
            channelCapacity);

        return (recorder, hub, database, provider, clock);
    }

    /// <summary>
    /// Captures <see cref="LogLevel.Error"/> so a swallowed re-subscribe failure is visible, and
    /// <see cref="LogLevel.Warning"/> so a suppressed print is a signal a test can wait on.
    /// </summary>
    private sealed class CollectingLogger : ILogger<TradeTapeRecorder>
    {
        public List<(Exception? Exception, string Message)> Errors { get; } = [];

        public List<string> Warnings { get; } = [];

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Error)
            {
                Errors.Add((exception, formatter(state, exception)));
            }
            else if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }

    private static async Task WaitUntil(Func<bool> condition, string? because = null)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        try
        {
            while (!condition())
            {
                await Task.Delay(10, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (because is not null)
        {
            throw new InvalidOperationException($"Timed out waiting: {because}");
        }
    }

    /// <summary>Resolves a distinct front contract per symbol so two instruments are two subscriptions.</summary>
    private sealed class PerInstrumentGateway : IMarketDataGateway
    {
        public string VenueId => "test";

        public Task<IReadOnlyList<VenueContract>> ResolveContractsAsync(
            InstrumentId instrument,
            CancellationToken cancellationToken)
        {
            string contract = instrument.Symbol switch
            {
                "ES" => "CON.F.US.EP.Z26",
                "NQ" => "CON.F.US.ENQ.Z26",
                _ => "CON.F.US.TEST.Z26",
            };

            return Task.FromResult<IReadOnlyList<VenueContract>>(
                [new VenueContract(contract, instrument, true, 0.25m, 12.50m)]);
        }

        public Task<IReadOnlyList<Bar>> GetBarsAsync(
            string contractId,
            BarRange window,
            TimeSpan barSize,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Bar>>([]);

        public Task<IReadOnlyList<VenueAccount>> GetAccountsAsync(
            bool onlyActive,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VenueAccount>>([]);

        public Task<IReadOnlyList<VenuePosition>> GetOpenPositionsAsync(
            int accountId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VenuePosition>>([]);

        public Task<IReadOnlyList<VenueOrder>> GetOrdersAsync(
            int accountId,
            BarRange? window,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VenueOrder>>([]);

        public Task<IReadOnlyList<VenueTrade>> GetTradesAsync(
            int accountId,
            BarRange window,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VenueTrade>>([]);
    }

    /// <summary>Holds persist so the bounded channel can fill in a test.</summary>
    private sealed class HoldingInterceptor(TaskCompletionSource hold, TaskCompletionSource? started)
        : SaveChangesInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            bool holdingTrades = eventData.Context?.ChangeTracker.Entries<TradeRecord>()
                .Any(entry => entry.State == EntityState.Added) == true;
            if (!holdingTrades)
            {
                return await base.SavingChangesAsync(eventData, result, cancellationToken)
                    .ConfigureAwait(false);
            }

            started?.TrySetResult();
            await hold.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await base.SavingChangesAsync(eventData, result, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Refuses the first SaveChanges that retires a still-open TapeCoverage row.</summary>
    private sealed class FailingFirstRetireCoverageInterceptor : SaveChangesInterceptor
    {
        private int _refusals;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            bool retiringStillOpen = eventData.Context?.ChangeTracker.Entries<TapeCoverageRecord>()
                .Any(entry =>
                    entry.State == EntityState.Deleted
                    && entry.Entity.RangeEnd == TapeCoverageRecord.StillListeningEnd) == true;
            if (retiringStillOpen && Interlocked.Increment(ref _refusals) == 1)
            {
                throw new InvalidOperationException("the store refused the coverage retire");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    /// <summary>Refuses a SaveChanges that retires a still-open TapeCoverage row.</summary>
    private sealed class FailingRetireCoverageInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            bool retiringStillOpen = eventData.Context?.ChangeTracker.Entries<TapeCoverageRecord>()
                .Any(entry =>
                    entry.State == EntityState.Deleted
                    && entry.Entity.RangeEnd == TapeCoverageRecord.StillListeningEnd) == true;
            if (retiringStillOpen)
            {
                throw new InvalidOperationException("the store refused the coverage retire");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    /// <summary>Refuses a SaveChanges that writes a still-open TapeCoverage row.</summary>
    private sealed class FailingOpenCoverageInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            bool writingOpen = eventData.Context?.ChangeTracker.Entries<TapeCoverageRecord>()
                .Any(entry =>
                    entry.State == EntityState.Added
                    && entry.Entity.RangeEnd == TapeCoverageRecord.StillListeningEnd) == true;
            if (writingOpen)
            {
                throw new InvalidOperationException("the store refused the coverage open");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    /// <summary>
    /// Holds the first still-open TapeCoverage write so a print can queue, then refuses it.
    /// </summary>
    private sealed class FailingFirstHeldOpenCoverageInterceptor(
        TaskCompletionSource hold,
        TaskCompletionSource? started) : SaveChangesInterceptor
    {
        private int _refusals;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            bool writingOpen = eventData.Context?.ChangeTracker.Entries<TapeCoverageRecord>()
                .Any(entry =>
                    entry.State == EntityState.Added
                    && entry.Entity.RangeEnd == TapeCoverageRecord.StillListeningEnd) == true;
            if (writingOpen && Interlocked.Increment(ref _refusals) == 1)
            {
                started?.TrySetResult();
                await hold.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                throw new InvalidOperationException("the store refused the coverage open");
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Holds the first still-open TapeCoverage write so a hub drop can run mid-persist, then lets
    /// it land.
    /// </summary>
    private sealed class HeldFirstOpenCoverageInterceptor(
        TaskCompletionSource hold,
        TaskCompletionSource? started) : SaveChangesInterceptor
    {
        private int _holds;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            bool writingOpen = eventData.Context?.ChangeTracker.Entries<TapeCoverageRecord>()
                .Any(entry =>
                    entry.State == EntityState.Added
                    && entry.Entity.RangeEnd == TapeCoverageRecord.StillListeningEnd) == true;
            if (writingOpen && Interlocked.Increment(ref _holds) == 1)
            {
                started?.TrySetResult();
                await hold.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            return await base.SavingChangesAsync(eventData, result, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>Refuses the first SaveChanges that writes a still-open TapeCoverage row.</summary>
    private sealed class FailingFirstOpenCoverageInterceptor : SaveChangesInterceptor
    {
        private int _refusals;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            bool writingOpen = eventData.Context?.ChangeTracker.Entries<TapeCoverageRecord>()
                .Any(entry =>
                    entry.State == EntityState.Added
                    && entry.Entity.RangeEnd == TapeCoverageRecord.StillListeningEnd) == true;
            if (writingOpen && Interlocked.Increment(ref _refusals) == 1)
            {
                throw new InvalidOperationException("the store refused the coverage open");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    /// <summary>Refuses a SaveChanges that writes a closed TapeCoverage row.</summary>
    private sealed class FailingClosedCoverageInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            bool writingClosed = eventData.Context?.ChangeTracker.Entries<TapeCoverageRecord>()
                .Any(entry =>
                    entry.State == EntityState.Added
                    && entry.Entity.RangeEnd != TapeCoverageRecord.StillListeningEnd) == true;
            if (writingClosed)
            {
                throw new InvalidOperationException("the store refused the coverage close");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
