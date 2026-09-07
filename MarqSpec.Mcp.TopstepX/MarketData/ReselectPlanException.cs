namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// A <c>reselect-bars</c> plan would degrade for the <b>whole</b> window, so nothing is re-decided (gh#506).
/// </summary>
/// <remarks>
/// <para>
/// <b>A read degrades and says so; a rewrite must not.</b> <see cref="BarCacheService"/>'s read path falls
/// back to the venue's own front when it cannot build a candidate set, because serving something beats
/// serving nothing. A verb that rewrites stored provenance has the opposite duty: a plan built on that
/// fallback would stamp a guess onto rows the store already holds. There are three such conditions — the
/// venue lists no contracts for the instrument, the instrument is not one this server serves, or the front's
/// expiry does not read against the product's listing cycle — and each names itself in the message.
/// </para>
/// <para>
/// <b>It is a type rather than a bare <see cref="InvalidOperationException"/> because the verb's exit code
/// turns on it.</b> EF Core raises <see cref="InvalidOperationException"/> for its own defects: an
/// untranslatable LINQ expression, an entity with no key, a sequence expected to hold one element. Matching
/// the base type would report a bug in this repository to an operator as "the plan degraded, nothing was
/// rewritten" — a tidy exit 3 for a fault that may well have committed a series already. Deriving from it
/// keeps <c>catch (InvalidOperationException)</c> anywhere else behaving as it did; only the seam and the
/// verb care about the narrower type (<c>ReselectExit</c>).
/// </para>
/// </remarks>
public sealed class ReselectPlanException : InvalidOperationException
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">Which of the three conditions it was, and what the operator can do about it.</param>
    public ReselectPlanException(string message)
        : base(message)
    {
    }
}
