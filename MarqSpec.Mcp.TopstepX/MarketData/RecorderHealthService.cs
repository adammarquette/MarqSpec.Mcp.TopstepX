using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using MarqSpec.Mcp.TopstepX.Tools;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// Builds the recorder-health report from the tape holder, the session calendar and the store (R-5.13).
/// </summary>
/// <remarks>
/// <para>
/// Reads only. The startup <see cref="StoreAvailabilityHolder"/> is not this probe and is not updated: a
/// health poll that rewrote it would make every other tool flip between available and unavailable on the
/// poll's cadence. A failure is <see cref="RecorderHealthReport.StoreConnectivity.Unavailable"/> with the
/// ledger omitted, never a message — the driver's text can name the host (gh#551).
/// </para>
/// <para>
/// An open <see cref="TapeCoverageRecord.StillListeningEnd"/> counts through the report instant only while
/// that symbol is listening. A still-open row while it is not is a crash leftover, and treating it as
/// coverage is how a dead recorder looks fully covered.
/// </para>
/// </remarks>
public sealed class RecorderHealthService(
    IRecorderHealthStore store,
    TapeAvailabilityHolder tape,
    InstrumentRegistry instruments,
    BarSessionCalendar calendar,
    TimeProvider clock,
    HostTelemetry telemetry)
{
    private readonly IRecorderHealthStore _store = store;
    private readonly TapeAvailabilityHolder _tape = tape;
    private readonly InstrumentRegistry _instruments = instruments;
    private readonly BarSessionCalendar _calendar = calendar;
    private readonly TimeProvider _clock = clock;
    private readonly HostTelemetry _telemetry = telemetry;

    /// <summary>The report as of the clock's current instant.</summary>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The report. A missing print time and an unreadable ledger are omitted.</returns>
    public async Task<RecorderHealthReport.Report> ReadAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset asOf = _clock.GetUtcNow();
        TapeAvailability process = _tape.Value;
        bool feedConnected = process.Reason is TapeUnavailableReason.None
            or TapeUnavailableReason.ConnectedButNotSubscribed;

        bool databaseUp = await ProbeAsync(cancellationToken).ConfigureAwait(false);
        ProductSession? open = ProductSessions.Containing(_calendar, asOf);
        ProductSession? named = open ?? ProductSessions.LatestClosed(_calendar, asOf);

        IReadOnlyList<ContractLastTick> ticks = [];
        IReadOnlyList<CoverageSlice> slices = [];
        if (databaseUp)
        {
            try
            {
                ticks = await _store.LastTicksAsync(cancellationToken).ConfigureAwait(false);
                if (named is { } window && Horizon(asOf, window) > window.OpenUtc)
                {
                    slices = await _store.CoverageAsync(window.OpenUtc, Horizon(asOf, window), cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                databaseUp = false;
                ticks = [];
                slices = [];
            }
        }

        IReadOnlyList<RecorderHealthReport.ContractStatus> contracts = BuildContracts(ticks, slices, databaseUp);
        IReadOnlyList<RecorderHealthReport.Gap>? gaps = databaseUp && named is { } session
            ? BuildGaps(session, asOf, slices, contracts)
            : null;

        _telemetry.RecorderHealthRead(Outcome(databaseUp, feedConnected, contracts, gaps));

        return new RecorderHealthReport.Report(
            asOf,
            feedConnected,
            process.Reason,
            databaseUp
                ? RecorderHealthReport.StoreConnectivity.Available
                : RecorderHealthReport.StoreConnectivity.Unavailable,
            named is { } reported
                ? new RecorderHealthReport.Session(
                    reported.TradeDate,
                    reported.OpenUtc,
                    reported.CloseUtc,
                    open is not null)
                : null,
            contracts,
            gaps);
    }

    private async Task<bool> ProbeAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _store.DatabaseConnectedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private IReadOnlyList<RecorderHealthReport.ContractStatus> BuildContracts(
        IReadOnlyList<ContractLastTick> ticks,
        IReadOnlyList<CoverageSlice> slices,
        bool databaseUp)
    {
        Dictionary<string, Dictionary<string, DateTimeOffset?>> bySymbol = new(StringComparer.Ordinal);
        foreach (InstrumentId instrument in _instruments.Instruments)
        {
            bySymbol.TryAdd(instrument.Symbol, new Dictionary<string, DateTimeOffset?>(StringComparer.Ordinal));
        }

        if (databaseUp)
        {
            foreach (ContractLastTick tick in ticks)
            {
                Dictionary<string, DateTimeOffset?> contracts = ContractsFor(bySymbol, tick.Instrument);
                if (!contracts.TryGetValue(tick.ContractId, out DateTimeOffset? existing)
                    || existing is null
                    || tick.TradeTimeUtc > existing)
                {
                    contracts[tick.ContractId] = tick.TradeTimeUtc;
                }
            }

            foreach (CoverageSlice slice in slices)
            {
                ContractsFor(bySymbol, slice.Instrument).TryAdd(slice.ContractId, null);
            }
        }

        List<RecorderHealthReport.ContractStatus> rows = [];
        foreach ((string symbol, Dictionary<string, DateTimeOffset?> contracts) in bySymbol)
        {
            TapeAvailability availability = _tape.For(symbol);
            if (contracts.Count == 0)
            {
                rows.Add(new RecorderHealthReport.ContractStatus(
                    symbol,
                    null,
                    availability.IsListening,
                    availability.Reason,
                    null));
                continue;
            }

            foreach ((string contractId, DateTimeOffset? lastTick) in contracts)
            {
                rows.Add(new RecorderHealthReport.ContractStatus(
                    symbol,
                    contractId,
                    availability.IsListening,
                    availability.Reason,
                    lastTick));
            }
        }

        rows.Sort(static (left, right) =>
        {
            int symbol = string.CompareOrdinal(left.Symbol, right.Symbol);
            return symbol != 0 ? symbol : CompareContractId(left.ContractId, right.ContractId);
        });
        return rows;
    }

    private static Dictionary<string, DateTimeOffset?> ContractsFor(
        Dictionary<string, Dictionary<string, DateTimeOffset?>> bySymbol,
        string instrument)
    {
        string symbol = new InstrumentId(instrument).Symbol;
        if (!bySymbol.TryGetValue(symbol, out Dictionary<string, DateTimeOffset?>? contracts))
        {
            contracts = new Dictionary<string, DateTimeOffset?>(StringComparer.Ordinal);
            bySymbol.Add(symbol, contracts);
        }

        return contracts;
    }

    private List<RecorderHealthReport.Gap> BuildGaps(
        ProductSession session,
        DateTimeOffset asOf,
        IReadOnlyList<CoverageSlice> slices,
        IReadOnlyList<RecorderHealthReport.ContractStatus> contracts)
    {
        DateTimeOffset horizon = Horizon(asOf, session);
        if (horizon <= session.OpenUtc)
        {
            return [];
        }

        List<RecorderHealthReport.Gap> gaps = [];
        foreach (RecorderHealthReport.ContractStatus row in contracts)
        {
            if (row.ContractId is null)
            {
                gaps.Add(new RecorderHealthReport.Gap(row.Symbol, null, session.OpenUtc, horizon));
                continue;
            }

            List<SessionCoverageGaps.Interval> covered = [];
            foreach (CoverageSlice slice in slices)
            {
                if (!string.Equals(new InstrumentId(slice.Instrument).Symbol, row.Symbol, StringComparison.Ordinal)
                    || !string.Equals(slice.ContractId, row.ContractId, StringComparison.Ordinal))
                {
                    continue;
                }

                if (slice.RangeEnd == TapeCoverageRecord.StillListeningEnd && !row.Listening)
                {
                    continue;
                }

                covered.Add(new SessionCoverageGaps.Interval(slice.RangeStart, slice.RangeEnd));
            }

            foreach (SessionCoverageGaps.Interval hole in SessionCoverageGaps.Find(session.OpenUtc, horizon, covered))
            {
                gaps.Add(new RecorderHealthReport.Gap(row.Symbol, row.ContractId, hole.Start, hole.End));
            }
        }

        gaps.Sort(static (left, right) =>
        {
            int symbol = string.CompareOrdinal(left.Symbol, right.Symbol);
            if (symbol != 0)
            {
                return symbol;
            }

            int contract = CompareContractId(left.ContractId, right.ContractId);
            return contract != 0 ? contract : left.FromUtc.CompareTo(right.FromUtc);
        });
        return gaps;
    }

    private static DateTimeOffset Horizon(DateTimeOffset asOf, ProductSession session) =>
        asOf < session.CloseUtc ? asOf : session.CloseUtc;

    private static string Outcome(
        bool databaseUp,
        bool feedConnected,
        IReadOnlyList<RecorderHealthReport.ContractStatus> contracts,
        IReadOnlyList<RecorderHealthReport.Gap>? gaps)
    {
        if (!databaseUp)
        {
            return RecorderHealthOutcome.Unavailable;
        }

        if (feedConnected && gaps is { Count: 0 } && contracts.All(static row => row.Listening))
        {
            return RecorderHealthOutcome.Ok;
        }

        return RecorderHealthOutcome.Degraded;
    }

    private static int CompareContractId(string? left, string? right)
    {
        if (left is null && right is null)
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        return string.CompareOrdinal(left, right);
    }
}
