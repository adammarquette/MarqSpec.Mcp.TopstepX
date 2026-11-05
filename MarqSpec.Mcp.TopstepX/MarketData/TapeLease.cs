using System.Collections.Concurrent;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// This process's claim on the instruments it records: acquire before subscribing, renew while
/// recording, release on a clean stop. Two subscribers on one tape double every volume and a
/// doubled delta looks like order flow, so ADR-0016's rule stops being prose here (gh#404).
/// </summary>
/// <remarks>
/// <para>
/// <b>Store-backed, because the store is the only thing two processes share.</b> A Postgres
/// advisory lock would work in the deployment and not in the unit suite, whose in-memory provider
/// has no equivalent; a claim nothing can test is a claim nothing defends.
/// </para>
/// <para>
/// <b>Per <c>(Venue, Instrument)</c>.</b> An operator running two recorders partitioned by
/// <c>MarketData__Instruments</c> is a supported deployment that gh#382 exists to protect, and a
/// whole-store claim would outlaw it. What is refused is only the overlap that doubles volume:
/// the same instrument in two processes.
/// </para>
/// <para>
/// <b>Expiry is read, never assumed.</b> A crashed holder must not strand the tape, so a row
/// carries an expiry its holder renews at <see cref="RenewInterval"/>. A row whose
/// <see cref="TapeLeaseRecord.ExpiresAt"/> has not passed is <em>held</em> — not "probably free
/// because the holder went quiet". The absence of a row is the only free state, and a store that
/// cannot be read yields <see cref="TapeLeaseOutcome.Unreadable"/> rather than a grant.
/// </para>
/// <para>
/// <b>The takeover is a conditional update, not a hopeful one.</b>
/// <see cref="TapeLeaseRecord.Generation"/> is a concurrency token, so two starts that both see
/// one expired row race the same generation: exactly one update matches, and the loser re-reads
/// and is refused.
/// </para>
/// <para>
/// <b><see cref="MayWrite"/> is the fence, and it is what makes a takeover safe.</b> A renewal
/// only tells a holder it has been replaced at the next tick, so "stand down when you notice"
/// leaves both processes writing for up to one <see cref="RenewInterval"/>. That window is not
/// harmless: <c>Sequence</c> is a per-process counter, so the same print written twice takes a
/// different key in each process and lands as two rows, which a footprint then reports as ordinary
/// doubled volume — ADR-0016's exact failure, arriving through the mechanism written to stop it.
/// So a holder does not wait to be told. It refuses to store a print received at or after
/// <em>its own</em> claim's expiry, which is the earliest instant anyone else could have taken
/// over. Against one clock the overlap is therefore not bounded but empty.
/// </para>
/// <para>
/// <b>What the fence does not close: clock skew between hosts.</b> Both processes compare their
/// own clock to one stored expiry, so a taker whose clock runs more than
/// <see cref="TimeToLive"/> ahead of the holder's can acquire while the holder still believes it
/// is inside its term. No local mechanism fixes that — it needs one clock — so it is stated rather
/// than claimed away, here and in ADR-0016. The generation check still leaves exactly one
/// <em>owner</em>; what skew can produce is a second <em>writer</em>, and those are not the same
/// property. Run the recorder on one host, or keep hosts synchronised.
/// </para>
/// <para>
/// <b>The lock here is a leaf.</b> This type never takes <see cref="TapeCoverageLedger"/>'s store
/// gate or its bookkeeping lock, and the ledger never calls this. There is therefore no path that
/// takes two of the three in either order, and the ledger's uniform store-then-bookkeeping
/// acquisition is untouched. <see cref="MayWrite"/> and <see cref="Held"/> read a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/> and take no lock at all, because the print path
/// calls the first of them under the ledger's store gate.
/// </para>
/// <para>
/// <b>No captive dependency.</b> Every store operation opens its own scope through
/// <see cref="IServiceScopeFactory"/>; <c>TopstepXDbContext</c> is scoped and this is held by a
/// singleton.
/// </para>
/// </remarks>
public sealed class TapeLease
{
    /// <summary>
    /// How long a claim stands unrenewed before another start may take it over.
    /// </summary>
    /// <remarks>
    /// Fixed rather than configured, and short on purpose. It is the worst-case tape gap after an
    /// uncontrolled crash: the dead process left no release, so the next start is refused until
    /// this elapses and only then acquires on its retry tick. A longer value strands the tape for
    /// longer; a shorter one evicts a merely slow holder more often, and every eviction costs the
    /// coverage range it closes.
    /// </remarks>
    public static readonly TimeSpan DefaultTimeToLive = TimeSpan.FromSeconds(90);

