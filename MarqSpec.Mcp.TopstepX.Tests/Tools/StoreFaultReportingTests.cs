using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Npgsql;

namespace MarqSpec.Mcp.TopstepX.Tests.Tools;

/// <summary>
/// A store fault is a fact about this server's database. It must reach a caller as a sentence, never as a
/// stack (gh#89).
/// </summary>
/// <remarks>
/// <para>
/// <b>These test the boundary, not a tool.</b> The subject is the call-tool filter the composition root
/// registers, resolved from the container the server actually builds — so what is pinned is that <i>every</i>
/// tool is covered, rather than that one tool remembered to catch something. That distinction is the whole
/// card: <c>MarketDataTools.ReadAsync</c> caught <c>VenueException</c> and nothing else, so a <c>23505</c>
/// from two overlapping fills crossed the boundary as a raw <see cref="DbUpdateException"/> — and
/// <c>get_indicators</c>, <c>get_key_levels</c> and <c>record_observation</c> never went through that method
/// at all.
/// </para>
/// <para>
/// <b>The exceptions here are fabricated, and that is a deliberate division of labour.</b> What these pin is
/// the boundary's <i>policy</i> — which exception types are translated, which are not, and that a healthy call
/// is untouched. That a real fault actually produces one of these types, carrying the SqlState this assumes,
/// is a claim about Postgres and is pinned against a real one in
/// <c>MarqSpec.Mcp.TopstepX.IntegrationTests.StoreFaultBoundaryTests</c> — <b>for two of the three, not for
/// all of them.</b> That tier drives a real <c>40001</c> past the retry and a real <c>3D000</c>.
/// </para>
/// <para>
/// <b>The duplicate key is the exception: since gh#133 it is fabricated <i>only</i>.</b> The three writes that
/// could produce one — the bars, the coverage ledger, the indicator projection — are all
/// <c>ON CONFLICT … DO UPDATE</c> now (gh#103, gh#122, gh#133; epic gh#80), so no call site can reach a
/// <c>23505</c> and the integration tier declined to fabricate a collision in order to keep driving one. This
/// test therefore stands alone for that branch, which is why the branch is still worth having: the schema has
/// unique keys, this filter is served on behalf of every tool rather than of the fill path, and the next
/// writer added can hit one. If a real one becomes reachable again, drive <i>that</i> one from the
/// integration tier and correct this paragraph — it has now been wrong once for exactly that reason.
/// </para>
/// </remarks>
public sealed class StoreFaultReportingTests
{
    private static readonly Dictionary<string, string?> _settings = new()
    {
        ["ConnectionStrings:Default"] = "Host=localhost;Database=x;Username=u;Password=p",
        ["MarketData:Instruments"] = "ES,NQ",
        ["MarketData:SessionCloseCentral"] = "16:00",
        ["MarketData:MaxRows"] = "5000",
    };

    [Fact]
    public async Task ALostRace_ReachesTheCallerAsAStatedCondition_RatherThanADbUpdateException()
    {
        // THE regression. Two fills of overlapping ranges both find a bucket absent and both INSERT it; the
        // loser gets 23505. It reached the caller of get_bars as an unhandled DbUpdateException -- a
        // stack-shaped fault, on a read tool, with no statement of what happened or what to do about it.
        Func<Task> call = () => Invoke(Throws(Duplicate()));

        (await call.Should().ThrowAsync<McpException>(
            "a store fault is a condition to state, not a stack to emit"))
            .WithMessage("*another*")
            .WithMessage("*retry*");
    }

