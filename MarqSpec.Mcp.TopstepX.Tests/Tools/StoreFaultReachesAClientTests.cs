using System.IO.Pipelines;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Tools;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Npgsql;

namespace MarqSpec.Mcp.TopstepX.Tests.Tools;

/// <summary>
/// The guard is only a boundary if the SDK lets it <i>see</i> a tool's exception. This drives a real client
/// over a real transport to prove it does.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the assumption the whole design rests on, and it is not obvious.</b> The SDK catches whatever a
/// tool throws and returns it as an error <see cref="CallToolResult"/> — which is how
/// <c>search_contracts</c> once reported nothing but <i>"An error occurred invoking 'search_contracts'"</i>.
/// If that catch sat <i>inside</i> the filter pipeline, a call-tool filter would never observe a
/// <see cref="DbUpdateException"/> at all and <c>StoreFaultGuard</c> would be dead code that every hand-fed
/// test still passed. So this feeds the exception in where a tool throws it, and reads the answer where a
/// client reads it: end to end, over a pipe pair, with the filters the composition root registers.
/// </para>
/// <para>
/// It also pins the <b>other</b> half — that a programming error still reaches the client as the SDK's opaque
/// "an error occurred invoking …" and nothing more, with the guard's sentence nowhere in it. That is the
/// observable difference between "the store is busy, retry" and "this server has a defect", and it is what
/// the caller actually sees.
/// </para>
/// </remarks>
public sealed class StoreFaultReachesAClientTests
{
    private static readonly Dictionary<string, string?> _settings = new()
    {
        ["ConnectionStrings:Default"] = "Host=localhost;Database=x;Username=u;Password=p",
        ["MarketData:Instruments"] = "ES,NQ",
        ["MarketData:SessionCloseCentral"] = "16:00",
        ["MarketData:MaxRows"] = "5000",
    };

    [Fact]
    public async Task ALostRaceInsideATool_ReachesTheClientAsTheStatedCondition()
    {
        DbUpdateException duplicate = new(
            "An error occurred while saving the entity changes.",
            new PostgresException(
                "duplicate key value violates unique constraint \"PK_Bars\"",
                "ERROR",
                "ERROR",
                PostgresErrorCodes.UniqueViolation));

        string reported = await CallAsync(() => throw duplicate);

        reported.Should().Contain(
            "Another writer committed rows this call collided on",
            "the filter has to run OUTSIDE the SDK's own tool catch, or the guard never sees anything");
        reported.Should().Contain("23505");
        reported.Should().Contain(
            "retry", "an error a caller cannot act on is barely better than the stack it replaced");

        // The SDK still prefixes its own "An error occurred invoking 'boom':" and then appends the message.
        // Measured, not assumed -- and it is why the assertions above are on the SENTENCE rather than on the
        // whole string being ours.
    }

    [Fact]
    public async Task AProgrammingErrorInsideATool_IsNotDressedUpAsAStoreCondition()
    {
        // The control for the test above. A defect in this server reaches the client as the SDK's own opaque
        // wrapper and nothing more -- which is right, because there is nothing the caller could do about it.
        string reported = await CallAsync(
            () => throw new InvalidOperationException("a pass must read the whole series"));

        reported.Should().Contain(
            "An error occurred invoking",
            "the guard let it past untranslated, which is the SDK's own reporting and the correct one here");
        reported.Should().NotContain(
            "store",
            "an invariant violation is a defect here; telling an operator to retry it wastes their day");
        reported.Should().NotContain("Postgres");
    }

    /// <summary>
    /// Runs one tool call end to end and returns what the client was told.
    /// </summary>
    /// <param name="tool">What the tool does when invoked.</param>
    /// <returns>The text content of the result.</returns>
    private static async Task<string> CallAsync(Func<string> tool)
    {
        // Bounded, so a handshake that never completes fails this test rather than hanging the suite.
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));

        Pipe toServer = new();
        Pipe toClient = new();

        McpServerOptions options = new()
        {
            ServerInfo = new Implementation { Name = "gh89", Version = "0.0.0" },
            ToolCollection = [McpServerTool.Create(tool, new McpServerToolCreateOptions { Name = "boom" })],
        };

        // THE FILTERS THE COMPOSITION ROOT REGISTERS, not a hand-placed one. If the wiring in Program is
        // removed, this test goes red rather than continuing to prove something about a local variable.
        using ServiceProvider provider = Build();
        foreach (McpRequestFilter<CallToolRequestParams, CallToolResult> filter in
            provider.GetRequiredService<IOptions<McpServerOptions>>().Value.Filters.Request.CallToolFilters)
        {
            options.Filters.Request.CallToolFilters.Add(filter);
        }

        await using McpServer server = McpServer.Create(
            new StreamServerTransport(
                toServer.Reader.AsStream(), toClient.Writer.AsStream(), "gh89", NullLoggerFactory.Instance),
            options,
            NullLoggerFactory.Instance,
            provider);

        Task running = server.RunAsync(CancellationToken.None);

        await using McpClient client = await McpClient.CreateAsync(
            new StreamClientTransport(
                toServer.Writer.AsStream(), toClient.Reader.AsStream(), NullLoggerFactory.Instance),
            cancellationToken: timeout.Token);

        CallToolResult result = await client.CallToolAsync(
            "boom", cancellationToken: timeout.Token);

        result.IsError.Should().BeTrue("the tool threw");

        return string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
    }

    private static ServiceProvider Build()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(_settings);

        Program.ConfigureServices(builder, new McpOptions { Transport = McpTransport.Stdio });

        return builder.Services.BuildServiceProvider();
    }
}
