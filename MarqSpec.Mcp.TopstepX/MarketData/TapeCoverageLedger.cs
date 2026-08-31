using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// The <c>TapeCoverage</c> state machine, stated once: which contracts this process believes it is
/// listening to, which closes are queued behind the store gate, and from which instant a print may
/// be stored. <see cref="TradeTapeRecorder"/> owns the hub and the print pipeline and calls this;
/// this owns the ledger and takes no hub.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is a type.</b> Five of one release's six defects — gh#365, gh#376, gh#377, gh#378 and
/// gh#382 — landed in this half of <see cref="TradeTapeRecorder"/>. Each fix was correct alone;
/// together they were five patches to a state machine nothing had ever stated. gh#382 is the clearest
/// symptom: a query that should be scoped by the ledger's own key, written where no type held that key.
/// The invariants below used to live as comments scattered across 900 lines (gh#390).
/// </para>
/// <para>
/// <b>The invariants, in one place.</b>
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// <b>An open range is claimed before the store write, and discarded if that write fails.</b> The
/// venue can print the instant it accepts a subscribe, so the claim has to precede the row; a claim
/// whose row never landed is a hole, not a range, and is removed from <em>both</em> the open set and
/// any close it has already been snapshotted into (gh#376).
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Prints from a subscribe attempt are suppressed until that attempt's ledger row lands.</b> The
/// boundary is recorded before the subscribe RPC, not after it, so a print queued while the RPC is
/// in flight is covered by it. An earlier attempt whose open never reached the store stays the
/// boundary: moving it forward would let that attempt's uncovered prints through as though they
/// belonged to a previous listen (gh#376).
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>A close retires the matching still-open row by the range that opened it</b> — venue,
/// instrument, contract <em>and</em> <c>RangeStart</c> — before the exclusive end is written, so a
/// close can never retire a later listen's open row (gh#377).
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>A zero-length close retires the open row and writes nothing.</b> An empty range is not
/// coverage, and storing one would claim a window in which nothing was listening.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>The store gate is held across take-the-batch and the write.</b> Taking the batch before the
/// gate would put a queued close beyond the reach of a failed open that must still discard it
/// (gh#376). The gate also serialises the print writes the recorder makes through
/// <see cref="EnterStoreAsync"/>: the in-memory provider is not safe for concurrent
/// <c>SaveChanges</c>, and a hang there looks like a silent tape.
/// </description>
/// </item>
/// </list>
/// <para>
/// <b>No captive dependency.</b> Every store operation opens its own scope through
/// <see cref="IServiceScopeFactory"/>; <c>TopstepXDbContext</c> is registered scoped and this is
/// held by a singleton.
/// </para>
/// </remarks>
public sealed class TapeCoverageLedger
{
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;

    private readonly Dictionary<string, OpenRange> _openRanges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _suppressPrintsFrom = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _ledgerOpenFrom = new(StringComparer.Ordinal);
    private readonly List<ClosedRange> _pendingCloses = [];

    /// <summary>Guards the four collections above. Held for bookkeeping only, never across an await.</summary>
    private readonly object _coverageGate = new();

    /// <summary>
    /// Serialises store writes. Print persist and coverage persist can run on different tasks; the
    /// in-memory provider is not safe for concurrent <c>SaveChanges</c>, and a hang there looks like
    /// a silent tape.
    /// </summary>
    private readonly SemaphoreSlim _store = new(1, 1);

    /// <summary>Creates the ledger.</summary>
    /// <param name="scopes">Per-operation scopes — the store is scoped, this is not.</param>
    /// <param name="clock">The clock. <c>RecordedAt</c> is stamped from it.</param>
    public TapeCoverageLedger(IServiceScopeFactory scopes, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(clock);

        _scopes = scopes;
        _clock = clock;
    }

    /// <summary>
    /// Takes the store gate so the caller can write outside the ledger — the print pipeline — under
    /// the same serialisation the coverage writes use. Release it by disposing the lease.
    /// </summary>
    /// <param name="cancellationToken">The stopping token.</param>
    /// <returns>A lease that releases the gate when disposed.</returns>
    public async Task<StoreLease> EnterStoreAsync(CancellationToken cancellationToken)
    {
        await _store.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new StoreLease(_store);
    }

    /// <summary>
    /// Claims a listen in memory the moment the venue confirms the subscribe, <em>before</em> its row
    /// is written. Invariant 1: the claim precedes the store write, and
    /// <see cref="DiscardFailedOpen"/> takes it back if that write does not land.
    /// </summary>
    /// <param name="contractId">The contract that was subscribed.</param>
    /// <param name="venue">The venue the contract was resolved through.</param>
    /// <param name="instrument">The normalised instrument symbol.</param>
    /// <param name="start">The instant the subscribe was confirmed — the range's inclusive start.</param>
    public void ClaimOpenRange(string contractId, string venue, string instrument, DateTimeOffset start)
    {
        lock (_coverageGate)
        {
            _openRanges[contractId] = new OpenRange(venue, instrument, start);
        }
    }

