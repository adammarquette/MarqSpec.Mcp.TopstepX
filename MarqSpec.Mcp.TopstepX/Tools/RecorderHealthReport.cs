using MarqSpec.Mcp.TopstepX.MarketData;

namespace MarqSpec.Mcp.TopstepX.Tools;

/// <summary>
/// The recorder-health payload shared by <c>get_recorder_health</c> and <c>GET /health/recorder</c>
/// (R-5.13, gh#756).
/// </summary>
/// <remarks>
/// Numbers, timestamps, booleans and enum names only (ADR-0008). A missing print time and an unreadable
/// ledger are omitted, never filled in. Nothing here carries a connection target or a sentence.
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
    /// <see langword="true"/> when the hub is up: listening, or connected and not yet subscribed.
    /// </param>
    /// <param name="Feed">The process-wide tape reason. Not a sentence.</param>
    /// <param name="Database">Whether the live probe connected.</param>
    /// <param name="Session">The elapsed session, or omitted when the calendar cannot name one.</param>
    /// <param name="Contracts">One row per configured symbol, plus each stored contract the probe could read.</param>
    /// <param name="Gaps">
    /// Holes in the elapsed session. Omitted when the ledger could not be read. Empty when the window is
    /// covered.
    /// </param>
    public sealed record Report(
        DateTimeOffset AsOfUtc,
        bool FeedConnected,
        TapeUnavailableReason Feed,
        StoreConnectivity Database,
        Session? Session,
        IReadOnlyList<ContractStatus> Contracts,
        IReadOnlyList<Gap>? Gaps);

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
    public sealed record ContractStatus(
        string Symbol,
        string? ContractId,
        bool Listening,
        TapeUnavailableReason Tape,
        DateTimeOffset? LastTickUtc);

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