    [Fact]
    public async Task ALostRace_IsNotReportedAsASuccessSomeoneElseAchieved()
    {
        // The scope question this card had to answer, and the answer is REPORT rather than swallow.
        //
        // The rows the INSERT collided on really are in the store -- the other writer put them there -- so a
        // duplicate key on an idempotent upsert looks like a success achieved by proxy. It is not one. The
        // collision aborts the WHOLE transaction, and that transaction is not only the bars: it is the
        // coverage ledger and the indicator projection over the same series. Answering "fine" would return a
        // series assembled inside a transaction that rolled back, with fetchedBuckets counting writes that
        // never landed and indicators the store does not hold.
        Exception? thrown = await Record.ExceptionAsync(() => Invoke(Throws(Duplicate())));

        thrown.Should().BeOfType<McpException>(
            "a rolled-back unit of work is not an answer, however the rows got there");
        thrown!.Message.Should().Match(
            "*rolled back*",
            "the caller is told which of its work survived -- 'someone else wrote the rows' is only half of "
            + "what happened");
    }

    [Fact]
    public async Task AStoreFaultThatIsNotADuplicate_ReachesTheCallerTheSameWay()
    {
        // The catch that only knew about VenueException let EVERY store fault past, not only 23505. A
        // connection dropped mid-save is the ordinary one -- the database was there at startup, which is when
        // StoreAvailability last looked, and went away afterwards.
        Func<Task> call = () => Invoke(Throws(new NpgsqlException("the connection was closed")));

        (await call.Should().ThrowAsync<McpException>()).WithMessage("*store*");
    }

    [Fact]
    public async Task AWriteWhoseOutcomeIsUNKNOWN_IsNotReportedAsIfNothingWasWritten()
    {
        // The regression, and the rule .github/copilot-instructions.md leads with: a timeout is not a
        // failure, it is an UNKNOWN OUTCOME, and a path that reports "did not happen" has lied.
        //
        // SaveChangesAsync sends COMMIT, Postgres commits it, and the connection drops -- or the command
        // timeout elapses -- before the acknowledgement comes back. Npgsql raises an NpgsqlException with NO
        // SqlState (an IOException or a TimeoutException inside, never a PostgresException), EF wraps it in a
        // DbUpdateException, and the bars, the coverage row and the projection are all on disk. The boundary
        // cannot see which of the two happened, so it must not pick one.
        DbUpdateException lostAcknowledgement = new(
            "An error occurred while saving the entity changes.",
            new NpgsqlException(
                "Exception while reading from stream", new IOException("the connection was reset")));

        Func<Task> call = () => Invoke(Throws(lostAcknowledgement));

        string reported = (await call.Should().ThrowAsync<McpException>()).Which.Message;

        reported.Should().NotContainEquivalentOf(
            "nothing was written",
            "the acknowledgement was lost, not necessarily the write -- claiming a durable outcome the "
            + "boundary cannot observe is the lie the checklist leads with");
        reported.Should().NotContainEquivalentOf(
            "rolled back", "a server that never answered cannot be said to have aborted anything");
        reported.Should().ContainEquivalentOf(
            "unknown", "what is known is that the outcome is not known, and that is what to say");
    }

    [Fact]
    public async Task APermanentStoreFault_IsNOTAdvertisedAsRetryable()
    {
        // A deploy where the migration has not been applied. get_indicators hits
        // `42P01 relation "IndicatorValues" does not exist` -- a defect in THIS deployment, not a condition
        // of the store -- and telling the caller to retry sends it round a loop it can never come out of.
        //
        // NpgsqlException is the provider's BASE type, so PostgresException rides in on the same catch. The
        // classifier a caller needs is the SqlState class, which the guard already reads.
        Func<Task> call = () => Invoke(Throws(Answered(PostgresErrorCodes.UndefinedTable)));

        string reported = (await call.Should().ThrowAsync<McpException>()).Which.Message;

        reported.Should().ContainEquivalentOf(
            "42P01", "the SqlState is the one coordinate-free fact worth echoing");
        reported.Should().ContainEquivalentOf(
            "will not help",
            "a missing relation is this server's own defect; a caller told to retry it retries forever");
        reported.Should().NotContainEquivalentOf(
            "the store itself needs attention",
            "the store answered correctly -- what it answered is that this server asked for something that "
            + "does not exist");
    }

