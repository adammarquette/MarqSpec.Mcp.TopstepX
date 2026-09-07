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
    /// <summary>The contract the single-series constructor puts every bar on.</summary>
    public const string DefaultContractId = "CON.F.US.TEST.Z26";

    private readonly Dictionary<string, Dictionary<DateTimeOffset, Bar>> _byContract =
        new(StringComparer.Ordinal);

    private readonly string _frontContractId;

    /// <summary>Creates the fake with a set of bars the venue is willing to serve.</summary>
    /// <param name="available">The bars the venue holds.</param>
    /// <remarks>
    /// The one-contract shape, and what nearly every test wants. It delegates to the multi-contract
    /// constructor with a single entry under <see cref="DefaultContractId"/>, so there is one implementation
    /// of the serving behaviour rather than two free to disagree (gh#387's lesson, applied inside the double).
    /// </remarks>
    public CountingGateway(IEnumerable<Bar> available)
        : this(
            new Dictionary<string, IEnumerable<Bar>>(StringComparer.Ordinal)
            {
                [DefaultContractId] = available,
            },
            DefaultContractId)
    {
    }

    /// <summary>
    /// Creates the fake with a <b>different series per contract</b>, as the roll policy needs (ADR-0020).
    /// </summary>
    /// <param name="byContract">The bars each contract holds, keyed by venue contract id.</param>
    /// <param name="frontContractId">
    /// The contract <see cref="ResolveContractsAsync"/> answers with — the venue's own pick, which for a
    /// historical range is precisely the contract that is <i>wrong</i>.
    /// </param>
    /// <remarks>
    /// <para>
    /// This is the shape the historical-contract work needs and the single-series one cannot express: the
    /// defect ADR-0020 fixes is that the venue-active contract answers a historical range with a thin,
    /// entirely plausible series while the contract that carried the volume answers a fat one. A double that
    /// serves the same bars whatever it is asked about cannot tell those two apart, so it cannot fail on the
    /// bug.
    /// </para>
    /// <para>
    /// <b>An unknown contract id answers empty, not the front's bars.</b> That is what the real venue does,
    /// and it is what makes a candidate that was never listed distinguishable from one that simply had no
    /// trades.
    /// </para>
    /// </remarks>
    public CountingGateway(IReadOnlyDictionary<string, IEnumerable<Bar>> byContract, string frontContractId)
    {
        ArgumentNullException.ThrowIfNull(byContract);
        ArgumentException.ThrowIfNullOrWhiteSpace(frontContractId);

        foreach ((string contractId, IEnumerable<Bar> bars) in byContract)
        {
            Dictionary<DateTimeOffset, Bar> series = new();
            foreach (Bar bar in bars)
            {
                series[bar.OpenTime] = bar;
            }

            _byContract[contractId] = series;
        }

        _frontContractId = frontContractId;
    }

    /// <inheritdoc />
    public string VenueId => "test";

    /// <summary>How many times bars have been requested.</summary>
    public int BarRequests { get; private set; }

    /// <summary>How many times contracts have been resolved.</summary>
    public int ContractRequests { get; private set; }

    /// <summary>
    /// How many times a contract has been looked up <b>by exact id</b>.
    /// </summary>
    /// <remarks>
    /// Counted separately from <see cref="ContractRequests"/> because the two are different vendor pools —
    /// search and lookup — and because the claim <c>ContractDirectory</c> makes is about this number
    /// specifically: repeated questions about one id cost exactly one.
    /// </remarks>
    public int ContractLookups { get; private set; }

    /// <summary>The contract ids this fake venue lists, in insertion order.</summary>
    public IReadOnlyCollection<string> KnownContracts => _byContract.Keys;

    /// <summary>Resets every counter, so a test can assert about one phase in isolation.</summary>
    public void ResetCounters()
    {
        BarRequests = 0;
        ContractRequests = 0;
        ContractLookups = 0;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<VenueContract>> ResolveContractsAsync(
        InstrumentId instrument,
        CancellationToken cancellationToken)
    {
        ContractRequests++;
        IReadOnlyList<VenueContract> contracts =
            [new VenueContract(_frontContractId, instrument, true, 0.25m, 12.50m)];
        return Task.FromResult(contracts);
    }

    /// <inheritdoc />
    public Task<VenueContract?> FindContractAsync(
        InstrumentId instrument,
        ContractExpiry expiry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expiry);

        ContractLookups++;

        // Matched on the EXPIRY the id carries rather than on a constructed string: the fake has no product
        // code table, and building one here would make the double disagree with the registry about which id
        // it lists. An id whose expiry cannot be read lists nothing, which is the honest answer.
        string? known = _byContract.Keys.FirstOrDefault(id =>
            ContractExpiry.TryParseContractId(id, out ContractExpiry listed) && listed == expiry);

        return Task.FromResult<VenueContract?>(
            known is null
                ? null
                : new VenueContract(
                    known,
                    instrument,
                    string.Equals(known, _frontContractId, StringComparison.Ordinal),
                    0.25m,
                    12.50m));
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Bar>> GetBarsAsync(
        string contractId,
        BarRange window,
        TimeSpan barSize,
        CancellationToken cancellationToken)
    {
        BarRequests++;

        if (!_byContract.TryGetValue(contractId, out Dictionary<DateTimeOffset, Bar>? available))
        {
            // A contract this fake venue does not list answers empty -- as the real one does.
            return Task.FromResult<IReadOnlyList<Bar>>([]);
        }

        // Stamped here, as a real gateway must: a history call answers for exactly one contract, and the
        // cache refuses bars that arrive without saying which (ADR-0011).
        IReadOnlyList<Bar> bars =
        [
            .. available.Values
                .Where(b => window.Contains(b.OpenTime))
                .OrderBy(b => b.OpenTime)
                .Select(b => b with { ContractId = contractId }),
        ];

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
