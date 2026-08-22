using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.Venue;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// A gateway that serves bars from a script and <b>counts every call</b>.
/// </summary>
/// <remarks>
/// The counting is the point. The cache's central claim is that a repeated read costs zero vendor calls, and
/// that is not a claim a mocking framework's "verify called once" expresses well — the interesting assertion
/// is <i>exactly zero, on the second call, after a first call that did work</i>.
/// </remarks>
public sealed class CountingGateway : IMarketDataGateway
{
    private readonly Dictionary<DateTimeOffset, Bar> _available = [];

    /// <summary>Creates the fake with a set of bars the venue is willing to serve.</summary>
    /// <param name="available">The bars the venue holds.</param>
    public CountingGateway(IEnumerable<Bar> available)
    {
        foreach (Bar bar in available)
        {
            _available[bar.OpenTime] = bar;
        }
    }

    /// <inheritdoc />
    public string VenueId => "test";

    /// <summary>How many times bars have been requested.</summary>
    public int BarRequests { get; private set; }

    /// <summary>How many times contracts have been resolved.</summary>
    public int ContractRequests { get; private set; }

    /// <summary>Resets both counters, so a test can assert about one phase in isolation.</summary>
    public void ResetCounters()
    {
        BarRequests = 0;
        ContractRequests = 0;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<VenueContract>> ResolveContractsAsync(
        InstrumentId instrument,
        CancellationToken cancellationToken)
    {
        ContractRequests++;
        IReadOnlyList<VenueContract> contracts =
            [new VenueContract("CON.F.US.TEST.Z26", instrument, true, 0.25m, 12.50m)];
        return Task.FromResult(contracts);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Bar>> GetBarsAsync(
        string contractId,
        BarRange window,
        TimeSpan barSize,
        CancellationToken cancellationToken)
    {
        BarRequests++;
        IReadOnlyList<Bar> bars =
            [.. _available.Values.Where(b => window.Contains(b.OpenTime)).OrderBy(b => b.OpenTime)];
        return Task.FromResult(bars);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<VenueAccount>> GetAccountsAsync(
        bool onlyActive,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<VenueAccount>>([]);

    /// <inheritdoc />
    public Task<IReadOnlyList<VenuePosition>> GetOpenPositionsAsync(
        int accountId,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<VenuePosition>>([]);

    /// <inheritdoc />
    public Task<IReadOnlyList<VenueOrder>> GetOrdersAsync(
        int accountId,
        BarRange? window,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<VenueOrder>>([]);

    /// <inheritdoc />
    public Task<IReadOnlyList<VenueTrade>> GetTradesAsync(
        int accountId,
        BarRange window,
        CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<VenueTrade>>([]);
}