    [Theory]
    [InlineData("42501")]
    [InlineData("3D000")]
    [InlineData("28P01")]
    public async Task TheOtherPermanentClasses_AreClassifiedTheSameWay(string sqlState)
    {
        // 42501 insufficient privilege, 3D000 database does not exist, 28P01 bad password. None of them
        // becomes true by being asked again.
        Func<Task> call = () => Invoke(Throws(Answered(sqlState)));

        string reported = (await call.Should().ThrowAsync<McpException>()).Which.Message;

        reported.Should().ContainEquivalentOf(sqlState);
        reported.Should().ContainEquivalentOf("will not help");
    }

    [Theory]
    [InlineData("08006")]
    [InlineData("53300")]
    [InlineData("57P01")]
    [InlineData("40001")]
    public async Task ATransientStoreFault_IsStillAdvertisedAsRetryable(string sqlState)
    {
        // The other side of the same gate: narrowing "retry" must not narrow it to nothing. Connection
        // failure, out of resources, operator intervention, serialisation failure -- all conditions of an
        // environment, all worth asking again.
        Func<Task> call = () => Invoke(Throws(Answered(sqlState)));

        string reported = (await call.Should().ThrowAsync<McpException>()).Which.Message;

        reported.Should().ContainEquivalentOf(sqlState);
        reported.Should().ContainEquivalentOf(
            "retry", "these are conditions of an environment, and asking again is the right advice");
        reported.Should().NotContainEquivalentOf("will not help");
    }

    [Fact]
    public async Task ASqlStateThisServerCannotClassify_PrefersUnknownOverAConfidentRetry()
    {
        // Fail closed. An unrecognised SqlState is not evidence that retrying works, and a permissive default
        // is the recurring defect shape this repository reviews for.
        Func<Task> call = () => Invoke(Throws(Answered("XX000")));

        string reported = (await call.Should().ThrowAsync<McpException>()).Which.Message;

        reported.Should().ContainEquivalentOf("XX000");
        reported.Should().ContainEquivalentOf(
            "unknown", "an unclassified condition is honestly unknown, not optimistically transient");
    }

    [Fact]
    public async Task ALostRace_IsDescribedWithoutNarratingAUnitOfWorkTheBoundaryNeverSaw()
    {
        // The guard speaks for all fifteen tools. "The coverage ledger and the indicator projection over the
        // same series" is a fact about SeriesUnitOfWork, and it is true today only because every unique key
        // in the schema happens to be bars-family. Handed to the tool that adds the next one, it is a false
        // statement wearing a boundary-shaped guarantee.
        Func<Task> call = () => Invoke(Throws(Duplicate()));

        string reported = (await call.Should().ThrowAsync<McpException>()).Which.Message;

        reported.Should().NotContainEquivalentOf(
            "coverage ledger", "a call-tool filter does not know which unit of work it was wrapping");
        reported.Should().NotContainEquivalentOf("indicator projection");
    }

    [Fact]
    public async Task AProgrammingError_CrossesTheBoundaryUNCHANGED()
    {
        // The test that stops the catch being widened later, and the reason it is not `catch (Exception)`.
        //
        // IndicatorProjector's whole-series guard is an InvalidOperationException, and it means an invariant
        // of this repository was violated. Reporting that as a store condition would tell an operator to
        // retry a call that will never succeed, and would bury the defect under a transient-looking sentence.
        InvalidOperationException invariant = new("a pass must read the whole series");

        Func<Task> call = () => Invoke(Throws(invariant));

        (await call.Should().ThrowAsync<InvalidOperationException>(
            "a defect in this repository is not a condition of the store"))
            .Which.Should().BeSameAs(invariant);
    }

