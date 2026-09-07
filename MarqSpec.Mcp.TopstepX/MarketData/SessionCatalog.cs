using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// The closed vocabulary of named sessions this server aggregates and serves.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IndicatorCatalog"/>, one concept over. One place declares the set, so the aggregator and the
/// tool surface cannot disagree by construction: a session the aggregator writes but the tools reject is
/// invisible, and one the tools accept but nothing writes reads back as an empty series — which an agent
/// interprets as "no session".
/// </para>
/// <para>
/// The vocabulary is <b>closed</b> deliberately. An unknown name is an error listing the known ones, because a
/// typo answered with no data is indistinguishable from a session that produced none.
/// </para>
/// <para>
/// A name is a <b>storage key</b>: session bars carry the definition that produced them, so renaming one
/// orphans every row already written under the old name (ADR-0022 §4).
/// </para>
/// <para>
/// <b>Per-instrument overrides are out of scope.</b> Every instrument this server serves shares one set of
/// session definitions. The configuration shape admits per-instrument ones later without a key change —
/// <c>MarketData__Sessions__rth__Instruments__ES__Window</c> hangs off the same
/// <see cref="SessionOptions"/> entry — so nothing here has to be redesigned when they are wanted.
/// </para>
/// </remarks>
public sealed class SessionCatalog
{
    private readonly Dictionary<string, SessionDefinition> _byName;

    /// <summary>Builds the catalogue from the configured sessions.</summary>
    /// <param name="options">The market-data options.</param>
    /// <param name="calendar">The session calendar the definitions are stated against.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="FormatException">A configured window is not <c>HH:mm-HH:mm</c>.</exception>
    /// <exception cref="ArgumentException">
    /// A definition breaks one of <see cref="SessionWindows.Validate"/>'s rules. Options bound at startup
    /// cannot produce that — <see cref="MarketDataOptions.Validate"/> refuses it and <c>ValidateOnStart</c>
    /// makes it a boot failure — but options built by hand can, exactly as
    /// <see cref="IndicatorCatalog"/> re-checks the key its own options validation already refused.
    /// </exception>
    public SessionCatalog(IOptions<MarketDataOptions> options, BarSessionCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(calendar);

        IReadOnlyList<SessionDefinition> definitions = options.Value.SessionList();

        foreach (SessionDefinition definition in definitions)
        {
            SessionWindows.Validate(definition, calendar);
        }

        // Ordinal on an already-lowercase name: Validate above has just insisted every name matches
        // ^[a-z][a-z0-9-]{0,15}$, so the lowering is what makes that guarantee visible at the lookup rather
        // than a normalisation this type performs. Resolve lowers its argument to meet it.
        _byName = definitions.ToDictionary(
            definition => definition.Name.ToLowerInvariant(), StringComparer.Ordinal);

        All = definitions;
    }

    /// <summary>
    /// Every configured session: the shipped ones in their shipped order, then any added name in configured
    /// order.
    /// </summary>
    /// <remarks>What the aggregation pass walks, so it carries every name an operator configured.</remarks>
    public IReadOnlyList<SessionDefinition> All { get; }

    /// <summary>The known session names, for an error message that is actually useful.</summary>
    public IEnumerable<string> KnownNames => _byName.Keys.Order(StringComparer.Ordinal);

    /// <summary>
    /// Resolves a session name to its definition, or throws naming the valid ones.
    /// </summary>
    /// <param name="name">The session name, case-insensitive on input and lowercase in storage.</param>
    /// <returns>The definition.</returns>
    /// <exception cref="KeyNotFoundException">The name is not in the vocabulary.</exception>
    public SessionDefinition Resolve(string name)
    {
        string normalised = (name ?? string.Empty).Trim().ToLowerInvariant();
        return _byName.TryGetValue(normalised, out SessionDefinition? definition)
            ? definition
            : throw new KeyNotFoundException(
                "Unknown session '" + normalised + "'. Known sessions: "
                + string.Join(", ", KnownNames) + ".");
    }
}
