using MarqSpec.Client.ProjectX;
using MarqSpec.Client.ProjectX.Api.Models;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.Venue;

/// <summary>
/// The ProjectX/TopstepX gateway, behind the read-only seam.
/// </summary>
/// <remarks>
/// <para>
/// <b>This type calls no order method, and the CI gate proves it</b> (ADR-0002). The client underneath has a
/// complete order surface — this is the first point in the repository where those calls are actually
/// reachable, and it is precisely why <c>scripts/check-no-order-path.sh</c> exists.
/// </para>
/// <para>
/// Everything the vendor gets wrong in a <i>successful-looking</i> way is handled here rather than left to
/// callers: the empty-universe-on-wrong-tier, the 1000-bar silent truncation, and the timestamps that arrive
/// without a kind.
/// </para>
/// </remarks>
public sealed class ProjectXMarketDataGateway : IMarketDataGateway
{
    /// <summary>The most bars one history call may ask for before the gateway truncates silently.</summary>
    public const int MaxBarsPerRequest = 1_000;

    private readonly IProjectXApiClient _client;
    private readonly InstrumentRegistry _registry;
    private readonly bool _live;
    private readonly ILogger<ProjectXMarketDataGateway> _logger;

    /// <summary>Creates the gateway.</summary>
    /// <param name="client">The vendor client.</param>
    /// <param name="registry">The served instruments, carrying the product code each contract must match.</param>
    /// <param name="options">The venue options, carrying the required data tier.</param>
    /// <param name="logger">The logger.</param>
    public ProjectXMarketDataGateway(
        IProjectXApiClient client,
        InstrumentRegistry registry,
        IOptions<VenueOptions> options,
        ILogger<ProjectXMarketDataGateway> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _client = client;
        _registry = registry;
        _live = options.Value.DataTier == ProjectXDataTier.Live;
        _logger = logger;
    }

    /// <inheritdoc />
    public string VenueId => "projectx";

