namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// What the <c>reselect-bars</c> verb tells the shell it did (gh#506).
/// </summary>
/// <remarks>
/// <para>
/// <b>A type rather than three literals in the verb branch, because <c>Program.Main</c> is not tested
/// through.</b> Nothing in this repository invokes it: the verbs are covered by exercising the loop
/// directly and by pinning the container resolution in <c>CompositionRootTests</c>. An exit code written
/// inline in the composition root would therefore be a contract with an operator's shell script that
/// nothing checks — and this is the number they read to decide whether to run the next window.
/// </para>
/// <para>
/// <b>It is deliberately not total.</b> Two exceptions have a story an operator can act on; anything else
/// is a fault, and a fault must arrive as a fault. Giving an unclassified exception a plausible code would
/// let a store contention, a cancellation or an outright bug read as "the window was re-decided, with a
/// caveat" — the same shape as a missing number returned as a default, which this server refuses
/// everywhere else.
/// </para>
/// </remarks>
public static class ReselectExit
{
    /// <summary>The run finished: the window was re-decided, and the log lines say by how much.</summary>
    public const int Ok = 0;

    /// <summary>
    /// The command line was refused, before the store was touched — a bad symbol, a malformed instant, an
    /// empty or inverted window, a window past the calendar's horizon. Nothing was written; correct the
    /// arguments and run it again.
    /// </summary>
    public const int RefusedArgument = 2;

    /// <summary>
    /// <b>The run stopped.</b> The command line was right and the window still was not re-decided, so this
    /// is an environment to fix rather than a typo — the store was unreachable or the migration dropped its
    /// connection, or a plan degraded for a whole window (the venue lists no contracts, the instrument is
    /// not served, its front does not read against the product's cycle).
    /// </summary>
    /// <remarks>
    /// <b>It does not mean nothing was written.</b> The reselector commits one unit of work per resolution
    /// series, so a degradation on the second series exits 3 with the first already committed — and the
    /// migration itself can degrade partway through. <b>Read the per-series log lines</b> to see how far the
    /// run got; the summary line is not printed when a series throws.
    /// </remarks>
    public const int Degraded = 3;

    /// <summary>
    /// The exit code for an exception the verb classifies.
    /// </summary>
    /// <param name="exception">The exception the verb caught.</param>
    /// <returns><see cref="RefusedArgument"/> or <see cref="Degraded"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The exception is not one this verb has a story for. It is not given a code at all — see the remarks
    /// on the type.
    /// </exception>
    public static int From(Exception exception) => exception switch
    {
        // ArgumentException is what ReselectArguments.Parse throws, and the reason every refusal there is
        // one: the parse runs before MigrateAsync, so a 2 means the run never started.
        ArgumentException => RefusedArgument,

        // ReselectPlanException and NOT its base InvalidOperationException. EF Core raises that base type
        // for its own defects -- an untranslatable LINQ expression, an entity with no key, a sequence
        // expected to hold one element -- and every one of those is a bug in this repository. Matching the
        // base would hand an operator "the plan degraded" and a tidy 3 for a fault that may already have
        // committed a series.
        ReselectPlanException => Degraded,

        _ => throw new ArgumentOutOfRangeException(
            nameof(exception),
            exception?.GetType().FullName,
            "The reselect-bars verb has no exit code for this exception, and must not invent one: it "
            + "classifies a refused argument and a whole-window degradation, and everything else is a "
            + "fault that reaches the operator with its stack."),
    };
}