    /// <summary>Stop reason written when the recorder exits on a fault rather than a clean stop.</summary>
    public const string FaultStop = "fault";

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _timeToLive;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>
    /// The claims this process holds, each against the expiry <em>it</em> last wrote. The value is
    /// the fence <see cref="MayWrite"/> reads, so it is kept here rather than re-read per print.
    /// </summary>
    private readonly ConcurrentDictionary<(string Venue, string Instrument), DateTimeOffset> _held =
        new();

    /// <summary>Creates the lease with <see cref="DefaultTimeToLive"/>.</summary>
    /// <param name="scopes">Per-operation scopes — the store is scoped, this is not.</param>
    /// <param name="clock">The clock. Expiry is compared against it, never assumed.</param>
    public TapeLease(IServiceScopeFactory scopes, TimeProvider clock)
        : this(scopes, clock, DefaultTimeToLive)
    {
    }

    /// <summary>Creates the lease.</summary>
    /// <param name="scopes">Per-operation scopes — the store is scoped, this is not.</param>
    /// <param name="clock">The clock. Expiry is compared against it, never assumed.</param>
    /// <param name="timeToLive">How long a claim stands unrenewed. Tests pass a short one.</param>
    public TapeLease(IServiceScopeFactory scopes, TimeProvider clock, TimeSpan timeToLive)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeToLive, TimeSpan.Zero);

        _scopes = scopes;
        _clock = clock;
        _timeToLive = timeToLive;
        OwnerId = Guid.NewGuid().ToString("n");
    }

    /// <summary>
    /// This process's identity on the rows it holds — new on every start.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately not stable across restarts.</b> A stable identity would let a restarting
    /// process reclaim its own predecessor's claim immediately instead of waiting out
    /// <see cref="TimeToLive"/>, and there is no key that earns it: a container id changes on every
    /// redeploy, so it is not stable, and a host name or a configured name is shared by two
    /// containers on one host, so it is not unique. A key that is wrong in the second direction
    /// hands one tape to two writers, which is the failure this whole type exists to prevent. The
    /// retry tick makes it unnecessary — a redeploy waits out at most one term rather than needing
    /// to recognise itself.
    /// </remarks>
    public string OwnerId { get; }

    /// <summary>How long a claim stands unrenewed before another start may take it over.</summary>
    public TimeSpan TimeToLive => _timeToLive;

    /// <summary>
    /// How often a holder renews, and how often a refused instrument is re-attempted. A third of
    /// the time to live, so two renewals may be lost — a store blip, a slow write — before the
    /// claim is reclaimable by anyone else.
    /// </summary>
    public TimeSpan RenewInterval => _timeToLive / 3;

    /// <summary>The claims this process currently believes it holds.</summary>
    public IReadOnlyCollection<(string Venue, string Instrument)> Held => [.. _held.Keys];

    /// <summary>
    /// Whether a print received at <paramref name="receivedAt"/> may be stored — the fence.
    /// </summary>
    /// <param name="venue">The venue.</param>
    /// <param name="instrument">The normalised instrument symbol.</param>
    /// <param name="receivedAt">When the print was received, taken at the hub event.</param>
    /// <returns>
    /// <see langword="true"/> only while this process can show it held the claim at that instant.
    /// </returns>
    /// <remarks>
    /// The comparison is against the expiry this process last wrote, and it uses the print's
    /// receipt rather than now, because the question is whether the claim was held when the print
    /// arrived — not whether it is held now that a slow drain has got round to it. A holder that
    /// cannot renew stops storing prints at its own expiry without being told anything, which is
    /// the whole point: the earliest instant a replacement could exist is the latest instant this
    /// process may write.
    /// </remarks>
    public bool MayWrite(string venue, string instrument, DateTimeOffset receivedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(venue);
        ArgumentException.ThrowIfNullOrWhiteSpace(instrument);

        return _held.TryGetValue((venue, instrument), out DateTimeOffset expiry)
            && receivedAt < expiry;
    }

    /// <summary>The expiry this process last wrote for a claim it holds.</summary>
    /// <param name="venue">The venue.</param>
    /// <param name="instrument">The normalised instrument symbol.</param>
    /// <returns>The expiry, or <see langword="null"/> when this process holds no such claim.</returns>
    public DateTimeOffset? ExpiryOf(string venue, string instrument)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(venue);
        ArgumentException.ThrowIfNullOrWhiteSpace(instrument);

        return _held.TryGetValue((venue, instrument), out DateTimeOffset expiry) ? expiry : null;
    }

    /// <summary>
    /// Takes one instrument's claim, if it is free or the holder's has lapsed.
    /// </summary>
    /// <param name="venue">The venue.</param>
    /// <param name="instrument">The normalised instrument symbol.</param>
    /// <param name="cancellationToken">The stopping token.</param>
    /// <returns>Granted, held by a named other process, or unreadable.</returns>
    public async Task<TapeLeaseOutcome> TryAcquireAsync(
        string venue,
        string instrument,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(venue);
        ArgumentException.ThrowIfNullOrWhiteSpace(instrument);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DateTimeOffset now = _clock.GetUtcNow();
            DateTimeOffset expiry = now + _timeToLive;
            using IServiceScope scope = _scopes.CreateScope();
            TopstepXDbContext database = scope.ServiceProvider.GetRequiredService<TopstepXDbContext>();

            TapeLeaseRecord? row = await FindAsync(database, venue, instrument, cancellationToken)
                .ConfigureAwait(false);

            if (row is null)
            {
                database.TapeLeases.Add(new TapeLeaseRecord
                {
                    Venue = venue,
                    Instrument = instrument,
                    OwnerId = OwnerId,
                    Generation = 1,
                    AcquiredAt = now,
                    HeartbeatAt = now,
                    ExpiresAt = expiry,
                    HubConnected = false,
                    Listening = false,
                    StopReason = null,
                    StoppedAt = null,
                });

                await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                _held[(venue, instrument)] = expiry;
                return TapeLeaseOutcome.Granted();
            }

            if (!string.Equals(row.OwnerId, OwnerId, StringComparison.Ordinal) && now < row.ExpiresAt)
            {
                // Held, and not lapsed. The only free state is the absence of a claim or an expiry
                // that has actually passed; a quiet holder is a holder.
                return TapeLeaseOutcome.HeldBy(row.OwnerId, row.ExpiresAt);
            }

            // A killed holder leaves hub and listening true. A tombstone leaves a stop
            // reason. Either one would be this process's answer until the next publish,
            // so a takeover starts from a clear row (gh#869).
            row.OwnerId = OwnerId;
            row.Generation++;
            row.AcquiredAt = now;
            row.HeartbeatAt = now;
            row.ExpiresAt = expiry;
            row.HubConnected = false;
            row.Listening = false;
            row.StopReason = null;
            row.StoppedAt = null;

            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _held[(venue, instrument)] = expiry;
            return TapeLeaseOutcome.Granted();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (DbUpdateException)
        {
            // Either another start inserted the row between the read and the write, or its
            // takeover of the same expired row matched the generation first. Re-read, so the
            // answer names whoever actually won rather than guessing at it.
            //
            // The insert half of that is written for the relational shape: a duplicate key on a
            // concurrent insert. The in-memory provider the unit suite runs does not raise the
            // same way, so only the takeover half is covered by a test here — the integration
            // suite is where the relational shape is exercised (gh#387).
            return await ReReadHolderAsync(venue, instrument, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Renews one claim this process holds.
    /// </summary>
    /// <param name="venue">The venue.</param>
    /// <param name="instrument">The normalised instrument symbol.</param>
    /// <param name="cancellationToken">The stopping token.</param>
    /// <returns>
    /// Whether the claim was renewed, and when the replacement took it if it was not — so the
    /// caller can close its coverage range at the instant it stopped being the holder rather than
    /// at the instant it found out. A store fault throws instead: an unwritable renewal is not
    /// proof of a loss.
    /// </returns>
    public Task<TapeLeaseRenewal> TryRenewAsync(
        string venue,
        string instrument,
        CancellationToken cancellationToken) =>
        TryRenewAsync(venue, instrument, null, cancellationToken);

    /// <summary>
    /// Renews one claim and, when <paramref name="feed"/> is set, the hub state the holder last saw.
    /// </summary>
    /// <param name="venue">The venue.</param>
    /// <param name="instrument">The normalised instrument symbol.</param>
    /// <param name="feed">The hub state to store. Null leaves the stored flags alone.</param>
    /// <param name="cancellationToken">The stopping token.</param>
    /// <returns>Whether the claim was renewed.</returns>
    public async Task<TapeLeaseRenewal> TryRenewAsync(
        string venue,
        string instrument,
        TapeFeed? feed,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(venue);
        ArgumentException.ThrowIfNullOrWhiteSpace(instrument);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            DateTimeOffset now = _clock.GetUtcNow();
            DateTimeOffset expiry = now + _timeToLive;
            using IServiceScope scope = _scopes.CreateScope();
            TopstepXDbContext database = scope.ServiceProvider.GetRequiredService<TopstepXDbContext>();

            TapeLeaseRecord? row = await FindAsync(database, venue, instrument, cancellationToken)
                .ConfigureAwait(false);

            if (row is null || !string.Equals(row.OwnerId, OwnerId, StringComparison.Ordinal))
            {
                // Taken over while this process was paused, or the row was removed. Either way
                // this process is no longer the holder and must stop recording that instrument.
                // A row that is someone else's dates the handover; a row that is gone does not,
                // and the caller falls back to this process's own expiry.
                _held.TryRemove((venue, instrument), out _);
                return new TapeLeaseRenewal(false, row?.AcquiredAt);
            }

            row.HeartbeatAt = now;
            row.ExpiresAt = expiry;
            if (feed is { } noted)
            {
                row.HubConnected = noted.HubConnected;
                row.Listening = noted.Listening;
                row.StopReason = null;
                row.StoppedAt = null;
            }

            try
            {
                await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException)
            {
                // The generation moved between the read and the write: someone reclaimed it.
                _held.TryRemove((venue, instrument), out _);
                return new TapeLeaseRenewal(false, null);
            }

            _held[(venue, instrument)] = expiry;
            return new TapeLeaseRenewal(true, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Gives up one claim and leaves the row as a tombstone. The expiry is the stop instant, so
    /// the next start can take it at once, and health can tell a stop from a process that never claimed.
    /// </summary>
    /// <param name="venue">The venue.</param>
    /// <param name="instrument">The normalised instrument symbol.</param>
    /// <param name="stopReason">Why this process is releasing. Stored on the row.</param>
    /// <param name="cancellationToken">The stopping token.</param>
    /// <returns>A task that completes when the tombstone is written, or the row is not ours.</returns>
    public async Task ReleaseAsync(
        string venue,
        string instrument,
        string stopReason,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(venue);
        ArgumentException.ThrowIfNullOrWhiteSpace(instrument);
        ArgumentException.ThrowIfNullOrWhiteSpace(stopReason);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _held.TryRemove((venue, instrument), out _);

            using IServiceScope scope = _scopes.CreateScope();
            TopstepXDbContext database = scope.ServiceProvider.GetRequiredService<TopstepXDbContext>();

            TapeLeaseRecord? row = await FindAsync(database, venue, instrument, cancellationToken)
                .ConfigureAwait(false);

            // Only this process's own row. Tombstoning one a later start already took over would
            // mark a live recorder as stopped.
            if (row is null || !string.Equals(row.OwnerId, OwnerId, StringComparison.Ordinal))
            {
                return;
            }

            DateTimeOffset now = _clock.GetUtcNow();
            row.ExpiresAt = now;
            row.StoppedAt = now;
            row.HeartbeatAt = now;
            row.HubConnected = false;
            row.Listening = false;
            row.StopReason = stopReason;
            try
            {
                await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Reclaimed between the read and the delete. The new holder's row stands.
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Gives up the local claim on one instrument without asserting anything about the store.
    /// </summary>
    /// <param name="venue">The venue.</param>
    /// <param name="instrument">The normalised instrument symbol.</param>
    /// <remarks>
    /// For the one case where the store cannot be reached and the term has run out: this process
    /// can no longer show it is the holder, so it must stop behaving like one immediately —
    /// <see cref="MayWrite"/> refuses from the next print. It deliberately does not tombstone the
    /// row the way a release does, because the store is exactly what is not answering,
    /// and because the row may already belong to a replacement.
    /// </remarks>
    public void Forfeit(string venue, string instrument)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(venue);
        ArgumentException.ThrowIfNullOrWhiteSpace(instrument);
        _held.TryRemove((venue, instrument), out _);
    }

    /// <summary>Gives up every claim this process holds, leaving each row as a tombstone.</summary>
    /// <param name="stopReason">Why this process is releasing.</param>
    /// <param name="cancellationToken">The stopping token.</param>
    /// <returns>A task that completes when each release has been attempted.</returns>
    public async Task ReleaseAllAsync(string stopReason, CancellationToken cancellationToken)
    {
        foreach ((string venue, string instrument) in Held)
        {
            await ReleaseAsync(venue, instrument, stopReason, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Writes the holder's current hub state onto every claim it still owns, without extending the term.
    /// </summary>
    /// <param name="feedFor">The hub state for one instrument symbol.</param>
    /// <param name="cancellationToken">The stopping token.</param>
    /// <returns>
    /// The claims this write lost. A concurrency conflict or a row that is no longer ours is a
    /// takeover, the same fact a failed renewal reports, and the caller stands down. An empty
    /// list is a write that kept every claim it still held.
    /// </returns>
    public async Task<IReadOnlyList<TapeFeedLoss>> WriteFeedAsync(
        Func<string, TapeFeed> feedFor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(feedFor);
        List<TapeFeedLoss> lost = [];
        foreach ((string venue, string instrument) in Held)
        {
            TapeFeedLoss? loss = await WriteOneFeedAsync(
                    venue, instrument, feedFor(instrument), cancellationToken)
                .ConfigureAwait(false);
            if (loss is { } dropped)
            {
                lost.Add(dropped);
            }
        }

        return lost;
    }

    private async Task<TapeFeedLoss?> WriteOneFeedAsync(
        string venue,
        string instrument,
        TapeFeed feed,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_held.TryGetValue((venue, instrument), out DateTimeOffset term))
            {
                return null;
            }

            using IServiceScope scope = _scopes.CreateScope();
            TopstepXDbContext database = scope.ServiceProvider.GetRequiredService<TopstepXDbContext>();
            TapeLeaseRecord? row = await FindAsync(database, venue, instrument, cancellationToken)
                .ConfigureAwait(false);
            if (row is null || !string.Equals(row.OwnerId, OwnerId, StringComparison.Ordinal))
            {
                // Taken over between the last renew and this write. Same loss as a renewal
                // that reads someone else's row: drop the claim and tell the caller.
                _held.TryRemove((venue, instrument), out _);
                return new TapeFeedLoss(venue, instrument, term);
            }

            row.HubConnected = feed.HubConnected;
            row.Listening = feed.Listening;
            row.HeartbeatAt = _clock.GetUtcNow();
            row.StopReason = null;
            row.StoppedAt = null;
            try
            {
                await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Generation moved under the write. The renewal path treats that race as a
                // loss; leaving it only in _held would stop prints and never stand down.
                _held.TryRemove((venue, instrument), out _);
                return new TapeFeedLoss(venue, instrument, term);
            }

            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static Task<TapeLeaseRecord?> FindAsync(
        TopstepXDbContext database,
        string venue,
        string instrument,
        CancellationToken cancellationToken) =>
        database.TapeLeases
            .FirstOrDefaultAsync(
                lease => lease.Venue == venue && lease.Instrument == instrument,
                cancellationToken);

    private async Task<TapeLeaseOutcome> ReReadHolderAsync(
        string venue,
        string instrument,
        CancellationToken cancellationToken)
    {
        using IServiceScope scope = _scopes.CreateScope();
        TopstepXDbContext database = scope.ServiceProvider.GetRequiredService<TopstepXDbContext>();
        TapeLeaseRecord? row = await FindAsync(database, venue, instrument, cancellationToken)
            .ConfigureAwait(false);

        if (row is null)
        {
            // The write was refused and there is no row to name a holder from. Ownership is
            // unknown, and unknown is not free.
            return TapeLeaseOutcome.Unreadable();
        }

        return string.Equals(row.OwnerId, OwnerId, StringComparison.Ordinal)
            ? TapeLeaseOutcome.Unreadable()
            : TapeLeaseOutcome.HeldBy(row.OwnerId, row.ExpiresAt);
    }
}

/// <summary>The hub state a holder writes onto its claim (gh#869).</summary>
/// <param name="HubConnected">Whether the market hub is connected.</param>
/// <param name="Listening">Whether this instrument is subscribed.</param>
public readonly record struct TapeFeed(bool HubConnected, bool Listening);

/// <summary>A feed write that found the claim was no longer ours (gh#869).</summary>
/// <param name="Venue">The venue.</param>
/// <param name="Instrument">The normalised instrument symbol.</param>
/// <param name="Term">The expiry this process last wrote, for the coverage close.</param>
public readonly record struct TapeFeedLoss(string Venue, string Instrument, DateTimeOffset? Term);

/// <summary>The result of renewing one claim (gh#404).</summary>
/// <param name="Kept">Whether this process is still the holder.</param>
/// <param name="ReclaimedAt">
/// When the replacement took the claim, when a replacement is on the row. The losing holder closes
/// its coverage range here rather than at the instant it noticed, so no two ranges claim the same
/// window. <see langword="null"/> when the row is simply gone and nothing dates the handover.
/// </param>
public readonly record struct TapeLeaseRenewal(bool Kept, DateTimeOffset? ReclaimedAt);
