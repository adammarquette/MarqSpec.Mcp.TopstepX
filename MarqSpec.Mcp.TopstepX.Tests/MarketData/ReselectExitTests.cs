using FluentAssertions;
using MarqSpec.Mcp.TopstepX.MarketData;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// What the <c>reselect-bars</c> verb tells the shell it did (gh#506).
/// </summary>
/// <remarks>
/// <para>
/// <b>The mapping lives in a static because <c>Program.Main</c> is not tested through.</b> Nothing in this
/// repository invokes it — the verbs are covered by testing the loop directly and by pinning the
/// resolution in <c>CompositionRootTests</c> — so an exit code written inline in the composition root is a
/// contract with an operator's shell script that nothing checks. An operator runs this verb over months of
/// history and reads the number to decide whether to run the next one.
/// </para>
/// <para>
/// Three outcomes, and they are different facts: the arguments were wrong (nothing was touched), the read
/// degraded for the whole window (the store is intact but nothing was re-decided), or it worked.
/// </para>
/// </remarks>
public sealed class ReselectExitTests
{
    [Fact]
    public void ARefusedArgument_ExitsTwo()
    {
        // ReselectArguments.Parse throws exactly this, before the store is touched -- so a 2 means the run
        // never started and re-running with a corrected command line is safe.
        ReselectExit.From(new ArgumentException("toUtc is before fromUtc.", "args"))
            .Should().Be(2);

        ReselectExit.RefusedArgument.Should().Be(2, "the constant and the mapping cannot drift apart");
    }

    [Fact]
    public void AWholeReadDegradation_ExitsThree()
    {
        // BarCacheService.ReselectWindowAsync throws ReselectPlanException naming the reason when the plan
        // would degrade for a whole window -- an unserved instrument, an unreadable front, a front outside
        // its own listing cycle. Distinct from 2 because the command line was RIGHT and the answer still is
        // not available: the operator has an environment to fix, not a typo. It does NOT mean nothing was
        // written -- one unit of work per series, so an earlier series may have committed.
        ReselectExit.From(new ReselectPlanException("The front month could not be read."))
            .Should().Be(3);

        ReselectExit.Degraded.Should().Be(3);
        ReselectExit.Ok.Should().Be(0);
    }

    [Fact]
    public void AnUnclassifiedFault_IsNotGivenAnExitCode()
    {
        // A fault this verb has no story for must reach the operator as an unhandled fault with its stack,
        // not as a tidy number. Handing back a plausible code here would let a store contention, a
        // cancellation or an outright bug be read as "the window was re-decided, with a caveat".
        Func<int> classify = () => ReselectExit.From(new TimeoutException("The venue did not answer."));

        classify.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void APlainInvalidOperationException_IsNotADegradation()
    {
        // EF CORE RAISES InvalidOperationException FOR ITS OWN DEFECTS -- an untranslatable LINQ expression,
        // a tracked entity with no key, a sequence that was expected to hold exactly one element. Every one
        // of those is a bug inside the reselector, and matching the base type would report it to an operator
        // as "the venue plan degraded, nothing was rewritten" with a tidy exit 3 -- when in fact a series
        // may well have committed and the defect is in this repository, not in their environment.
        //
        // So only the seam's own type counts. This is the case that stops the match being widened back.
        Func<int> classify = () => ReselectExit.From(
            new InvalidOperationException("Sequence contains no elements."));

        classify.Should().Throw<ArgumentOutOfRangeException>(
            "only ReselectPlanException means the plan degraded for the whole window");
    }
}