    /// <summary>
    /// Snapshots every claimed listen into the pending closes at <paramref name="end"/> and clears the
    /// open set. Called from the hub's drop transition and from shutdown, both of which run on a
    /// thread that must not block on the store — the write is
    /// <see cref="PersistPendingClosesAsync"/>, queued separately.
    /// </summary>
    /// <param name="end">The exclusive end of every range being closed.</param>
    public void CloseOpenRangesAt(DateTimeOffset end)
    {
        lock (_coverageGate)
        {
            foreach ((string contractId, OpenRange open) in _openRanges)
            {
                _pendingCloses.Add(
                    new ClosedRange(open.Venue, open.Instrument, contractId, open.RangeStart, end));
            }

            _openRanges.Clear();
        }
    }

    /// <summary>
    /// Closes one claimed listen at <paramref name="end"/>. A contract with no claim is a no-op: a
    /// subscribe that never succeeded opened no range to close.
    /// </summary>
    /// <param name="contractId">The contract whose listen ended.</param>
    /// <param name="end">The exclusive end of the range.</param>
    public void CloseOpenRangeAt(string contractId, DateTimeOffset end)
    {
        lock (_coverageGate)
        {
            if (_openRanges.Remove(contractId, out OpenRange open))
            {
                _pendingCloses.Add(
                    new ClosedRange(open.Venue, open.Instrument, contractId, open.RangeStart, end));
            }
        }
    }

    /// <summary>
    /// Discards still-open coverage rows this start supersedes — the ones an earlier run of
    /// <b>this</b> process left behind — so a crash cannot claim coverage after death.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Scoped to the instruments this start is about to subscribe.</b> The predicate carries
    /// the venue and the instruments a front contract actually resolved for, not the whole table.
    /// Unscoped, a second recorder passing the HTTP and <c>RecordTape</c> gates — a rolling
    /// redeploy, or two recorders split by <c>MarketData__Instruments</c> — deleted every open row
    /// in the store, including one a live process was still writing under. That process does not
    /// notice: it keeps writing prints and stays Listening, while its reads take the empty-ledger
    /// refusal gh#365 closed. The range is not recoverable — there is no market-tape REST backfill
    /// (ADR-0016, gh#382).
    /// </para>
    /// <para>
    /// <b>An open row for an instrument this process does not record is left, not deleted.</b> The
    /// two answers are indistinguishable from outside the store, so this one is written down:
    /// another recorder may still own that row, and deleting it destroys a ledger range that
    /// cannot be rebuilt, whereas leaving it costs this process nothing —
    /// <c>VolumeProfileService</c> only reads a still-open row as coverage while that instrument
    /// is Listening <i>here</i>, and an instrument this process never subscribed never is, so a
    /// foreign sentinel is filtered out of its own answers.
    /// </para>
    /// <para>
    /// <b><c>ContractId</c> is deliberately not in the predicate.</b> Every open row for an
    /// instrument this start subscribes is superseded, whichever contract it names, because a
    /// leftover written before a roll would otherwise survive forever — and the Listening guard
    /// is per instrument, so that stale sentinel would read as coverage to
    /// <see cref="TapeCoverageRecord.StillListeningEnd"/> on a contract nothing is listening to.
    /// </para>
    /// <para>
    /// <b>What this does not fix.</b> Two recorders configured for the <i>same</i> instrument — a
    /// rolling redeploy, or a restart overlapping a still-draining container — resolve the same
    /// front contract, so the starting one still supersedes the running one's open row. No
    /// predicate can separate them: "my crash leftover" and "their live listen" are the same row,
    /// and this method has to keep deleting the first. That overlap is the deployment ADR-0016
    /// already calls wrong, and closing it means refusing the second recorder a claim rather than
    /// widening a query (gh#404). It is left visible here rather than papered over.
    /// </para>
    /// </remarks>
    /// <param name="venue">The venue this start resolved its contracts through.</param>
    /// <param name="instruments">
    /// The instruments this start is about to subscribe, at that venue. An empty set discards
    /// nothing — a start that resolved no contract supersedes no row.
    /// </param>
    /// <param name="cancellationToken">The stopping token.</param>
    public async Task DiscardAbandonedOpenRangesAsync(
        string venue,
        IEnumerable<string> instruments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instruments);

        List<string> scoped = [.. instruments.Distinct(StringComparer.Ordinal)];
        if (scoped.Count == 0)
        {
            return;
        }

