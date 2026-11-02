using MarqSpec.Mcp.TopstepX.MarketData;

namespace MarqSpec.Mcp.TopstepX.Tools;

/// <summary>
/// The recorder-health payload shared by <c>get_recorder_health</c> and <c>GET /health/recorder</c>
/// (R-5.14, gh#756).
/// </summary>
/// <remarks>
/// Numbers, timestamps, booleans and enum names only (ADR-0008). A missing print time and an unreadable
/// ledger are omitted, never filled in. <c>hubReconnects</c> counts a successful connect after a
/// loss or a failed attempt. A clean first connect is zero, and zero is a real count.
/// <c>lastTickAgeSeconds</c> is the wall-clock age, clamped at zero, and omitted with the print time.
/// A reply this process reused keeps that age against the cached <c>asOfUtc</c>, and carries
/// <c>cachedAtUtc</c> and <c>ageMs</c>. A reply that was just built omits both. Nothing here
/// carries a connection target or a sentence.
/// </remarks>
public static class RecorderHealthReport
{
    /// <summary>Whether the live database probe opened a connection.</summary>
    public enum StoreConnectivity
    {
        /// <summary>The probe connected.</summary>
        Available = 0,

        /// <summary>The probe did not connect, or a read of the ledger failed.</summary>
        Unavailable = 1,
    }

    /// <summary>One report.</summary>
    /// <param name="AsOfUtc">When the report was taken, UTC.</param>
    /// <param name="FeedConnected">
    /// <see langword="true"/> when this process's hub is up, or when this process is not the
    /// recorder and another process holds a live tape lease.
    /// </param>
    /// <param name="Feed">
    /// The tape reason. <see cref="TapeUnavailableReason.RecordedElsewhere"/> is another process's
    /// live lease. <see cref="TapeUnavailableReason.Stopped"/> after <c>NeverStarted</c> is a
    /// lapsed lease. Not a sentence.
    /// </param>
    /// <param name="Database">Whether the live probe connected.</param>
    /// <param name="Session">The elapsed session, or omitted when the calendar cannot name one.</param>
    /// <param name="Contracts">One row per configured symbol, plus each stored contract the probe could read.</param>
    /// <param name="Gaps">
    /// Holes in the elapsed session. Omitted when the ledger could not be read. Empty when the window is
    /// covered.
    /// </param>
    /// <param name="HubReconnects">
    /// Successful market-hub connects after a connection was lost or a connect attempt failed.
    /// A clean first connect is zero, and zero is a real count.
    /// </param>
    /// <param name="CachedAtUtc">
    /// When this process stored the report. Omitted when the reply was just built.
    /// </param>
    /// <param name="AgeMs">
    /// How old the stored report is at this read, in milliseconds. Omitted when the reply was just built.
    /// </param>
    /// <param name="TapeDaysKept">
    /// Trades receipts retention set back to pending because the source count no longer matched,
    /// still waiting for a new export. Zero is a real count. Omitted when the database probe did not connect.
    /// </param>
    /// <param name="BarBackfill">
    /// The last bar-backfill pass this process finished. Omitted when it has not finished one.
    /// </param>
    public sealed record Report(
        DateTimeOffset AsOfUtc,
        bool FeedConnected,
        TapeUnavailableReason Feed,
        StoreConnectivity Database,
        Session? Session,
        IReadOnlyList<ContractStatus> Contracts,
        IReadOnlyList<Gap>? Gaps,
        int HubReconnects,
        DateTimeOffset? CachedAtUtc = null,
        long? AgeMs = null,
        int? TapeDaysKept = null,
        BarBackfill? BarBackfill = null);

    /// <summary>The last bar-backfill pass this process finished (gh#899).</summary>
    /// <param name="StartedUtc">When the pass started, UTC.</param>
    /// <param name="EndedUtc">When it finished, UTC.</param>
    /// <param name="StopReason">A closed tag, not a sentence.</param>
    /// <param name="Pages">Pages the cache was asked for. Zero is a real count.</param>
    /// <param name="Errors">Failures counted for the pass. Zero is a real count.</param>
    public sealed record BarBackfill(
        DateTimeOffset StartedUtc,
        DateTimeOffset EndedUtc,
        string StopReason,
        int Pages,
        int Errors);

    /// <summary>The product session the gaps were measured against.</summary>
    /// <param name="TradeDate">The trade date the calendar assigns.</param>
    /// <param name="OpenUtc">When the session opened, UTC.</param>
    /// <param name="CloseUtc">When the session closes, UTC.</param>
    /// <param name="InSession"><see langword="true"/> when <see cref="Report.AsOfUtc"/> falls inside it.</param>
    public sealed record Session(
        DateOnly TradeDate,
        DateTimeOffset OpenUtc,
        DateTimeOffset CloseUtc,
        bool InSession);

    /// <summary>One symbol's tape, and the latest stored print when one exists.</summary>
    /// <param name="Symbol">The normalised symbol.</param>
    /// <param name="ContractId">The venue contract, or omitted when none is stored.</param>
    /// <param name="Listening">Whether this process is subscribed to the symbol now.</param>
    /// <param name="Tape">This symbol's tape reason. Not a sentence.</param>
    /// <param name="LastTickUtc">The latest stored print time, or omitted when no print is stored.</param>
    /// <param name="LastTickAgeSeconds">
    /// Wall-clock <see cref="Report.AsOfUtc"/> minus <see cref="LastTickUtc"/>, in seconds, clamped
    /// at zero. Omitted when <see cref="LastTickUtc"/> is omitted. Zero is a real age. A reply reused
    /// from the five-second memo keeps this age against that reply's <c>asOfUtc</c>.
    /// </param>
    public sealed record ContractStatus(
        string Symbol,
        string? ContractId,
        bool Listening,
        TapeUnavailableReason Tape,
        DateTimeOffset? LastTickUtc,
        double? LastTickAgeSeconds);

    /// <summary>One hole in the elapsed session, half-open <c>[FromUtc, ToUtc)</c>.</summary>
    /// <param name="Symbol">The normalised symbol.</param>
    /// <param name="ContractId">The venue contract, or omitted when the symbol has none stored.</param>
    /// <param name="FromUtc">Inclusive.</param>
    /// <param name="ToUtc">Exclusive.</param>
    public sealed record Gap(
        string Symbol,
        string? ContractId,
        DateTimeOffset FromUtc,
        DateTimeOffset ToUtc);
}
