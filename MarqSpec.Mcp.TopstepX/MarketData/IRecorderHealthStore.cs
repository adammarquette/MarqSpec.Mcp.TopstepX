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

    /// <summary>
    /// The latest stored print per contract inside <c>[windowStart, windowEnd]</c>, for the named instruments.
    /// </summary>
    /// <param name="instruments">The configured symbols. A print for any other symbol is not read.</param>
    /// <param name="windowStart">Inclusive.</param>
    /// <param name="windowEnd">Inclusive. The caller passes the report instant, and a print at that instant has happened.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>
    /// One row per contract that printed inside the window. Empty when none did, or the window is empty.
    /// </returns>
    /// <remarks>
    /// Bounded on purpose. The latest print in the whole tape is a scan of every row ever stored, and a
    /// health poll is not a reason to do that (gh#756).
    /// </remarks>
    Task<IReadOnlyList<ContractLastTick>> LastTicksAsync(
        IReadOnlyCollection<string> instruments,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken);

    /// <summary>Listening ranges for the named instruments that overlap <c>[windowStart, windowEnd)</c>.</summary>
    /// <param name="instruments">The configured symbols. A range for any other symbol is not read.</param>
    /// <param name="windowStart">Inclusive.</param>
    /// <param name="windowEnd">Exclusive.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The overlapping ranges, including a still-open listen.</returns>
    Task<IReadOnlyList<CoverageSlice>> CoverageAsync(
        IReadOnlyCollection<string> instruments,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        CancellationToken cancellationToken);

    /// <summary>
    /// Trades receipts retention set back to pending because the source count no longer matched.
    /// One per day, venue, instrument and contract. Zero is a real count.
    /// </summary>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>How many such receipts are still waiting for a new export.</returns>
    Task<int> TapeDaysKeptAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Trades rows recorded after the uploaded receipt's export time. The receipt stays uploaded,
    /// so the Drive file is not replaced. Zero is a real count.
    /// </summary>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>How many such rows are still stored.</returns>
    Task<int> TapeLateRowsAsync(CancellationToken cancellationToken);
}
