namespace MarqSpec.Mcp.TopstepX.Domain.MarketData;

/// <summary>
/// The closed vocabulary of <see cref="PivotSource"/> values a series can actually be read through.
/// </summary>
/// <remarks>
/// <para>
/// <b>One list, so <see cref="PivotSource.Unknown"/> is refused everywhere rather than in most places.</b>
/// Three call sites need the same answer — the detection guard in <see cref="KeyLevels"/>, the options
/// validation that runs at startup, and the tool that resolves a caller's name — and a source rejected at two
/// of them is not rejected: the third is the one that picks a price series by accident. <c>Unknown = 0</c> is
/// what a field left unset binds to, so the value that must never be honoured is exactly the value that
/// arrives when nobody chose one.
/// </para>
/// <para>
/// <b>Derived from the enum rather than written out</b>, so a fourth source added to
/// <see cref="PivotSource"/> is servable the moment it exists rather than the moment somebody remembers this
/// file. The order is declaration order — <see cref="PivotSource.HeikinAshiBody"/> first, because it is the
/// default and an error message that lists it first is telling the reader what they would have got.
/// </para>
/// <para>
/// It lives in <c>Domain</c> beside the enum and stays pure — no clock, no store, no configuration. The host
/// translates the exceptions below into its own tool errors; it does not restate the vocabulary.
/// </para>
/// </remarks>
public static class PivotSources
{
    /// <summary>Every source except <see cref="PivotSource.Unknown"/>, in declaration order.</summary>
    public static IReadOnlyList<PivotSource> Servable { get; } =
        [.. Enum.GetValues<PivotSource>().Where(source => source != PivotSource.Unknown)];

    /// <summary>The servable names, comma-separated, for an error message that is actually useful.</summary>
    public static string KnownNames { get; } = string.Join(", ", Servable);

    /// <summary>
    /// Whether a source is one this pipeline can read a series through.
    /// </summary>
    /// <param name="source">The source.</param>
    /// <returns>
    /// <see langword="false"/> for <see cref="PivotSource.Unknown"/> and for any value outside the enum.
    /// </returns>
    /// <remarks>
    /// The second half matters as much as the first. A cast integer — <c>(PivotSource)99</c> — is neither
    /// <c>Unknown</c> nor a defined member, and <see cref="KeyLevels"/> reads anything it does not recognise
    /// as Heikin-Ashi, so an out-of-range value would have produced an ordinary-looking level set measured
    /// from a source nobody asked for.
    /// </remarks>
    public static bool IsServable(PivotSource source) => Servable.Contains(source);

    /// <summary>
    /// Resolves a source name, or throws naming the valid ones.
    /// </summary>
    /// <param name="name">The source name, case-insensitive on input and padded or not.</param>
    /// <returns>The source.</returns>
    /// <exception cref="KeyNotFoundException">The name is not in the vocabulary.</exception>
    /// <remarks>
    /// Matched against the names in <see cref="Servable"/> rather than through <c>Enum.TryParse</c>, which
    /// would accept <c>"0"</c> and <c>"Unknown"</c> as well as <c>"99"</c> — the three inputs this exists to
    /// refuse. An unknown name is an error listing the known ones, on the same reasoning as
    /// <c>IndicatorCatalog</c>: a typo that returned no levels is indistinguishable from a market that has
    /// produced no structure, and only the second reads as a conclusion.
    /// </remarks>
    public static PivotSource Resolve(string? name)
    {
        string trimmed = (name ?? string.Empty).Trim();

        foreach (PivotSource source in Servable)
        {
            if (string.Equals(source.ToString(), trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return source;
            }
        }

        throw new KeyNotFoundException(
            "Unknown pivot source '" + trimmed + "'. Known sources: " + KnownNames + ".");
    }
}
