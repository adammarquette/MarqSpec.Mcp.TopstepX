using MarqSpec.Mcp.TopstepX.Domain.MarketData;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// The closed vocabulary of level-detection methods this server serves.
/// </summary>
/// <remarks>
/// <para>
/// The same shape as <see cref="IndicatorCatalog"/>, and for the same reason: one place declares the set, so
/// the tool surface and whatever computes cannot disagree by construction. The vocabulary is <b>closed</b>
/// deliberately — an unknown name is an error listing the known ones, because a typo that returned no levels
/// is indistinguishable from a market that has produced no structure, and the second reads as a conclusion.
/// </para>
/// <para>
/// <b>It is not the indicator projector's shape, deliberately.</b> That writes one <c>decimal</c> per
/// <c>(name, period, bucket)</c>. A zone has bounds, a kind, a touch count and a formation time — which is
/// why the level table nothing ever filled was given a synthetic key, until gh#276 dropped it — and levels
/// are computed on read rather than stored, so a method name is a request vocabulary rather than a storage
/// key. Nothing here writes anything.
/// </para>
/// <para>
/// <b>Every method in it must refuse a series that spans a contract roll</b> (<c>R-3.5</c>). That is a rule
/// each implementation satisfies rather than one the seam enforces, so it is swept over
/// <see cref="All"/> in <c>LevelMethodCatalogRollTests</c> rather than trusted: a method reached by a
/// different path loses the confinement without failing.
/// </para>
/// </remarks>
public sealed class LevelMethodCatalog
{
    private readonly Dictionary<string, ILevelMethod> _byName;

    /// <summary>Builds the catalogue.</summary>
    /// <remarks>
    /// <b>No configuration, and now by decision rather than by deferral (gh#244).</b>
    /// <see cref="ILevelMethod.Detect"/> takes its options <i>per call</i>, so a catalogue holding the
    /// configured defaults would hold a value it never reads. They live at the tool boundary instead —
    /// <c>MarketDataTools.ResolveDetection</c> — which is where "the caller did not say" becomes "the
    /// operator's configured value". That the options can be per-call at all is ADR-0013: levels are
    /// computed on read and nothing stores one, so there is no storage key for a parameter to fall out of.
    /// </remarks>
    public LevelMethodCatalog()
    {
        ILevelMethod[] methods =
        [
            new SwingLevelMethod(),
        ];

        _byName = methods.ToDictionary(m => m.Name, StringComparer.Ordinal);
        All = methods;
    }

    /// <summary>Every method, in registration order.</summary>
    public IReadOnlyList<ILevelMethod> All { get; }

    /// <summary>The known method names, for an error message that is actually useful.</summary>
    public IEnumerable<string> KnownNames => _byName.Keys.Order(StringComparer.Ordinal);

    /// <summary>
    /// Resolves a method name, or throws naming the valid ones.
    /// </summary>
    /// <param name="name">The method name, case-insensitive on input and lowercase in the vocabulary.</param>
    /// <returns>The method.</returns>
    /// <exception cref="KeyNotFoundException">The name is not in the vocabulary.</exception>
    public ILevelMethod Resolve(string name)
    {
        string normalised = (name ?? string.Empty).Trim().ToLowerInvariant();
        return _byName.TryGetValue(normalised, out ILevelMethod? method)
            ? method
            : throw new KeyNotFoundException(
                "Unknown level method '" + normalised + "'. Known methods: "
                + string.Join(", ", KnownNames) + ".");
    }
}