    [Fact]
    public async Task AHealthyCall_PassesThroughTheBoundaryUntouched()
    {
        // gh#87: a guard is only correct if it is also invisible. Without this, "translates a store fault"
        // and "fails every call" are the same green.
        CallToolResult answer = new() { IsError = false };

        CallToolResult returned = await Invoke((_, _) => new ValueTask<CallToolResult>(answer));

        returned.Should().BeSameAs(answer, "nothing faulted, so the boundary has nothing to say");
    }

    [Fact]
    public void TheGuardIsRegisteredOnTheSERVER_NotOnATool()
    {
        // What makes this a boundary rather than a fourth call site. A tool added tomorrow is covered by
        // having been registered, not by its author remembering a try/catch -- which is the gh#69 lesson
        // stated as wiring instead of as a rule.
        using ServiceProvider provider = Build();

        // Named, not counted. `NotBeEmpty` stayed green with StoreFaultGuard deleted from Program and any
        // other call-tool filter registered in its place -- a test whose name claimed the wiring while its
        // assertion checked only that SOMETHING was wired.
        Filters(provider).Should().Contain(
            StoreFaultGuard.Filter,
            "every tools/call goes through this pipeline; a guard anywhere else covers one tool");
    }

    [Fact]
    public async Task TheRegisteredPipeline_BEHAVESLikeTheGuard()
    {
        // The other half of the same claim, because reference identity alone would survive the guard being
        // registered where it never runs: the list the composition root builds, folded into a pipeline and
        // driven, produces THIS guard's translation of a fabricated fault.
        DbUpdateException duplicate = Duplicate();

        Func<Task> call = () => Invoke(Throws(duplicate));

        (await call.Should().ThrowAsync<McpException>())
            .Which.Message.Should().Be(
                StoreFaultGuard.Describe(duplicate),
                "the filter that ran is the one whose wording this repository maintains");
    }

    /// <summary>A fault the store itself answered with, arriving raw from the provider on a read.</summary>
    /// <param name="sqlState">What Postgres answered.</param>
    /// <returns>The fault.</returns>
    private static PostgresException Answered(string sqlState) =>
        new("the store answered", "ERROR", "ERROR", sqlState);

    private static DbUpdateException Duplicate() =>
        new(
            "An error occurred while saving the entity changes.",
            new PostgresException(
                "duplicate key value violates unique constraint \"PK_Bars\"",
                "ERROR",
                "ERROR",
                PostgresErrorCodes.UniqueViolation));

    private static McpRequestHandler<CallToolRequestParams, CallToolResult> Throws(Exception exception) =>
        (_, _) => throw exception;

    /// <summary>Runs a tool body through the boundary the composition root actually registers.</summary>
    /// <param name="tool">What the tool call does.</param>
    /// <returns>Whatever came back out of the pipeline.</returns>
    private static async Task<CallToolResult> Invoke(
        McpRequestHandler<CallToolRequestParams, CallToolResult> tool)
    {
        using ServiceProvider provider = Build();

        // Folded in reverse, so the filter registered first is the OUTERMOST -- the order the SDK composes
        // them in. It matters the moment there is a second one.
        McpRequestHandler<CallToolRequestParams, CallToolResult> pipeline = tool;
        foreach (McpRequestFilter<CallToolRequestParams, CallToolResult> filter in Filters(provider).Reverse())
        {
            pipeline = filter(pipeline);
        }

        // The context is never read by a guard that only classifies exceptions, and building a real one needs
        // a live McpServer and a client handshake -- which would test the SDK rather than this repository.
        return await pipeline(null!, CancellationToken.None);
    }

    private static IList<McpRequestFilter<CallToolRequestParams, CallToolResult>> Filters(
        ServiceProvider provider) =>
        provider.GetRequiredService<IOptions<McpServerOptions>>().Value.Filters.Request.CallToolFilters;

    private static ServiceProvider Build()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(_settings);

        Program.ConfigureServices(builder, new McpOptions { Transport = McpTransport.Stdio });

        return builder.Services.BuildServiceProvider();
    }
}
