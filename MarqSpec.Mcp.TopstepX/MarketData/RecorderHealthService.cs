using System.Diagnostics;
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
/// Rows are the configured symbols, plus a contract only when that contract printed or was covered inside
/// the elapsed session. A rolled-off contract whose tape ended earlier is not a row, so it cannot look like
/// a session-long gap. An unconfigured symbol is not a row either.
/// </para>
/// <para>
/// An open <see cref="TapeCoverageRecord.StillListeningEnd"/> counts through the report instant only while
/// that symbol is listening. A still-open row while it is not is a crash leftover, and treating it as
/// coverage is how a dead recorder looks fully covered.
/// </para>
/// <para>
/// The database work has its own deadline, <see cref="DefaultProbeTimeout"/>, measured on the injected
/// clock. The caller's cancellation is a different fact and is not counted, and a build the caller cancelled
/// is not remembered. A fresh report is remembered for <see cref="RecorderHealthMemo.Ttl"/>, five seconds
/// in this process, so a tight poll does not scan the tape again. A reused reply says when it was stored.
/// </para>
/// </remarks>
public sealed class RecorderHealthService(
    IRecorderHealthStore store,
    TapeAvailabilityHolder tape,
    InstrumentRegistry instruments,
    BarSessionCalendar calendar,
    TimeProvider clock,
    HostTelemetry telemetry,
    RecorderHealthMemo memo)
{
    /// <summary>How long the database work of one report may take before it counts as a timeout.</summary>
    public static readonly TimeSpan DefaultProbeTimeout = TimeSpan.FromSeconds(2);

    private readonly IRecorderHealthStore _store = store;
    private readonly TapeAvailabilityHolder _tape = tape;
    private readonly InstrumentRegistry _instruments = instruments;
    private readonly BarSessionCalendar _calendar = calendar;
    private readonly TimeProvider _clock = clock;
    private readonly HostTelemetry _telemetry = telemetry;
    private readonly RecorderHealthMemo _memo = memo;

    /// <summary>The deadline for this instance. Tests in this assembly shorten it; production leaves the default.</summary>
    internal TimeSpan ProbeTimeout { get; set; } = DefaultProbeTimeout;

    /// <summary>The report as of the clock's current instant.</summary>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The report. A missing print time and an unreadable ledger are omitted.</returns>
    public async Task<RecorderHealthReport.Report> ReadAsync(CancellationToken cancellationToken)
    {
        using Activity? span = _telemetry.Activities.StartActivity("recorder.health", ActivityKind.Internal);

        if (_memo.Read(_clock) is { } cached)
        {
            Tag(span, cached.Outcome);
            return Stamp(cached.Report, cached.CachedAt);
        }

        await _memo.EnterAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_memo.Read(_clock) is { } fresh)
            {
                Tag(span, fresh.Outcome);
                return Stamp(fresh.Report, fresh.CachedAt);
            }

            (RecorderHealthReport.Report report, string outcome) =
                await BuildAsync(cancellationToken).ConfigureAwait(false);
            _memo.Remember(_clock, report, outcome);
            Tag(span, outcome);
            return report;
        }
        finally
        {
            _memo.Exit();
        }
    }

    private RecorderHealthReport.Report Stamp(RecorderHealthReport.Report report, DateTimeOffset cachedAt)
    {
        long ageMs = (long)(_clock.GetUtcNow() - cachedAt).TotalMilliseconds;
        return report with { CachedAtUtc = cachedAt, AgeMs = ageMs };
    }

    private async Task<(RecorderHealthReport.Report Report, string Outcome)> BuildAsync(
        CancellationToken cancellationToken)
    {
        DateTimeOffset asOf = _clock.GetUtcNow();
        TapeAvailability process = _tape.Value;
        bool feedConnected = process.Reason is TapeUnavailableReason.None
            or TapeUnavailableReason.ConnectedButNotSubscribed;

        ProductSession? open = ProductSessions.Containing(_calendar, asOf);
        ProductSession? named = open ?? ProductSessions.LatestClosed(_calendar, asOf);
        Window? window = named is { } session && Horizon(asOf, session) > session.OpenUtc
            ? new Window(session.OpenUtc, Horizon(asOf, session))
            : null;

        string[] symbols = _instruments.Instruments.Select(instrument => instrument.Symbol).ToArray();
        Probe probe;
        IReadOnlyList<ContractLastTick> ticks = [];
        IReadOnlyList<CoverageSlice> slices = [];
        using CancellationTokenSource deadline = new();
        using ITimer timeout = _clock.CreateTimer(
            static state => ((CancellationTokenSource)state!).Cancel(),
            deadline,
            ProbeTimeout,
            Timeout.InfiniteTimeSpan);
        using (CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token))
        {
            try
            {
                probe = await ProbeAsync(linked.Token).ConfigureAwait(false);
                if (probe == Probe.Up && window is { } elapsed)
                {
                    ticks = await _store.LastTicksAsync(symbols, elapsed.Start, elapsed.End, linked.Token)
                        .ConfigureAwait(false);
                    slices = await _store.CoverageAsync(symbols, elapsed.Start, elapsed.End, linked.Token)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                probe = Probe.TimedOut;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                probe = Probe.Down;
            }
        }

        if (probe != Probe.Up)
        {
            ticks = [];
            slices = [];
        }

        bool databaseUp = probe == Probe.Up;
        IReadOnlyList<RecorderHealthReport.ContractStatus> contracts =
            BuildContracts(ticks, slices, databaseUp, window);
        IReadOnlyList<RecorderHealthReport.Gap>? gaps = databaseUp && named is { } reportedSession
            ? BuildGaps(reportedSession, asOf, slices, contracts)
            : null;

        string outcome = Outcome(probe, feedConnected, contracts, gaps);
        _telemetry.RecorderHealthRead(outcome);

        RecorderHealthReport.Report report = new(
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
        return (report, outcome);
    }

    private async Task<Probe> ProbeAsync(CancellationToken databaseToken)
    {
        try
        {
            bool up = await _store.DatabaseConnectedAsync(databaseToken).ConfigureAwait(false);
            return up ? Probe.Up : Probe.Down;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return Probe.Down;
        }
    }

    private IReadOnlyList<RecorderHealthReport.ContractStatus> BuildContracts(
        IReadOnlyList<ContractLastTick> ticks,
        IReadOnlyList<CoverageSlice> slices,
        bool databaseUp,
        Window? window)
    {
        Dictionary<string, Dictionary<string, DateTimeOffset?>> bySymbol = new(StringComparer.Ordinal);
        foreach (InstrumentId instrument in _instruments.Instruments)
        {
            bySymbol.TryAdd(instrument.Symbol, new Dictionary<string, DateTimeOffset?>(StringComparer.Ordinal));
        }

        if (databaseUp && window is { } elapsed)
        {
            foreach (ContractLastTick tick in ticks)
            {
                if (!TryContractsFor(bySymbol, tick.Instrument, out Dictionary<string, DateTimeOffset?>? contracts)
                    || tick.TradeTimeUtc < elapsed.Start
                    || tick.TradeTimeUtc > elapsed.End)
                {
                    continue;
                }

                if (!contracts.TryGetValue(tick.ContractId, out DateTimeOffset? existing)
                    || existing is null
                    || tick.TradeTimeUtc > existing)
                {
                    contracts[tick.ContractId] = tick.TradeTimeUtc;
                }
            }

            foreach (CoverageSlice slice in slices)
            {
                if (!TryContractsFor(bySymbol, slice.Instrument, out Dictionary<string, DateTimeOffset?>? contracts)
                    || slice.RangeStart >= elapsed.End
                    || slice.RangeEnd <= elapsed.Start)
                {
                    continue;
                }

                contracts.TryAdd(slice.ContractId, null);
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

    private static bool TryContractsFor(
        Dictionary<string, Dictionary<string, DateTimeOffset?>> bySymbol,
        string instrument,
        out Dictionary<string, DateTimeOffset?> contracts)
    {
        string symbol = new InstrumentId(instrument).Symbol;
        return bySymbol.TryGetValue(symbol, out contracts!);
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
        Probe probe,
        bool feedConnected,
        IReadOnlyList<RecorderHealthReport.ContractStatus> contracts,
        IReadOnlyList<RecorderHealthReport.Gap>? gaps)
    {
        if (probe == Probe.TimedOut)
        {
            return RecorderHealthOutcome.Timeout;
        }

        if (probe != Probe.Up)
        {
            return RecorderHealthOutcome.Unavailable;
        }

        if (feedConnected && gaps is { Count: 0 } && contracts.All(static row => row.Listening))
        {
            return RecorderHealthOutcome.Ok;
        }

        return RecorderHealthOutcome.Degraded;
    }

    private static void Tag(Activity? span, string outcome)
    {
        span?.SetTag(HostTelemetry.OutcomeTag, outcome);
        for (Activity? activity = span is null ? Activity.Current : span.Parent;
            activity is not null;
            activity = activity.Parent)
        {
            if (activity.Kind == ActivityKind.Server)
            {
                activity.SetTag(HostTelemetry.OutcomeTag, outcome);
                return;
            }
        }
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

    private enum Probe
    {
        Up,
        Down,
        TimedOut,
    }

    private readonly record struct Window(DateTimeOffset Start, DateTimeOffset End);
}
