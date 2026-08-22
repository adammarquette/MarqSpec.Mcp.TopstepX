using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;

namespace MarqSpec.Mcp.TopstepX.Venue;

/// <summary>
/// The gateway that is registered while there is no real one — every call fails, loudly and with the reason.
/// </summary>
/// <remarks>
/// <para>
/// The ProjectX adapter is blocked on a client release (gh#13): nuget.org carries a version that predates the
/// integer-enum serialization fix, without which every bar retrieval returns 400.
/// </para>
/// <para>
/// <b>Why a failing implementation rather than no registration at all.</b> With nothing registered, the host
/// would fail at startup and an MCP client could not even list the tools — so the surface would be
/// unreviewable, and the many tools that touch no venue at all (instruments, session state, indicator reads
/// over stored bars, observations) would be unusable for a reason unrelated to them. This way the server
/// starts, the tool list is real, and only the calls that genuinely need the venue fail — each with a message
/// naming the actual blocker rather than a null reference.
/// </para>
/// <para>
/// It is deliberately not called a "fake" or a "stub". It is not a test double and nothing should be tested
/// against it; it is a placeholder that refuses.
/// </para>
/// </remarks>
public sealed class UnconfiguredMarketDataGateway : IMarketDataGateway
{
    private const string Explanation =
        "No venue gateway is configured. The ProjectX adapter is waiting on a release of "
        + "MarqSpec.Client.ProjectX: nuget.org carries a version predating the integer-enum serialization "
        + "fix, without which every bar retrieval returns HTTP 400. See gh#13. "
        + "Tools that do not touch the venue — list_instruments, get_market_session, get_indicators over "
        + "already-stored bars, and the observation tools — work normally.";

    /// <inheritdoc />
    public string VenueId => "projectx";

    /// <inheritdoc />
    public Task<IReadOnlyList<VenueContract>> ResolveContractsAsync(
        InstrumentId instrument,
        CancellationToken cancellationToken) => throw new VenueException(Explanation);

    /// <inheritdoc />
    public Task<IReadOnlyList<Bar>> GetBarsAsync(
        string contractId,
        BarRange window,
        TimeSpan barSize,
        CancellationToken cancellationToken) => throw new VenueException(Explanation);

    /// <inheritdoc />
    public Task<IReadOnlyList<VenueAccount>> GetAccountsAsync(
        bool onlyActive,
        CancellationToken cancellationToken) => throw new VenueException(Explanation);

    /// <inheritdoc />
    public Task<IReadOnlyList<VenuePosition>> GetOpenPositionsAsync(
        int accountId,
        CancellationToken cancellationToken) => throw new VenueException(Explanation);

    /// <inheritdoc />
    public Task<IReadOnlyList<VenueOrder>> GetOrdersAsync(
        int accountId,
        BarRange? window,
        CancellationToken cancellationToken) => throw new VenueException(Explanation);

    /// <inheritdoc />
    public Task<IReadOnlyList<VenueTrade>> GetTradesAsync(
        int accountId,
        BarRange window,
        CancellationToken cancellationToken) => throw new VenueException(Explanation);
}
