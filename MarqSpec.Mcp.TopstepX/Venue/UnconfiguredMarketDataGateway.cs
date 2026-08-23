using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;

namespace MarqSpec.Mcp.TopstepX.Venue;

/// <summary>
/// The gateway that is registered while there is no real one — every call fails, loudly and with the reason.
/// </summary>
/// <remarks>
/// <para>
/// Registered when the venue is <b>not configured</b> — no credentials, or no data tier. The real adapter
/// (<see cref="ProjectXMarketDataGateway"/>) takes its place as soon as both are present.
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
        "No venue credentials are configured, so nothing can be read from the broker. Set ProjectX__ApiKey "
        + "(your USERNAME), ProjectX__ApiSecret (your API KEY) and ProjectX__DataTier (Simulated or Live), "
        + "then restart this server. Note the first two names are inverted from what they read like: putting "
        + "the API key in both fields authenticates as a user who does not exist. "
        + "Tools that need no venue - list_instruments, get_market_session, and anything served from bars "
        + "already in the store - work normally.";

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
