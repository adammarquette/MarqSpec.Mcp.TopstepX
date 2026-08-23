using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
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
/// is untouched. That a real lost race actually produces one of these types, carrying the SqlState this
/// assumes, is a claim about Postgres and is pinned against a real one in
/// <c>MarqSpec.Mcp.TopstepX.IntegrationTests.StoreFaultBoundaryTests</c>.
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

        Filters(provider).Should().NotBeEmpty(
            "every tools/call goes through this pipeline; a guard anywhere else covers one tool");
    }

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
