using System.Globalization;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// The closed vocabulary of indicators this server computes and serves, and every period it computes each of
/// them at.
/// </summary>
/// <remarks>
/// <para>
/// One place declares the set, so the projection and the tool surface cannot disagree by construction. An
/// indicator the projection computes but the tools reject is invisible; one the tools accept but the
/// projection never writes reads back as an empty series, which an agent will interpret as "no signal".
/// </para>
/// <para>
/// The vocabulary is <b>closed</b> deliberately. An unknown name is an error listing the known ones, because a
/// typo that returns no data is indistinguishable from a market that produced none.
/// </para>
/// <para>
/// <b>Names and instances are different sets, and every caller wants one or the other.</b> A name has one
/// PRIMARY period — the singular <c>Indicators__*Period</c> key — and may have additional ones beside it.
/// So <see cref="All"/> is every configured <c>(name, period)</c> instance, because the projection has to
/// write all of them; <see cref="Primaries"/> is exactly one per name, because
/// <c>get_market_snapshot</c>'s indicator map is keyed by name and a map built by walking <see cref="All"/>
/// would let whichever instance came last win. That is not a smaller answer, it is a wrong one: the same
/// name over a different window, with nothing in the payload saying so. <see cref="KnownNames"/> is the
/// vocabulary and is unchanged by any amount of widening.
/// </para>
/// </remarks>
public sealed class IndicatorCatalog
{
    private readonly Dictionary<string, IIndicator> _byName;
    private readonly Dictionary<(string Name, int Period), IIndicator> _byKey;
    private readonly Dictionary<string, IReadOnlyList<int>> _periodsByName;

    /// <summary>Builds the catalogue from the configured periods.</summary>
    /// <param name="options">The indicator options.</param>
    /// <param name="calendar">The session calendar — VWAP is anchored to a session, so it needs one.</param>
    /// <exception cref="ArgumentException">
    /// Two instances would share a <c>(name, period)</c>. Options bound at startup cannot produce that —
    /// <see cref="IndicatorOptions.Validate"/> refuses a repeated period and <c>ValidateOnStart</c> makes it
    /// a boot failure — but options built by hand can, and that pair is a storage key.
    /// </exception>
    public IndicatorCatalog(IOptions<IndicatorOptions> options, BarSessionCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(calendar);

        IndicatorOptions o = options.Value;

        // Built once and listed by its own period rather than by a literal 0: VWAP is anchored, so there is
        // one instance of it however many periods anything else is configured at.
        VwapIndicator vwap = new(calendar);

        // One entry per FAMILY: the configured periods, primary first, and what one period builds. MACD and
        // Bollinger build three instances from one period each, because a slow length that produced a line
        // but no signal, or an upper band with no middle, would answer the other reads with an empty series.
        // The order is the projection order this catalogue has always had, and the primaries come out of it
        // in that same order.
        (IReadOnlyList<int> Periods, Func<int, IIndicator[]> Build)[] families =
        [
            (o.AtrPeriods(), period => [new AtrIndicator(period)]),
            (o.RsiPeriods(), period => [new RsiIndicator(period)]),
            (o.SmaPeriods(), period => [new SmaIndicator(period)]),
            (o.EmaPeriods(), period => [new EmaIndicator(period)]),
            (o.MacdSlowPeriods(), period =>
            [
                new MacdLineIndicator(period),
                new MacdSignalIndicator(period),
                new MacdHistogramIndicator(period),
            ]),
            // VWAP is anchored, not windowed: one instance, and it takes no period from a caller either.
            ([vwap.Period], _ => [vwap]),
            (o.BollingerPeriods(), period =>
            [
                new BollingerUpperIndicator(period),
                new BollingerMiddleIndicator(period),
                new BollingerLowerIndicator(period),
            ]),
            (o.RollingVwapPeriods(), period => [new RollingVwapIndicator(period)]),
        ];

        List<IIndicator> primaries = [];
        List<IIndicator> additional = [];

        foreach ((IReadOnlyList<int> periods, Func<int, IIndicator[]> build) in families)
        {
            for (int i = 0; i < periods.Count; i++)
            {
                (i == 0 ? primaries : additional).AddRange(build(periods[i]));
            }
        }

        IIndicator[] all = [.. primaries, .. additional];

        _byName = primaries.ToDictionary(i => i.Name, StringComparer.Ordinal);
        _byKey = new Dictionary<(string, int), IIndicator>(all.Length);

        foreach (IIndicator indicator in all)
        {
            if (!_byKey.TryAdd((indicator.Name, indicator.Period), indicator))
            {
                throw new ArgumentException(
                    "The catalogue would hold two instances of '" + indicator.Name + "' at period "
                    + indicator.Period.ToString(CultureInfo.InvariantCulture)
                    + ". (Indicator, Period) is the storage key, so the second would overwrite the first on "
                    + "every bar, under a row that names neither window.",
                    nameof(options));
            }
        }

        // Primary first, then the additional ones in configured order — because All is built that way and
        // GroupBy preserves both the first-seen order of the groups and the order within each of them.
        _periodsByName = all
            .GroupBy(i => i.Name, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                IReadOnlyList<int> (g) => [.. g.Select(i => i.Period)],
                StringComparer.Ordinal);

        Primaries = primaries;
        All = all;
    }

    /// <summary>
    /// Every configured instance: the primaries in projection order, then every additional period.
    /// </summary>
    /// <remarks>
    /// What the projection walks, so it carries every <c>(name, period)</c> an operator configured. A caller
    /// that wants one reading per NAME wants <see cref="Primaries"/> instead.
    /// </remarks>
    public IReadOnlyList<IIndicator> All { get; }