    /// <inheritdoc />
    public async Task<IReadOnlyList<VenueContract>> ResolveContractsAsync(
        InstrumentId instrument,
        CancellationToken cancellationToken)
    {
        IEnumerable<Contract> matches = await Guarded(
            () => _client.SearchContractsAsync(instrument.Symbol, _live, cancellationToken),
            "searching contracts for " + instrument.Symbol).ConfigureAwait(false);

        List<Contract> found = [.. matches];
        if (found.Count == 0)
        {
            // NOT an error from the gateway's point of view -- the wrong data tier returns an EMPTY universe
            // rather than a failure, so this is where the two possibilities have to be named together.
            _logger.LogWarning(
                "The venue returned no contracts for {Instrument} on the {Tier} tier. If this instrument is "
                + "listed, check ProjectX__DataTier: the wrong tier returns an empty universe, not an error.",
                instrument.Symbol,
                _live ? "Live" : "Simulated");
            return [];
        }

        // THE SEARCH IS FUZZY, AND EVERYTHING IT RETURNS IS FLAGGED ACTIVE.
        //
        // Observed live: searching "ES" returns EP (correct) alongside FVA (a Treasury note), JY6 (Japanese
        // Yen), MX6, TYA and MES -- six contracts, every one ActiveContract=true. Searching "YM" returns YM
        // and MYM, the full contract and the micro, whose point values differ by a factor of ten.
        //
        // So ActiveContract cannot select, and neither can list order. The product code is the only thing
        // that identifies the contract, and it is CHECKED rather than preferred: a request for ES that cannot
        // find an EP contract fails, instead of returning Yen bars that would be stored under ES and have
        // every indicator and key level computed from them.
        string productCode = _registry.ProductCodeFor(instrument);
        List<Contract> matching = [.. found.Where(c => HasProductCode(c.Id, productCode))];

        if (matching.Count == 0)
        {
            throw new VenueException(
                "The venue returned " + found.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " contract(s) for '" + instrument.Symbol + "' but none carries the expected product code '"
                + productCode + "'. The search is fuzzy, so those results are other instruments. Either the "
                + "venue changed this product's code -- verify it against a live search and update "
                + "InstrumentRegistry -- or the data tier is wrong.");
        }

        // A second, independent check. If a product code ever starts pointing at a different contract, the
        // tick size is what catches it, and a wrong tick size silently rescales every money figure.
        decimal expectedTick = _registry.SpecFor(instrument).TickSize;
        foreach (Contract candidate in matching.Where(c => c.TickSize != expectedTick))
        {
            throw new VenueException(
                "Contract '" + candidate.Id + "' matches the product code for '" + instrument.Symbol
                + "' but reports a tick size of "
                + candidate.TickSize.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " where this server expects "
                + expectedTick.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ". Refusing rather than pricing this instrument on the wrong scale.");
        }

        // Active first, then the nearest expiry -- the front month is what a caller asking for "ES" means.
        return
        [
            .. matching
                .OrderByDescending(c => c.ActiveContract)
                .ThenBy(c => c.Id, StringComparer.Ordinal)
                .Select(c => ProjectXMapping.ToContract(c, instrument)),
        ];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Bar>> GetBarsAsync(
        string contractId,
        BarRange window,
        TimeSpan barSize,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contractId);
        ArgumentNullException.ThrowIfNull(window);

        if (window.IsEmpty)
        {
            return [];
        }

        (AggregateBarUnit unit, int unitNumber) = ProjectXMapping.ToBarUnit(barSize);

        // Page here rather than trust the caller. The gateway caps a call at MaxBarsPerRequest and truncates
        // beyond it SILENTLY -- a response of exactly 1000 bars for a wider window is indistinguishable from a
        // complete answer, so a caller cannot detect the clipping even in principle.
        TimeSpan page = TimeSpan.FromTicks(MaxBarsPerRequest * barSize.Ticks);
        List<Bar> collected = [];

        for (DateTimeOffset from = window.Start; from < window.End; from += page)
        {
            DateTimeOffset to = from + page;
            if (to > window.End)
            {
                to = window.End;
            }

            IEnumerable<AggregateBar> bars = await Guarded(
                () => _client.GetHistoricalBarsAsync(
                    contractId,
                    from.UtcDateTime,
                    to.UtcDateTime,
                    unit,
                    unitNumber,
                    MaxBarsPerRequest,
                    _live,
                    false, // includePartialBar: never. The cache drops forming bars again anyway.
                    cancellationToken),
                "retrieving bars for " + contractId).ConfigureAwait(false);

            collected.AddRange(bars.Select(ProjectXMapping.ToBar));
        }

        // The gateway does not promise an order, and every indicator downstream is path-dependent. Sorting
        // here is cheaper than discovering a shuffled series as a wrong number later.
        return [.. collected.DistinctBy(b => b.OpenTime).OrderBy(b => b.OpenTime)];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<VenueAccount>> GetAccountsAsync(
        bool onlyActive,
        CancellationToken cancellationToken)
    {
        IEnumerable<TradingAccount> accounts = await Guarded(
            () => _client.GetAccountsAsync(onlyActive, cancellationToken),
            "listing accounts").ConfigureAwait(false);

        return [.. accounts.Select(ProjectXMapping.ToAccount)];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<VenuePosition>> GetOpenPositionsAsync(
        int accountId,
        CancellationToken cancellationToken)
    {
        IEnumerable<Position> positions = await Guarded(
            () => _client.GetOpenPositionsAsync(accountId, cancellationToken),
            "reading open positions").ConfigureAwait(false);

        return [.. positions.Select(ProjectXMapping.ToPosition)];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<VenueOrder>> GetOrdersAsync(
        int accountId,
        BarRange? window,
        CancellationToken cancellationToken)
    {
        IEnumerable<Order> orders = window is null
            ? await Guarded(
                () => _client.GetOpenOrdersAsync(accountId, cancellationToken),
                "reading open orders").ConfigureAwait(false)
            : await Guarded(
                () => _client.GetOrdersAsync(
                    accountId, window.Start.UtcDateTime, window.End.UtcDateTime, cancellationToken),
                "searching orders").ConfigureAwait(false);

        return [.. orders.Select(ProjectXMapping.ToOrder)];
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<VenueTrade>> GetTradesAsync(
        int accountId,
        BarRange window,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(window);

        IEnumerable<HalfTrade> trades = await Guarded(
            () => _client.GetTradesAsync(
                accountId, window.Start.UtcDateTime, window.End.UtcDateTime, cancellationToken),
            "searching trades").ConfigureAwait(false);

        return [.. trades.Select(ProjectXMapping.ToTrade)];
    }

    /// <summary>
    /// Whether a contract id carries a product code, as <c>CON.F.US.{code}.{expiry}</c>.
    /// </summary>
    /// <param name="contractId">The venue contract id.</param>
    /// <param name="productCode">The expected product code.</param>
    /// <returns><see langword="true"/> when the id's product segment matches exactly.</returns>
    /// <remarks>
    /// Segment equality, not a substring test. A <c>Contains</c> check for <c>ES</c> would match <c>MES</c>,
    /// and one for <c>CL</c> would match <c>MCLE</c> — selecting a micro contract for a full-size request,
    /// which is a tenfold error in every money figure and looks entirely plausible on a chart.
    /// </remarks>
    public static bool HasProductCode(string contractId, string productCode)
    {
        if (string.IsNullOrWhiteSpace(contractId))
        {
            return false;
        }

        // CON.F.US.{product}.{expiry} — the product is the second-to-last segment, which stays true even if
        // the venue ever lengthens the prefix.
        string[] segments = contractId.Split('.');
        return segments.Length >= 2 && string.Equals(segments[^2], productCode, StringComparison.Ordinal);
    }

    /// <summary>
    /// Runs a vendor call, translating its failures into one exception type with the vendor's numeric code.
    /// </summary>
    /// <remarks>
    /// The vendor's own message string is deliberately not carried through: it is free text on a channel a
    /// language model reads (ADR-0008), and the code carries the diagnostic value without the surface. What
    /// the caller gets instead is <i>what this server was doing</i>, which is more useful anyway.
    /// </remarks>
    private static async Task<T> Guarded<T>(Func<Task<T>> call, string what)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (MarqSpec.Client.ProjectX.Exceptions.ProjectXApiException ex)
        {
            // The vendor's STATUS CODE, never its message string -- the code carries the
            // diagnostic value without putting vendor free text on a channel a model reads.
            throw ex.StatusCode is { } code
                ? new VenueException("The gateway refused while " + what + ".", code)
                : new VenueException("The gateway refused while " + what + ".");
        }
        catch (MarqSpec.Client.ProjectX.Exceptions.AuthenticationException)
        {
            throw new VenueException(
                "The gateway rejected the credentials. Note that ProjectX__ApiKey is the USERNAME and "
                + "ProjectX__ApiSecret is the API key -- putting the key in both authenticates as a user who "
                + "does not exist, and the gateway reports that as a bare unknown error.");
        }
        catch (HttpRequestException ex)
        {
            throw new VenueException("The gateway could not be reached while " + what + ".", ex);
        }
    }
}
