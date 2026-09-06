using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// The coverage ledger's own invariants, driven directly — no hub, no channel, no
/// <c>BackgroundService</c>. That these cases can be written at all is the point of extracting
/// <see cref="TapeCoverageLedger"/> out of <see cref="TradeTapeRecorder"/>: every one of them used
/// to need a live subscription lifecycle standing behind it (gh#390).
/// </summary>
public sealed class TapeCoverageLedgerTests
{
    private const string Venue = "test";
    private const string Instrument = "ES";
    private const string Contract = "CON.F.US.TEST.Z26";

    private static readonly DateTimeOffset _start =
        new(2026, 8, 28, 14, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task AFailedOpenPersist_DiscardsAQueuedCloseForThatListen_SoNoRangeIsInvented()
    {
        // Invariant 1. The claim precedes the store write, so a hub drop can snapshot a listen
        // whose row never lands. That snapshot is a hole, not a range: writing it would claim
        // coverage for a window nothing was recorded under (gh#376).
        FakeTimeProvider clock = new(_start);
        (TapeCoverageLedger ledger, TopstepXDbContext database, ServiceProvider services) =
            Build(clock, new RefusingOpenInterceptor());

        await using (services)
        await using (database)
        {
            ledger.ClaimOpenRange(Contract, Venue, Instrument, _start);
            ledger.CloseOpenRangesAt(_start.AddMinutes(1));

            Func<Task> persist = () => ledger.PersistOpenRangeAsync(
                Venue, Instrument, Contract, _start, CancellationToken.None);

            await persist.Should().ThrowAsync<InvalidOperationException>(
                "a store that refuses the open must not be reported as a successful listen");

            await ledger.PersistPendingClosesAsync(CancellationToken.None);

            CoverageRows(database).Should().BeEmpty(
                "the close queued for a listen whose open never reached the store was discarded "
                + "with it, so nothing claims that window");
        }
    }

    [Fact]
    public async Task AFailedOpenPersist_LeavesNoClaim_SoALaterCloseOfEverythingWritesNothing()
    {
        // Invariant 1, the other half: the open set is taken back too, so the next
        // close-everything sweep does not resurrect the listen the store refused.
        FakeTimeProvider clock = new(_start);
        (TapeCoverageLedger ledger, TopstepXDbContext database, ServiceProvider services) =
            Build(clock, new RefusingOpenInterceptor());

        await using (services)
        await using (database)
        {
            ledger.ClaimOpenRange(Contract, Venue, Instrument, _start);

            Func<Task> persist = () => ledger.PersistOpenRangeAsync(
                Venue, Instrument, Contract, _start, CancellationToken.None);
            await persist.Should().ThrowAsync<InvalidOperationException>();

            ledger.CloseOpenRangesAt(_start.AddMinutes(5));
            await ledger.PersistPendingClosesAsync(CancellationToken.None);

            CoverageRows(database).Should().BeEmpty();
        }
    }

    [Fact]
    public async Task AQueuedClose_RetiresTheRowItsOwnRangeOpened_AndLeavesALaterListenStanding()
    {
        // Invariant 3. The retire predicate carries RangeStart, so a close flushed after the next
        // listen has already opened cannot delete that listen's still-open row (gh#377).
        FakeTimeProvider clock = new(_start);
        (TapeCoverageLedger ledger, TopstepXDbContext database, ServiceProvider services) =
            Build(clock);

        DateTimeOffset firstEnd = _start.AddMinutes(1);
        DateTimeOffset secondStart = _start.AddMinutes(2);

        await using (services)
        await using (database)
        {
            ledger.ClaimOpenRange(Contract, Venue, Instrument, _start);
            await ledger.PersistOpenRangeAsync(
                Venue, Instrument, Contract, _start, CancellationToken.None);

            // The hub drops: the listen is queued for closing but not yet written.
            ledger.CloseOpenRangesAt(firstEnd);

            // It reconnects and opens a new range before that close is flushed.
            ledger.ClaimOpenRange(Contract, Venue, Instrument, secondStart);
            await ledger.PersistOpenRangeAsync(
                Venue, Instrument, Contract, secondStart, CancellationToken.None);

            await ledger.PersistPendingClosesAsync(CancellationToken.None);

            List<TapeCoverageRecord> rows = CoverageRows(database);
            rows.Should().HaveCount(2);
            rows.Should().ContainSingle(row =>
                row.RangeStart == _start && row.RangeEnd == firstEnd,
                "the first listen closes at its own exclusive end");
            rows.Should().ContainSingle(row =>
                row.RangeStart == secondStart
                && row.RangeEnd == TapeCoverageRecord.StillListeningEnd,
                "the second listen is still open and a close for the first must not retire it");
        }
    }

    [Fact]
    public async Task AZeroLengthClose_RetiresTheOpenRow_AndWritesNoRange()
    {
        // Invariant 4. An empty range is not coverage: storing [t, t) would claim a window in
        // which nothing was listening, and it must not be left open either.
        FakeTimeProvider clock = new(_start);
        (TapeCoverageLedger ledger, TopstepXDbContext database, ServiceProvider services) =
            Build(clock);

        await using (services)
        await using (database)
        {
            ledger.ClaimOpenRange(Contract, Venue, Instrument, _start);
            await ledger.PersistOpenRangeAsync(
                Venue, Instrument, Contract, _start, CancellationToken.None);

            CoverageRows(database).Should().ContainSingle(row =>
                row.RangeEnd == TapeCoverageRecord.StillListeningEnd);

            ledger.CloseOpenRangesAt(_start);
            await ledger.PersistPendingClosesAsync(CancellationToken.None);

            CoverageRows(database).Should().BeEmpty(
                "the still-open row is retired, and a zero-length range is not written in its place");
        }
    }

    [Fact]
    public async Task APrint_IsSuppressedFromTheSubscribeAttempt_UntilThatAttemptsOpenLands()
    {
        // Invariant 2. The boundary is taken before the subscribe RPC, so a print the venue
        // emits while the RPC is in flight is only stored once this listen's row exists (gh#376).
        FakeTimeProvider clock = new(_start);
        (TapeCoverageLedger ledger, TopstepXDbContext database, ServiceProvider services) =
            Build(clock);

        DateTimeOffset attempt = _start;
        DateTimeOffset confirmed = _start.AddSeconds(1);

        await using (services)
        await using (database)
        {
            ledger.ShouldSuppressPrint(Contract, attempt.AddSeconds(-1))
                .Should().BeFalse("nothing has been attempted, so nothing is suppressed");

            ledger.RememberSubscribeAttempt(Contract, attempt);
            ledger.ShouldSuppressPrint(Contract, attempt)
                .Should().BeTrue("the open has not reached the store yet");

            ledger.ClaimOpenRange(Contract, Venue, Instrument, confirmed);
            await ledger.PersistOpenRangeAsync(
                Venue, Instrument, Contract, confirmed, CancellationToken.None);

            ledger.ShouldSuppressPrint(Contract, attempt.AddMilliseconds(500))
                .Should().BeTrue("a print from before the range opened is still uncovered");
            ledger.ShouldSuppressPrint(Contract, confirmed)
                .Should().BeFalse("the range that covers this print is in the store");
        }
    }

    [Fact]
    public async Task AFailedOpen_KeepsTheEarlierBoundary_SoItsPrintsStaySuppressedAcrossARetry()
    {
        // Invariant 2's awkward half: an attempt whose open never landed stays the boundary.
        // Moving it forward on the retry would let that attempt's uncovered prints through as
        // though they belonged to a previous listen.
        FakeTimeProvider clock = new(_start);
        (TapeCoverageLedger ledger, TopstepXDbContext database, ServiceProvider services) =
            Build(clock, new RefusingOpenInterceptor());

        DateTimeOffset first = _start;
        DateTimeOffset second = _start.AddMinutes(1);

        await using (services)
        await using (database)
        {
            ledger.RememberSubscribeAttempt(Contract, first);
            ledger.ClaimOpenRange(Contract, Venue, Instrument, first);
            Func<Task> persist = () => ledger.PersistOpenRangeAsync(
                Venue, Instrument, Contract, first, CancellationToken.None);
            await persist.Should().ThrowAsync<InvalidOperationException>();

            ledger.RememberSubscribeAttempt(Contract, second);

            ledger.ShouldSuppressPrint(Contract, first)
                .Should().BeTrue("the failed attempt's prints are uncovered whatever happens next");
        }
    }

    [Fact]
    public async Task ALeftoverOnARolledAwayContract_IsDiscarded_ForAnInstrumentTheStartRecords()
    {
        // The discard is scoped by (Venue, Instrument), not by the front contract. Keyed on
        // ContractId too, a leftover written before a roll survives every later start — and the
        // Listening guard is per instrument, so it would read as coverage to 9999 on a contract
        // nothing is subscribed to (gh#382).
        FakeTimeProvider clock = new(_start);
        (TapeCoverageLedger ledger, TopstepXDbContext database, ServiceProvider services) =
            Build(clock);

        await using (services)
        await using (database)
        {
            await SeedStillListeningAsync(database, Instrument, "CON.F.US.TEST.U26");

            await ledger.DiscardAbandonedOpenRangesAsync(Venue, [Instrument], CancellationToken.None);

            CoverageRows(database).Should().BeEmpty(
                "a leftover written before a roll is still this process's own");
        }
    }

    [Fact]
    public async Task AnOpenRowForAnInstrumentTheStartDoesNotRecord_IsLeftStanding()
    {
        // The other half of gh#382: another recorder may still own that row, and the range it
        // holds cannot be rebuilt — there is no market-tape REST backfill.
        FakeTimeProvider clock = new(_start);
        (TapeCoverageLedger ledger, TopstepXDbContext database, ServiceProvider services) =
            Build(clock);

        await using (services)
        await using (database)
        {
            await SeedStillListeningAsync(database, "NQ", "CON.F.US.TESTNQ.Z26");

            await ledger.DiscardAbandonedOpenRangesAsync(Venue, [Instrument], CancellationToken.None);

            CoverageRows(database).Should().ContainSingle(row => row.Instrument == "NQ");
        }
    }

    [Fact]
    public async Task AStartThatResolvedNoContract_DiscardsNothing()
    {
        FakeTimeProvider clock = new(_start);
        (TapeCoverageLedger ledger, TopstepXDbContext database, ServiceProvider services) =
            Build(clock);

        await using (services)
        await using (database)
        {
            await SeedStillListeningAsync(database, Instrument, Contract);

            await ledger.DiscardAbandonedOpenRangesAsync(Venue, [], CancellationToken.None);

            CoverageRows(database).Should().ContainSingle();
        }
    }

    [Fact]
    public async Task AFailedClosePersist_PutsTheBatchBack_SoTheCloseIsRetriedRatherThanLost()
    {
        FakeTimeProvider clock = new(_start);
        RefusingClosedInterceptor refusing = new();
        (TapeCoverageLedger ledger, TopstepXDbContext database, ServiceProvider services) =
            Build(clock, refusing);

        DateTimeOffset end = _start.AddMinutes(1);

        await using (services)
        await using (database)
        {
            ledger.ClaimOpenRange(Contract, Venue, Instrument, _start);
            await ledger.PersistOpenRangeAsync(
                Venue, Instrument, Contract, _start, CancellationToken.None);

            ledger.CloseOpenRangesAt(end);

            Func<Task> flush = () => ledger.PersistPendingClosesAsync(CancellationToken.None);
            await flush.Should().ThrowAsync<InvalidOperationException>();

            refusing.Refuse = false;
            await ledger.PersistPendingClosesAsync(CancellationToken.None);

            CoverageRows(database).Should().ContainSingle(row =>
                row.RangeStart == _start && row.RangeEnd == end,
                "a close the store refused is retried, not dropped");
        }
    }

    [Fact]
    public async Task TheStoreLease_SerialisesAWriterOutsideTheLedger_AgainstACoverageWrite()
    {
        // The print pipeline lives in TradeTapeRecorder but shares this gate: the in-memory
        // provider is not safe for concurrent SaveChanges.
        FakeTimeProvider clock = new(_start);
        (TapeCoverageLedger ledger, TopstepXDbContext database, ServiceProvider services) =
            Build(clock);

        await using (services)
        await using (database)
        {
            Task blocked;
            using (await ledger.EnterStoreAsync(CancellationToken.None))
            {
                blocked = ledger.PersistOpenRangeAsync(
                    Venue, Instrument, Contract, _start, CancellationToken.None);

                await Task.Delay(50);
                blocked.IsCompleted.Should().BeFalse("the lease holds the store gate");
            }

            await blocked;

            CoverageRows(database).Should().ContainSingle(row =>
                row.RangeEnd == TapeCoverageRecord.StillListeningEnd);
        }
    }

    private static List<TapeCoverageRecord> CoverageRows(TopstepXDbContext database)
    {
        database.ChangeTracker.Clear();
        return [.. database.TapeCoverage.OrderBy(row => row.RangeStart)];
    }

    private static async Task SeedStillListeningAsync(
        TopstepXDbContext database,
        string instrument,
        string contractId)
    {
        database.TapeCoverage.Add(new TapeCoverageRecord
        {
            Venue = Venue,
            Instrument = instrument,
            ContractId = contractId,
            RangeStart = _start.AddDays(-1),
            RangeEnd = TapeCoverageRecord.StillListeningEnd,
            RecordedAt = _start.AddDays(-1),
        });
        await database.SaveChangesAsync();
    }

    private static (TapeCoverageLedger Ledger, TopstepXDbContext Database, ServiceProvider Services)
        Build(FakeTimeProvider clock, SaveChangesInterceptor? interceptor = null)
    {
        DbContextOptionsBuilder<TopstepXDbContext> builder = new DbContextOptionsBuilder<TopstepXDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));
        if (interceptor is not null)
        {
            builder.AddInterceptors(interceptor);
        }

        DbContextOptions<TopstepXDbContext> options = builder.Options;

        ServiceCollection services = new();
        services.AddScoped(_ => new TopstepXDbContext(options));
        ServiceProvider provider = services.BuildServiceProvider();

        TapeCoverageLedger ledger = new(
            provider.GetRequiredService<IServiceScopeFactory>(), clock);

        return (ledger, new TopstepXDbContext(options), provider);
    }

    /// <summary>Refuses a SaveChanges that writes a still-open TapeCoverage row.</summary>
    private sealed class RefusingOpenInterceptor : SaveChangesInterceptor
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

    /// <summary>Refuses a SaveChanges that writes a closed TapeCoverage row, until told not to.</summary>
    private sealed class RefusingClosedInterceptor : SaveChangesInterceptor
    {
        public bool Refuse { get; set; } = true;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            bool writingClosed = eventData.Context?.ChangeTracker.Entries<TapeCoverageRecord>()
                .Any(entry =>
                    entry.State == EntityState.Added
                    && entry.Entity.RangeEnd != TapeCoverageRecord.StillListeningEnd) == true;
            if (Refuse && writingClosed)
            {
                throw new InvalidOperationException("the store refused the coverage close");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