    /// <summary>Exactly one instance per name — the primary period each name is served at.</summary>
    /// <remarks>
    /// The snapshot's indicator map is keyed by name, and this is the list that keys it. Walking
    /// <see cref="All"/> there would let a later instance of a name overwrite an earlier one, so a server
    /// with an additional EMA configured would publish EMA at the wrong window with nothing saying so.
    /// </remarks>
    public IReadOnlyList<IIndicator> Primaries { get; }

    /// <summary>The known indicator names, for an error message that is actually useful.</summary>
    /// <remarks>Names, not instances: additional periods never widen this.</remarks>
    public IEnumerable<string> KnownNames => _byName.Keys.Order(StringComparer.Ordinal);

    /// <summary>
    /// Resolves an indicator name to the primary instance, or throws naming the valid ones.
    /// </summary>
    /// <param name="name">The indicator name, case-insensitive on input and lowercase in storage.</param>
    /// <returns>The indicator at its primary period.</returns>
    /// <exception cref="KeyNotFoundException">The name is not in the vocabulary.</exception>
    /// <remarks>
    /// Unchanged: this has always meant "the configured one", and every existing call site means that.
    /// Selecting among the configured periods is <see cref="Resolve(string, int?)"/>.
    /// </remarks>
    public IIndicator Resolve(string name)
    {
        string normalised = (name ?? string.Empty).Trim().ToLowerInvariant();
        return _byName.TryGetValue(normalised, out IIndicator? indicator)
            ? indicator
            : throw new KeyNotFoundException(
                "Unknown indicator '" + normalised + "'. Known indicators: "
                + string.Join(", ", KnownNames) + ".");
    }

    /// <summary>
    /// Resolves an indicator name and a chosen period to the instance this server computes.
    /// </summary>
    /// <param name="name">The indicator name, case-insensitive on input and lowercase in storage.</param>
    /// <param name="period">The chosen period, or <see langword="null"/> for the primary.</param>
    /// <returns>The indicator.</returns>
    /// <exception cref="KeyNotFoundException">
    /// The name is not in the vocabulary, the name is <c>vwap</c> and a period was given at all, or the
    /// period is not one this server computes that indicator at.
    /// </exception>
    /// <remarks>
    /// <para>
    /// A period SELECTS among what is configured; it never asks for a new one. A period this server does not
    /// compute would read back as an empty series, and an empty series is indistinguishable from a market
    /// that produced none — so the refusal lists what IS configured and says which of those is the primary.
    /// </para>
    /// <para>
    /// The NAME is checked first. Telling a caller who typed <c>stochastic</c> that it "is not computed at
    /// period 14" would send them looking for a configuration key that does not exist.
    /// </para>
    /// </remarks>
    public IIndicator Resolve(string name, int? period)
    {
        IIndicator primary = Resolve(name);

        if (period is null)
        {
            return primary;
        }

        if (primary is VwapIndicator)
        {
            throw new KeyNotFoundException(
                "'" + primary.Name + "' takes no period: it is anchored to the session, not to a window. "
                + "Omit period.");
        }

        return _byKey.TryGetValue((primary.Name, period.Value), out IIndicator? indicator)
            ? indicator
            : throw new KeyNotFoundException(
                "Indicator '" + primary.Name + "' is not computed at period "
                + period.Value.ToString(CultureInfo.InvariantCulture) + ". Configured periods for "
                + primary.Name + ": " + DescribePeriods(_periodsByName[primary.Name])
                + ". A period this server does not compute would read back as an empty series, which is "
                + "indistinguishable from a market that produced none.");
    }

    /// <summary>
    /// The periods this catalogue computes an indicator at — the primary first, then the additional ones in
    /// configured order.
    /// </summary>
    /// <param name="name">The indicator name.</param>
    /// <returns>The configured periods, primary first.</returns>
    /// <exception cref="KeyNotFoundException">The name is not in the vocabulary.</exception>
    /// <remarks>
    /// Configured order, never sorted: the primary is the one a caller gets by omitting the period, and a
    /// sorted list would bury it wherever its value happened to fall.
    /// </remarks>
    public IReadOnlyList<int> PeriodsFor(string name) => _periodsByName[Resolve(name).Name];

    /// <summary>
    /// The PRIMARY period this catalogue is configured to compute an indicator at.
    /// </summary>
    /// <param name="name">The indicator name.</param>
    /// <returns>The primary period.</returns>
    /// <exception cref="KeyNotFoundException">The name is not in the vocabulary.</exception>
    /// <remarks>
    /// Exposed so a caller can ask for "the RSI" without knowing which period was configured. Asking for a
    /// period this server never computed would return an empty series that looks like missing market data.
    /// The additional periods, if any, are <see cref="PeriodsFor"/>.
    /// </remarks>
    public int ConfiguredPeriodFor(string name) => Resolve(name).Period;

    /// <summary>The configured periods as an operator reads them, with the primary labelled.</summary>
    /// <param name="periods">The periods, primary first.</param>
    /// <returns>e.g. <c>20 (primary), 10, 13</c>.</returns>
    private static string DescribePeriods(IReadOnlyList<int> periods) =>
        string.Join(
            ", ",
            periods.Select((p, i) =>
                p.ToString(CultureInfo.InvariantCulture) + (i == 0 ? " (primary)" : string.Empty)));
}