        await _store.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using IServiceScope scope = _scopes.CreateScope();
            TopstepXDbContext database = scope.ServiceProvider.GetRequiredService<TopstepXDbContext>();
            List<TapeCoverageRecord> abandoned = await database.TapeCoverage
                .Where(row => row.RangeEnd == TapeCoverageRecord.StillListeningEnd
                    && row.Venue == venue
                    && scoped.Contains(row.Instrument))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (abandoned.Count == 0)
            {
                return;
            }

            database.TapeCoverage.RemoveRange(abandoned);
            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _store.Release();
        }
    }

    /// <summary>
    /// Writes the still-open row for a confirmed listen, and records that it landed so the
    /// suppression boundary (invariant 2) lifts.
    /// </summary>
    /// <remarks>
    /// Any earlier still-open row on the same <c>(Venue, Instrument, ContractId)</c> is retired
    /// first: two open sentinels on one key are indistinguishable to a reader, and the later listen
    /// is the live one. A row that already <i>is</i> this range is left alone rather than rewritten.
    /// A store fault discards the in-memory claim (invariant 1) while the gate is still held, so a
    /// close queued behind this gate cannot invent a closed row for an open the store refused, and
    /// then rethrows — the caller drops the venue subscription rather than claiming the venue
    /// refused it (gh#376).
    /// </remarks>
    /// <param name="venue">The venue.</param>
    /// <param name="instrument">The normalised instrument symbol.</param>
    /// <param name="contractId">The contract being listened to.</param>
    /// <param name="start">The range's inclusive start — the instant the subscribe was confirmed.</param>
    /// <param name="cancellationToken">The stopping token.</param>
    public async Task PersistOpenRangeAsync(
        string venue,
        string instrument,
        string contractId,
        DateTimeOffset start,
        CancellationToken cancellationToken)
    {
        await _store.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using IServiceScope scope = _scopes.CreateScope();
            TopstepXDbContext database = scope.ServiceProvider.GetRequiredService<TopstepXDbContext>();
            List<TapeCoverageRecord> stillOpen = await database.TapeCoverage
                .Where(row => row.Venue == venue
                    && row.Instrument == instrument
                    && row.ContractId == contractId
                    && row.RangeEnd == TapeCoverageRecord.StillListeningEnd)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            if (stillOpen.Count == 1
                && stillOpen[0].RangeStart == start)
            {
                RememberLedgerOpen(contractId, start);
                return;
            }

            if (stillOpen.Count > 0)
            {
                database.TapeCoverage.RemoveRange(stillOpen);
            }

            database.TapeCoverage.Add(new TapeCoverageRecord
            {
                Venue = venue,
                Instrument = instrument,
                ContractId = contractId,
                RangeStart = start,
                RangeEnd = TapeCoverageRecord.StillListeningEnd,
                RecordedAt = _clock.GetUtcNow(),
            });

            await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            RememberLedgerOpen(contractId, start);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Still holding the store gate: discard the in-memory listen so a close write
            // queued behind this gate cannot invent a closed row for an open the store
            // refused (gh#376).
            DiscardFailedOpen(contractId, start);
            throw;
        }
        finally
        {
            _store.Release();
        }
    }

    /// <summary>
    /// Flushes the queued closes: retire each one's still-open row by the range that opened it
    /// (invariant 3), then write the exclusive ends (invariant 4 skips the zero-length ones).
    /// </summary>
    /// <remarks>
    /// The batch is taken <em>under</em> the store gate, not before it (invariant 5): a range still
    /// queued is a range a failed open persist can still discard. A fault puts the whole batch back
    /// at the head of the queue and rethrows, so a close is retried rather than lost — leaving a
    /// still-open row standing while the outage lasts, which reads as an absence rather than as
    /// coverage.
    /// </remarks>
    /// <param name="cancellationToken">The stopping token.</param>
    public async Task PersistPendingClosesAsync(CancellationToken cancellationToken)
    {
        // The batch is taken under the store gate, not before it: a range still queued is a
        // range a failed open persist can still discard (gh#376).
        await _store.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ClosedRange[] batch;
            lock (_coverageGate)
            {
                if (_pendingCloses.Count == 0)
                {
                    return;
                }

                batch = [.. _pendingCloses];
                _pendingCloses.Clear();
            }

            using IServiceScope scope = _scopes.CreateScope();
            TopstepXDbContext database = scope.ServiceProvider.GetRequiredService<TopstepXDbContext>();
            try
            {
                bool retired = false;
                foreach (ClosedRange range in batch)
                {
                    List<TapeCoverageRecord> stillOpen = await database.TapeCoverage
                        .Where(row => row.Venue == range.Venue
                            && row.Instrument == range.Instrument
                            && row.ContractId == range.ContractId
                            && row.RangeStart == range.RangeStart
                            && row.RangeEnd == TapeCoverageRecord.StillListeningEnd)
                        .ToListAsync(cancellationToken)
                        .ConfigureAwait(false);

                    if (stillOpen.Count == 0)
                    {
                        continue;
                    }

                    database.TapeCoverage.RemoveRange(stillOpen);
                    retired = true;
                }

                if (retired)
                {
                    await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }

                DateTimeOffset recordedAt = _clock.GetUtcNow();
                bool wroteClosed = false;
                foreach (ClosedRange range in batch)
                {
                    if (range.RangeEnd <= range.RangeStart)
                    {
                        continue;
                    }

                    database.TapeCoverage.Add(new TapeCoverageRecord
                    {
                        Venue = range.Venue,
                        Instrument = range.Instrument,
                        ContractId = range.ContractId,
                        RangeStart = range.RangeStart,
                        RangeEnd = range.RangeEnd,
                        RecordedAt = recordedAt,
                    });
                    wroteClosed = true;
                }

                if (wroteClosed)
                {
                    await database.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                lock (_coverageGate)
                {
                    _pendingCloses.InsertRange(0, batch);
                }

                throw;
            }
        }
        finally
        {
            _store.Release();
        }
    }

    /// <summary>
    /// Records the instant a subscribe was attempted, from which this contract's prints are
    /// suppressed until that attempt's ledger row lands (invariant 2).
    /// </summary>
    /// <param name="contractId">The contract being subscribed.</param>
    /// <param name="attempt">The instant before the subscribe RPC, not after it.</param>
    public void RememberSubscribeAttempt(string contractId, DateTimeOffset attempt)
    {
        lock (_coverageGate)
        {
            // An earlier attempt whose open never reached the store stays the boundary. Its
            // prints are uncovered whatever happens next, and moving the boundary forward
            // would let them through as if they belonged to a previous listen.
            if (_suppressPrintsFrom.TryGetValue(contractId, out DateTimeOffset suppress)
                && (!_ledgerOpenFrom.TryGetValue(contractId, out DateTimeOffset ledger) || ledger < suppress))
            {
                return;
            }

            _suppressPrintsFrom[contractId] = attempt;
        }
    }

    /// <summary>
    /// Takes back a claim whose store write did not land: the open range, and any close it was
    /// already snapshotted into by a hub drop that raced the write (invariant 1, gh#376).
    /// </summary>
    /// <param name="contractId">The contract whose open persist failed.</param>
    /// <param name="start">The start of the range that failed, so a later listen's is not discarded.</param>
    public void DiscardFailedOpen(string contractId, DateTimeOffset start)
    {
        lock (_coverageGate)
        {
            _openRanges.Remove(contractId);
            _pendingCloses.RemoveAll(range =>
                string.Equals(range.ContractId, contractId, StringComparison.Ordinal)
                && range.RangeStart == start);
        }
    }

    /// <summary>
    /// Whether a print received at <paramref name="receivedAt"/> falls in a window no ledger row
    /// covers, and so must not be stored (invariant 2).
    /// </summary>
    /// <param name="contractId">The contract the print arrived on.</param>
    /// <param name="receivedAt">When the print was received, taken at the hub event.</param>
    /// <returns><see langword="true"/> to drop the print.</returns>
    public bool ShouldSuppressPrint(string contractId, DateTimeOffset receivedAt)
    {
        lock (_coverageGate)
        {
            if (!_suppressPrintsFrom.TryGetValue(contractId, out DateTimeOffset suppress)
                || receivedAt < suppress)
            {
                return false;
            }

            bool landedOpen = _ledgerOpenFrom.TryGetValue(contractId, out DateTimeOffset ledger)
                && ledger >= suppress;
            return !landedOpen || receivedAt < ledger;
        }
    }

    /// <summary>Records that this contract's open row reached the store, lifting the suppression.</summary>
    private void RememberLedgerOpen(string contractId, DateTimeOffset start)
    {
        lock (_coverageGate)
        {
            _ledgerOpenFrom[contractId] = start;
        }
    }

    /// <summary>Holds the store gate until disposed. Returned by <see cref="EnterStoreAsync"/>.</summary>
    public readonly struct StoreLease : IDisposable
    {
        private readonly SemaphoreSlim? _store;

        internal StoreLease(SemaphoreSlim store) => _store = store;

        /// <summary>Releases the store gate.</summary>
        public void Dispose() => _store?.Release();
    }

    private readonly record struct OpenRange(string Venue, string Instrument, DateTimeOffset RangeStart);

    private readonly record struct ClosedRange(
        string Venue,
        string Instrument,
        string ContractId,
        DateTimeOffset RangeStart,
        DateTimeOffset RangeEnd);
}
