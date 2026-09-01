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
/// and is refused. Without that, the reclaim path would itself create the double writer.
/// </para>
/// <para>
/// <b>A renewal that finds the row taken is a loss, and the loser stands down.</b> A holder merely
/// paused past its expiry — a long stall, a store outage — can be taken over while it is still
/// subscribed. <see cref="TryRenewAsync"/> reports that, and the recorder drops the subscription
/// rather than keeping a second writer on the tape. That, not the expiry itself, is what keeps a
/// reclaim from producing the failure this type prevents.
/// </para>
/// <para>
/// <b>The lock here is a leaf.</b> This type never takes <see cref="TapeCoverageLedger"/>'s store
/// gate or its bookkeeping lock, and the ledger never calls this. There is therefore no path that
/// takes two of the three in either order, and the ledger's uniform store-then-bookkeeping
/// acquisition is untouched.
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
    /// Fixed rather than configured. A shorter value reclaims a crashed holder sooner and evicts a
    /// merely slow one more often; a longer one strands the tape after a crash. This is generous
    /// against both a full garbage collection pause and plausible clock skew between two hosts,
    /// and the generation check means skew can only move <em>when</em> a reclaim happens, never
    /// produce two owners.
    /// </remarks>
    public static readonly TimeSpan DefaultTimeToLive = TimeSpan.FromSeconds(90);

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _timeToLive;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<(string Venue, string Instrument)> _held = [];

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
    /// This process's identity on the rows it holds — new on every start, so a restart is never
    /// mistaken for the process it replaced.
    /// </summary>
    public string OwnerId { get; }

    /// <summary>How long a claim stands unrenewed before another start may take it over.</summary>
    public TimeSpan TimeToLive => _timeToLive;

    /// <summary>
    /// How often a holder renews. A third of the time to live, so two renewals may be lost — a
    /// store blip, a slow write — before the claim is reclaimable by anyone else.
    /// </summary>
    public TimeSpan RenewInterval => _timeToLive / 3;

    /// <summary>The claims this process currently believes it holds.</summary>
    public IReadOnlyCollection<(string Venue, string Instrument)> Held
    {
        get
        {
            _gate.Wait();
            try
            {
                return [.. _held];
            }
            finally
            {
                _gate.Release();
            }
        }
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
                    ExpiresAt = now + _timeToLive,
                });

                await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                _held.Add((venue, instrument));
                return TapeLeaseOutcome.Granted();
            }

            if (!string.Equals(row.OwnerId, OwnerId, StringComparison.Ordinal) && now < row.ExpiresAt)
            {
                // Held, and not lapsed. The only free state is the absence of a claim or an expiry
                // that has actually passed; a quiet holder is a holder.
                return TapeLeaseOutcome.HeldBy(row.OwnerId, row.ExpiresAt);
            }

            row.OwnerId = OwnerId;
            row.Generation++;
            row.AcquiredAt = now;
            row.HeartbeatAt = now;
            row.ExpiresAt = now + _timeToLive;

            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _held.Add((venue, instrument));
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
    /// <see langword="false"/> when the claim is gone — taken over, or deleted — so the caller can
    /// stand down. A store fault throws instead: an unwritable renewal is not proof of a loss.
    /// </returns>
    public async Task<bool> TryRenewAsync(
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
            using IServiceScope scope = _scopes.CreateScope();
            TopstepXDbContext database = scope.ServiceProvider.GetRequiredService<TopstepXDbContext>();

            TapeLeaseRecord? row = await FindAsync(database, venue, instrument, cancellationToken)
                .ConfigureAwait(false);

            if (row is null || !string.Equals(row.OwnerId, OwnerId, StringComparison.Ordinal))
            {
                // Taken over while this process was paused, or the row was removed. Either way
                // this process is no longer the holder and must stop recording that instrument.
                _held.Remove((venue, instrument));
                return false;
            }

            row.HeartbeatAt = now;
            row.ExpiresAt = now + _timeToLive;

            try
            {
                await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateConcurrencyException)
            {
                // The generation moved between the read and the write: someone reclaimed it.
                _held.Remove((venue, instrument));
                return false;
            }

            _held.Add((venue, instrument));
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Gives up one claim, so the next start does not have to wait out the time to live.
    /// </summary>
    /// <param name="venue">The venue.</param>
    /// <param name="instrument">The normalised instrument symbol.</param>
    /// <param name="cancellationToken">The stopping token.</param>
    /// <returns>A task that completes when the row is gone or was never this process's.</returns>
    public async Task ReleaseAsync(
        string venue,
        string instrument,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(venue);
        ArgumentException.ThrowIfNullOrWhiteSpace(instrument);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _held.Remove((venue, instrument));

            using IServiceScope scope = _scopes.CreateScope();
            TopstepXDbContext database = scope.ServiceProvider.GetRequiredService<TopstepXDbContext>();

            TapeLeaseRecord? row = await FindAsync(database, venue, instrument, cancellationToken)
                .ConfigureAwait(false);

            // Only this process's own row. Deleting one a later start already took over would hand
            // the tape to a third process while the second is still recording under it.
            if (row is null || !string.Equals(row.OwnerId, OwnerId, StringComparison.Ordinal))
            {
                return;
            }

            database.TapeLeases.Remove(row);
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

    /// <summary>Gives up every claim this process holds.</summary>
    /// <param name="cancellationToken">The stopping token.</param>
    /// <returns>A task that completes when each release has been attempted.</returns>
    public async Task ReleaseAllAsync(CancellationToken cancellationToken)
    {
        foreach ((string venue, string instrument) in Held)
        {
            await ReleaseAsync(venue, instrument, cancellationToken).ConfigureAwait(false);
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
