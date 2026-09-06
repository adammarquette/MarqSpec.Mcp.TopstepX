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
/// The tape claim's own rules, driven directly — no hub, no channel, no <c>BackgroundService</c>.
/// Two processes are two <see cref="TapeLease"/> instances over one store, which is exactly what
/// two recorders are (gh#404).
/// </summary>
public sealed class TapeLeaseTests
{
    private const string Venue = "test";
    private const string Instrument = "ES";

    private static readonly DateTimeOffset _start =
        new(2026, 8, 30, 14, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task AFreeInstrument_IsGranted_AndTheRowRecordsThisProcessAsTheHolder()
    {
        FakeTimeProvider clock = new(_start);
        (TapeLease lease, TopstepXDbContext database, ServiceProvider services) = Build(clock);

        await using (services)
        await using (database)
        {
            TapeLeaseOutcome outcome = await lease.TryAcquireAsync(
                Venue, Instrument, CancellationToken.None);

            outcome.IsGranted.Should().BeTrue("nothing else holds this instrument");
            lease.Held.Should().ContainSingle(held => held.Instrument == Instrument);

            TapeLeaseRecord row = Rows(database).Should().ContainSingle().Subject;
            row.OwnerId.Should().Be(lease.OwnerId);
            row.Generation.Should().Be(1);
            row.ExpiresAt.Should().Be(_start + lease.TimeToLive,
                "the expiry a later start reads is written, not inferred");
        }
    }

    [Fact]
    public async Task ASecondProcessOnTheSameInstrument_IsRefused_NamingTheHolderAndItsExpiry()
    {
        // The whole point. Two subscribers on one tape double every volume and a doubled delta
        // reads as order flow rather than as a bug (ADR-0016). ADR-0016 said so; nothing enforced
        // it, and gh#382 only made the collision survivable.
        FakeTimeProvider clock = new(_start);
        (TapeLease first, TopstepXDbContext database, ServiceProvider services) = Build(clock);
        TapeLease second = Second(services, clock);

        await using (services)
        await using (database)
        {
            (await first.TryAcquireAsync(Venue, Instrument, CancellationToken.None))
                .IsGranted.Should().BeTrue();

            TapeLeaseOutcome refused = await second.TryAcquireAsync(
                Venue, Instrument, CancellationToken.None);

            refused.IsGranted.Should().BeFalse();
            refused.IsUnreadable.Should().BeFalse("the store answered; the claim is simply taken");
            refused.HolderId.Should().Be(first.OwnerId, "a refusal has to name who to go and stop");
            refused.HolderExpiresAt.Should().Be(_start + first.TimeToLive);
            second.Held.Should().BeEmpty();
            Rows(database).Should().ContainSingle(row => row.OwnerId == first.OwnerId);
        }
    }

    [Fact]
    public async Task AQuietHolderWhoseClaimHasNotLapsed_IsStillTheHolder()
    {
        // A missing number is missing, never a default: a holder that has gone quiet is not
        // "probably free". Only an expiry that has actually passed frees the claim, and the
        // stored expiry is what says so.
        FakeTimeProvider clock = new(_start);
        (TapeLease first, TopstepXDbContext database, ServiceProvider services) = Build(clock);
        TapeLease second = Second(services, clock);

        await using (services)
        await using (database)
        {
            await first.TryAcquireAsync(Venue, Instrument, CancellationToken.None);

            // One tick short of the expiry, and the holder has not heartbeat since.
            clock.Advance(first.TimeToLive - TimeSpan.FromTicks(1));

            (await second.TryAcquireAsync(Venue, Instrument, CancellationToken.None))
                .IsGranted.Should().BeFalse("the claim has not lapsed yet, however quiet its holder is");
        }
    }

    [Fact]
    public async Task AClaimWhoseHolderIsGone_IsTakenOverOnTheNextStart_SoACrashDoesNotStrandTheTape()
    {
        FakeTimeProvider clock = new(_start);
        (TapeLease crashed, TopstepXDbContext database, ServiceProvider services) = Build(clock);
        TapeLease next = Second(services, clock);

        await using (services)
        await using (database)
        {
            await crashed.TryAcquireAsync(Venue, Instrument, CancellationToken.None);

            clock.Advance(crashed.TimeToLive);

            TapeLeaseOutcome taken = await next.TryAcquireAsync(
                Venue, Instrument, CancellationToken.None);

            taken.IsGranted.Should().BeTrue("an expired claim is reclaimable, or a crash locks the tape forever");

            TapeLeaseRecord row = Rows(database).Should().ContainSingle().Subject;
            row.OwnerId.Should().Be(next.OwnerId);
            row.Generation.Should().Be(2, "the generation is what makes the reclaim itself conditional");
        }
    }

    [Fact]
    public async Task TwoStartsReclaimingOneExpiredClaim_LeaveOneHolder_NotTwo()
    {
        // The reclaim path is the one place a claim could itself produce the double writer it
        // exists to refuse. Generation is a concurrency token, so of two takeovers racing the
        // same row exactly one update matches; the loser re-reads and is refused.
        FakeTimeProvider clock = new(_start);
        string databaseName = Guid.NewGuid().ToString();

        ServiceProvider plain = Provider(databaseName);
        await using (plain)
        {
            TapeLease crashed = Lease(plain, clock);
            await crashed.TryAcquireAsync(Venue, Instrument, CancellationToken.None);
            clock.Advance(crashed.TimeToLive);

            TapeLease winner = Lease(plain, clock);

            // The loser's own save is interrupted: the winner's takeover lands first, against the
            // generation the loser already read.
            ServiceProvider racing = Provider(
                databaseName,
                new TakeoverRaceInterceptor(() =>
                    winner.TryAcquireAsync(Venue, Instrument, CancellationToken.None)
                        .GetAwaiter()
                        .GetResult()));

            await using (racing)
            {
                TapeLease loser = Lease(racing, clock);

                TapeLeaseOutcome outcome = await loser.TryAcquireAsync(
                    Venue, Instrument, CancellationToken.None);

                outcome.IsGranted.Should().BeFalse("the winner's takeover matched the generation first");
                outcome.HolderId.Should().Be(winner.OwnerId);
                loser.Held.Should().BeEmpty();

                await using TopstepXDbContext database = Context(databaseName);
                Rows(database).Should().ContainSingle(row => row.OwnerId == winner.OwnerId);
            }
        }
    }

    [Fact]
    public async Task AHolderWhoseClaimWasTakenOver_LosesItsRenewal_SoItStandsDownRatherThanWritingTwice()
    {
        // A holder merely paused past its expiry — a stall, a store outage — can be taken over
        // while it is still subscribed. The renewal is where it finds out, and finding out is
        // what keeps a reclaim from producing two writers on one tape.
        FakeTimeProvider clock = new(_start);
        (TapeLease paused, TopstepXDbContext database, ServiceProvider services) = Build(clock);
        TapeLease took = Second(services, clock);

        await using (services)
        await using (database)
        {
            await paused.TryAcquireAsync(Venue, Instrument, CancellationToken.None);
            clock.Advance(paused.TimeToLive);
            await took.TryAcquireAsync(Venue, Instrument, CancellationToken.None);

            TapeLeaseRenewal lost = await paused.TryRenewAsync(
                Venue, Instrument, CancellationToken.None);

            lost.Kept.Should().BeFalse("the row names someone else now");
            lost.ReclaimedAt.Should().Be(_start + paused.TimeToLive,
                "the loser closes its coverage range at the handover, not at the moment it "
                + "noticed — a range ending at notice claims a window the replacement claims too");
            paused.Held.Should().BeEmpty();

            Rows(database).Should().ContainSingle(row => row.OwnerId == took.OwnerId,
                "the evicted holder must not write its own expiry back over the new holder's");
        }
    }

    [Fact]
    public async Task ARenewal_PushesTheExpiryOut_SoASlowSecondStartStillFindsItHeld()
    {
        FakeTimeProvider clock = new(_start);
        (TapeLease holder, TopstepXDbContext database, ServiceProvider services) = Build(clock);
        TapeLease second = Second(services, clock);

        await using (services)
        await using (database)
        {
            await holder.TryAcquireAsync(Venue, Instrument, CancellationToken.None);

            clock.Advance(holder.RenewInterval);
            (await holder.TryRenewAsync(Venue, Instrument, CancellationToken.None))
                .Kept.Should().BeTrue();

            clock.Advance(holder.TimeToLive - holder.RenewInterval);

            (await second.TryAcquireAsync(Venue, Instrument, CancellationToken.None))
                .IsGranted.Should().BeFalse("the heartbeat moved the expiry, which is the point of one");
        }
    }

    [Fact]
    public async Task ACleanRelease_FreesTheClaim_WithoutTheNextStartWaitingOutTheTimeToLive()
    {
        FakeTimeProvider clock = new(_start);
        (TapeLease stopping, TopstepXDbContext database, ServiceProvider services) = Build(clock);
        TapeLease next = Second(services, clock);

        await using (services)
        await using (database)
        {
            await stopping.TryAcquireAsync(Venue, Instrument, CancellationToken.None);
            await stopping.ReleaseAllAsync(CancellationToken.None);

            Rows(database).Should().BeEmpty("the absence of a row is the free state");
            stopping.Held.Should().BeEmpty();

            (await next.TryAcquireAsync(Venue, Instrument, CancellationToken.None))
                .IsGranted.Should().BeTrue("a redeploy must not have to wait out a claim nobody holds");
        }
    }

    [Fact]
    public async Task AnEvictedHoldersRelease_LeavesTheNewHoldersClaimStanding()
    {
        // The evicted process shutting down must not delete the row a live recorder now owns, or
        // a third start would be granted a tape that is already being recorded.
        FakeTimeProvider clock = new(_start);
        (TapeLease evicted, TopstepXDbContext database, ServiceProvider services) = Build(clock);
        TapeLease took = Second(services, clock);

        await using (services)
        await using (database)
        {
            await evicted.TryAcquireAsync(Venue, Instrument, CancellationToken.None);
            clock.Advance(evicted.TimeToLive);
            await took.TryAcquireAsync(Venue, Instrument, CancellationToken.None);

            await evicted.ReleaseAllAsync(CancellationToken.None);

            Rows(database).Should().ContainSingle(row => row.OwnerId == took.OwnerId);
        }
    }

    [Fact]
    public async Task TheSplitByInstrumentDeployment_GrantsBothProcesses_BecauseTheClaimIsPerInstrument()
    {
        // Two recorders partitioned by MarketData__Instruments are a supported deployment that
        // gh#382 exists to protect. A whole-store claim would outlaw it; this one refuses only
        // the overlap that doubles volume.
        FakeTimeProvider clock = new(_start);
        (TapeLease es, TopstepXDbContext database, ServiceProvider services) = Build(clock);
        TapeLease nq = Second(services, clock);

        await using (services)
        await using (database)
        {
            (await es.TryAcquireAsync(Venue, "ES", CancellationToken.None))
                .IsGranted.Should().BeTrue();
            (await nq.TryAcquireAsync(Venue, "NQ", CancellationToken.None))
                .IsGranted.Should().BeTrue("a split deployment is legal, not the thing being refused");

            Rows(database).Should().HaveCount(2);
        }
    }

    [Fact]
    public async Task TheSameInstrumentAtTwoVenues_IsTwoClaims()
    {
        FakeTimeProvider clock = new(_start);
        (TapeLease one, TopstepXDbContext database, ServiceProvider services) = Build(clock);
        TapeLease other = Second(services, clock);

        await using (services)
        await using (database)
        {
            (await one.TryAcquireAsync("venue-a", Instrument, CancellationToken.None))
                .IsGranted.Should().BeTrue();
            (await other.TryAcquireAsync("venue-b", Instrument, CancellationToken.None))
                .IsGranted.Should().BeTrue("the same product on two venues is two series");
        }
    }

    [Fact]
    public async Task AReacquireByTheSameProcess_IsGranted_AndDoesNotRefuseItself()
    {
        FakeTimeProvider clock = new(_start);
        (TapeLease lease, TopstepXDbContext database, ServiceProvider services) = Build(clock);

        await using (services)
        await using (database)
        {
            await lease.TryAcquireAsync(Venue, Instrument, CancellationToken.None);

            clock.Advance(lease.TimeToLive * 2);

            (await lease.TryAcquireAsync(Venue, Instrument, CancellationToken.None))
                .IsGranted.Should().BeTrue("a process is never refused its own claim");
            Rows(database).Should().ContainSingle();
        }
    }

    [Fact]
    public void TheRenewInterval_IsAThirdOfTheTimeToLive_SoTwoLostRenewalsAreSurvivable()
    {
        FakeTimeProvider clock = new(_start);
        (TapeLease lease, TopstepXDbContext database, ServiceProvider services) = Build(clock);

        using (services)
        using (database)
        {
            lease.RenewInterval.Should().Be(lease.TimeToLive / 3);
        }
    }

    [Fact]
    public async Task AHolderPastItsOwnTerm_MayNotWriteAPrint_SoATakeoverCannotDoubleTheTape()
    {
        // The fence, and the reason the two-writer window is empty rather than merely short.
        // A renewal only reports a lost claim at the next tick, so a holder that waited to be
        // told would keep writing for up to one RenewInterval after a legitimate takeover — and
        // Sequence is a per-process counter, so the same print written by both processes takes a
        // different key in each and lands twice instead of collapsing. A footprint then reports
        // doubled volume and a doubled delta as an ordinary answer, which is precisely ADR-0016's
        // failure mode. So the holder does not wait: its own expiry is the last instant it writes,
        // and that is the earliest instant anyone else can hold the claim.
        FakeTimeProvider clock = new(_start);
        (TapeLease lease, TopstepXDbContext database, ServiceProvider services) = Build(clock);

        await using (services)
        await using (database)
        {
            await lease.TryAcquireAsync(Venue, Instrument, CancellationToken.None);

            lease.MayWrite(Venue, Instrument, _start).Should().BeTrue();
            lease.MayWrite(Venue, Instrument, _start + lease.TimeToLive - TimeSpan.FromTicks(1))
                .Should().BeTrue("the term is this process's to write in");

            lease.MayWrite(Venue, Instrument, _start + lease.TimeToLive)
                .Should().BeFalse("the expiry is exclusive: it is when someone else may take over");
            lease.MayWrite(Venue, Instrument, _start + lease.TimeToLive + TimeSpan.FromMinutes(5))
                .Should().BeFalse();
        }
    }

    [Fact]
    public async Task TheFence_AsksWhenThePrintArrived_NotWhenTheStoreGotRoundToIt()
    {
        // A slow drain must not throw away a print this process genuinely held the claim for.
        // The question is whether the claim covered the print, not whether it covers now.
        FakeTimeProvider clock = new(_start);
        (TapeLease lease, TopstepXDbContext database, ServiceProvider services) = Build(clock);

        await using (services)
        await using (database)
        {
            await lease.TryAcquireAsync(Venue, Instrument, CancellationToken.None);
            DateTimeOffset arrived = _start + TimeSpan.FromSeconds(1);

            clock.Advance(lease.TimeToLive * 2);

            lease.MayWrite(Venue, Instrument, arrived).Should().BeTrue(
                "the print arrived well inside the term, however late the drain reached it");
            lease.MayWrite(Venue, Instrument, clock.GetUtcNow()).Should().BeFalse();
        }
    }

    [Fact]
    public async Task ARenewal_MovesTheFence_SoAHolderThatKeepsItsClaimKeepsWriting()
    {
        FakeTimeProvider clock = new(_start);
        (TapeLease lease, TopstepXDbContext database, ServiceProvider services) = Build(clock);

        await using (services)
        await using (database)
        {
            await lease.TryAcquireAsync(Venue, Instrument, CancellationToken.None);

            clock.Advance(lease.RenewInterval);
            await lease.TryRenewAsync(Venue, Instrument, CancellationToken.None);

            lease.MayWrite(Venue, Instrument, _start + lease.TimeToLive).Should().BeTrue(
                "the renewal pushed the term out past the original expiry");
        }
    }

    [Fact]
    public async Task AForfeitedClaim_StopsWritingImmediately_WithoutTouchingTheStore()
    {
        // The store is the thing that is not answering, so the row is left exactly as it is: it
        // may already belong to a replacement, and deleting it would hand the tape to a third
        // process. What must change at once is this process's own behaviour.
        FakeTimeProvider clock = new(_start);
        (TapeLease lease, TopstepXDbContext database, ServiceProvider services) = Build(clock);

        await using (services)
        await using (database)
        {
            await lease.TryAcquireAsync(Venue, Instrument, CancellationToken.None);

            lease.Forfeit(Venue, Instrument);

            lease.MayWrite(Venue, Instrument, _start).Should().BeFalse();
            lease.Held.Should().BeEmpty();
            Rows(database).Should().ContainSingle(row => row.OwnerId == lease.OwnerId,
                "forfeiting is about this process, and says nothing about who owns the row");
        }
    }

    [Fact]
    public async Task AnUnheldInstrument_MayNotBeWritten_SoAnUnclaimedTapeIsNeverRecorded()
    {
        FakeTimeProvider clock = new(_start);
        (TapeLease lease, TopstepXDbContext database, ServiceProvider services) = Build(clock);

        await using (services)
        await using (database)
        {
            await lease.TryAcquireAsync(Venue, "ES", CancellationToken.None);

            lease.MayWrite(Venue, "NQ", _start).Should().BeFalse(
                "holding one instrument is not a licence to record another");
        }
    }

    private static List<TapeLeaseRecord> Rows(TopstepXDbContext database)
    {
        database.ChangeTracker.Clear();
        return [.. database.TapeLeases.OrderBy(row => row.Instrument)];
    }

    private static (TapeLease Lease, TopstepXDbContext Database, ServiceProvider Services)
        Build(FakeTimeProvider clock)
    {
        string databaseName = Guid.NewGuid().ToString();
        ServiceProvider provider = Provider(databaseName);
        return (Lease(provider, clock), Context(databaseName), provider);
    }

    private static TapeLease Second(ServiceProvider provider, FakeTimeProvider clock) =>
        Lease(provider, clock);

    private static TapeLease Lease(ServiceProvider provider, FakeTimeProvider clock) =>
        new(provider.GetRequiredService<IServiceScopeFactory>(), clock, TimeSpan.FromSeconds(90));

    private static TopstepXDbContext Context(string databaseName) => new(Options(databaseName));

    private static DbContextOptions<TopstepXDbContext> Options(
        string databaseName,
        SaveChangesInterceptor? interceptor = null)
    {
        DbContextOptionsBuilder<TopstepXDbContext> builder = new DbContextOptionsBuilder<TopstepXDbContext>()
            .UseInMemoryDatabase(databaseName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning));
        if (interceptor is not null)
        {
            builder.AddInterceptors(interceptor);
        }

        return builder.Options;
    }

    private static ServiceProvider Provider(
        string databaseName,
        SaveChangesInterceptor? interceptor = null)
    {
        DbContextOptions<TopstepXDbContext> options = Options(databaseName, interceptor);
        ServiceCollection services = new();
        services.AddScoped(_ => new TopstepXDbContext(options));
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Lets another process's takeover land between this one's read and its write, so the
    /// generation this context holds is already stale when it saves.
    /// </summary>
    private sealed class TakeoverRaceInterceptor(Action takeover) : SaveChangesInterceptor
    {
        private int _races;

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            bool updatingClaim = eventData.Context?.ChangeTracker.Entries<TapeLeaseRecord>()
                .Any(entry => entry.State == EntityState.Modified) == true;
            if (updatingClaim && Interlocked.Increment(ref _races) == 1)
            {
                takeover();
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
}
