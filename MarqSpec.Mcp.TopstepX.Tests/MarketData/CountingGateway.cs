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
    /// <para>
    /// <b>The front must be one of the keys.</b> <see cref="ResolveContractsAsync"/> lists what it holds
    /// front first by ordering on <see cref="IsFront"/>, so a front the dictionary does not hold leaves
    /// every key tied: the sort is stable, and <c>contracts[0]</c> becomes whichever contract went in
    /// first — a contract the test never named as the front, chosen by insertion order. A test built to
    /// pin "the venue front is the thin one" would then pin nothing and pass or fail for an unrelated
    /// reason. Refused here, while the double is still cheap to change.
    /// </para>
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// When <paramref name="frontContractId"/> is blank, or is not a key of <paramref name="byContract"/>.
    /// </exception>
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

        if (!_byContract.ContainsKey(frontContractId))
        {
            throw new ArgumentException(
                "The front contract '" + frontContractId + "' is not one this gateway holds. It holds: "
                + string.Join(", ", _byContract.Keys)
                + ". A front that is not a key is never listed first, so contracts[0] would silently be "
                + "another contract.",
                nameof(frontContractId));
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

    /// <summary>
    /// Whether the venue lists the instrument at all. Set <see langword="false"/> for the empty universe.
    /// </summary>
    /// <remarks>
    /// An empty contract list is what the <b>wrong market-data tier</b> looks like on this gateway — ProjectX
    /// answers a question about an instrument it cannot see with no contracts rather than with an error
    /// (gh#504). It is a venue condition rather than a bar script, so it is a switch here rather than a
    /// second double.
    /// </remarks>
    public bool ListsTheInstrument { get; set; } = true;

    /// <summary>
    /// Expiry codes <see cref="FindContractAsync"/> answers <see langword="null"/> for, whatever bars this
    /// fake holds under them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A venue negative is not the same thing as a contract with no bars</b> (gh#570). Leaving a contract
    /// out of the constructor makes it unlistable <i>and</i> barless at once, so a case about what happens
    /// when the existence check fails cannot also show the candidate that <i>would</i> have won. This switch
    /// separates them: the bars stay scripted, and the lookup refuses.
    /// </para>
    /// <para>
    /// Mutable, and deliberately: <c>ContractDirectory</c> re-asks a negative after its
    /// <c>NegativeLifetime</c>, and the only way to pin that the re-ask changes the answer is for the venue
    /// to change its mind mid-test.
    /// </para>
    /// </remarks>
    public ISet<string> Unlisted { get; } = new HashSet<string>(StringComparer.Ordinal);

    /// <inheritdoc />
    /// <remarks>
    /// <b>It lists everything it holds, front first</b> — not the front alone. A double that lists one
    /// contract however many it was built with cannot express a <i>roll window</i>, which is the only time
    /// the venue lists two expiries of one product (<c>InFrontMonthOrder</c>), and a caller that mistakes
    /// the whole listing for the set of contracts it may consult is invisible to it. The single-series
    /// constructor holds exactly one contract, so it answers exactly as it always did.
    /// </remarks>
    public Task<IReadOnlyList<VenueContract>> ResolveContractsAsync(
        InstrumentId instrument,
        CancellationToken cancellationToken)
    {
        ContractRequests++;
        IReadOnlyList<VenueContract> contracts = ListsTheInstrument
            ?
            [
                .. _byContract.Keys
                    .OrderByDescending(id => IsFront(id))
                    .Select(id => new VenueContract(id, instrument, IsFront(id), 0.25m, 12.50m)),
            ]
            : [];
        return Task.FromResult(contracts);
    }

    /// <summary>Whether an id is the one the venue marks active.</summary>
    /// <param name="contractId">The contract id.</param>
    /// <returns><see langword="true"/> when it is the front.</returns>
    private bool IsFront(string contractId) =>
        string.Equals(contractId, _frontContractId, StringComparison.Ordinal);

    /// <inheritdoc />
    public Task<VenueContract?> FindContractAsync(
        InstrumentId instrument,
        ContractExpiry expiry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expiry);

        ContractLookups++;

        if (Unlisted.Contains(expiry.Code))
        {
            // Counted first, then refused. The lookup DID reach the venue -- a negative costs a call exactly
            // as a positive does, and a double that answered null for free would hide the cost the directory
            // exists to bound.
            return Task.FromResult<VenueContract?>(null);
        }

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
