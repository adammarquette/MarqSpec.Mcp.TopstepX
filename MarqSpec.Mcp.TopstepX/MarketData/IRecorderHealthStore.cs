namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>One contract's latest stored print.</summary>
/// <param name="Instrument">The normalised symbol.</param>
/// <param name="ContractId">The venue contract id.</param>
/// <param name="TradeTimeUtc">When the venue says that print occurred, UTC.</param>
public sealed record ContractLastTick(string Instrument, string ContractId, DateTimeOffset TradeTimeUtc);

/// <summary>One stored listening range, half-open.</summary>
/// <param name="Instrument">The normalised symbol.</param>
/// <param name="ContractId">The venue contract id.</param>
/// <param name="RangeStart">Inclusive.</param>
/// <param name="RangeEnd">Exclusive. <see cref="Data.Entities.TapeCoverageRecord.StillListeningEnd"/> while open.</param>
public sealed record CoverageSlice(
    string Instrument,
    string ContractId,
    DateTimeOffset RangeStart,
    DateTimeOffset RangeEnd);

/// <summary>
/// The store reads a recorder-health report needs. One implementation talks to the database;
/// tests supply the other.
/// </summary>
public interface IRecorderHealthStore
{
    /// <summary>
    /// Whether a connection can be opened now.
    /// </summary>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns><see langword="true"/> when the store answers.</returns>
    /// <remarks>
    /// A failure is <see langword="false"/>, not a thrown exception and not a message. The
    /// exception a driver raises can carry the connection target, and this report is handed to
    /// whoever holds a bearer token (gh#551). Cancellation still propagates.
    /// </remarks>
    Task<bool> DatabaseConnectedAsync(CancellationToken cancellationToken);

    /// <summary>The latest stored print per contract.</summary>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>One row per contract that has a print. Empty when the tape has none.</returns>
    Task<IReadOnlyList<ContractLastTick>> LastTicksAsync(CancellationToken cancellationToken);

    /// <summary>Listening ranges that overlap <c>[windowStart, windowEnd)</c>.</summary>
    /// <param name="windowStart">Inclusive.</param>
    /// <param name="windowEnd">Exclusive.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The overlapping ranges, including a still-open listen.</returns>
    Task<IReadOnlyList<CoverageSlice>> CoverageAsync(
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken);
}
