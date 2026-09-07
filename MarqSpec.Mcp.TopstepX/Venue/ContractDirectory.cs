using System.Collections.Concurrent;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;

namespace MarqSpec.Mcp.TopstepX.Venue;

/// <summary>
/// A process-wide memo over "does the venue list this contract id?" (ADR-0020).
/// </summary>
/// <remarks>
/// <para>
/// A historical fetch builds one candidate id per listed month per range, and asks about the same handful of
/// ids over and over: every read of last March on MES is a question about <c>H26</c> and <c>M26</c>, and the
/// answer cannot change between one read and the next. This is where that question stops reaching the venue.
/// </para>
/// <para>
/// <b>The two answers do not keep the same way, and that asymmetry is the whole design.</b>
/// </para>
/// <list type="bullet">
/// <item>
/// A <b>positive</b> is permanent for the life of the process. A contract the venue lists does not stop
/// existing — it expires, and an expired contract still answers by id with its full hourly history.
/// </item>
/// <item>
/// A <b>negative</b> is stamped and re-asked after <see cref="NegativeLifetime"/>. "The venue does not know
/// this id" is a fact with a shelf life: an expiry far enough out has simply not listed <i>yet</i>, and a
/// contract that begins listing at noon would otherwise stay invisible until the process restarts. An hour
/// is short enough that a listing is picked up the same session and long enough that a range fetched
/// repeatedly costs one lookup, not one per read.
/// </item>
/// </list>
/// <para>
/// <b>The gateway is passed in per call rather than injected.</b> This is a singleton — the vendor counts
/// requests against the credential, not against a request scope, so a per-scope memo would ask the same
/// question once per concurrent tool call — while <see cref="IMarketDataGateway"/> is scoped. Taking the
/// gateway as an argument is what keeps the lifetimes honest; holding one would be a captive dependency.
/// </para>
/// <para>
/// <b>What this does not do.</b> It does not single-flight: two callers racing on a cold id both ask the
/// venue, and both write the same answer. That costs one extra lookup out of a pool of 200 per 60 seconds
/// and can never produce a wrong answer, which is a better trade than a lock on the read path. It also
/// caches nothing about <i>bars</i> — only whether the contract exists.
/// </para>
/// </remarks>
public sealed class ContractDirectory
{
    /// <summary>How long a "the venue does not list this" answer is trusted before it is asked again.</summary>
    public static readonly TimeSpan NegativeLifetime = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    /// <summary>Creates the directory.</summary>
    /// <param name="clock">The clock the negative stamps are read against.</param>
    public ContractDirectory(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        _clock = clock;
    }

    /// <summary>How many entries the memo currently holds. Diagnostics only.</summary>
    public int Count => _entries.Count;

    /// <summary>
    /// The contract for one instrument and expiry, from the memo when it holds a usable answer and from the
    /// venue otherwise.
    /// </summary>
    /// <param name="gateway">The gateway to ask, when the memo cannot answer.</param>
    /// <param name="instrument">The instrument.</param>
    /// <param name="expiry">The expiry.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The contract, or <see langword="null"/> when the venue does not list it.</returns>
    /// <exception cref="ArgumentNullException">The gateway or the expiry is null.</exception>
    /// <remarks>
    /// A failure is <b>not</b> memoised. If the lookup throws, nothing is written and the next caller asks
    /// again — a transient refusal recorded as "does not exist" would be exactly the plausible-looking
    /// absence this server is built to refuse.
    /// </remarks>
    public async Task<VenueContract?> FindAsync(
        IMarketDataGateway gateway,
        InstrumentId instrument,
        ContractExpiry expiry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(expiry);

        // Keyed on the instrument AND the expiry. Every product shares the same expiry codes, so a key of
        // "U26" alone would answer an MES question with the ES contract -- the micro-for-full-size confusion
        // HasProductCode exists to prevent, arriving through the cache instead of through the venue.
        string key = instrument.Symbol + "." + expiry.Code;

        if (_entries.TryGetValue(key, out Entry? cached) && !cached.IsStale(_clock.GetUtcNow()))
        {
            return cached.Contract;
        }

        VenueContract? contract =
            await gateway.FindContractAsync(instrument, expiry, cancellationToken).ConfigureAwait(false);

        _entries[key] = new Entry(contract, _clock.GetUtcNow());
        return contract;
    }

    /// <summary>One remembered answer, and when it was remembered.</summary>
    /// <param name="Contract">The contract, or null when the venue does not list the id.</param>
    /// <param name="RecordedAt">When the venue answered.</param>
    private sealed record Entry(VenueContract? Contract, DateTimeOffset RecordedAt)
    {
        /// <summary>Whether this answer must be asked again.</summary>
        /// <param name="now">The current instant.</param>
        /// <returns><see langword="true"/> when a negative answer has outlived its stamp.</returns>
        public bool IsStale(DateTimeOffset now) =>
            Contract is null && now - RecordedAt >= NegativeLifetime;
    }
}
