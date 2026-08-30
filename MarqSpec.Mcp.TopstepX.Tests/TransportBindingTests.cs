using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MarqSpec.Mcp.TopstepX.Tests;

/// <summary>
/// Under stdio the host still starts Kestrel, but it must not take a well-known port to do it.
/// </summary>
/// <remarks>
/// <para>
/// This exists because two stdio sessions could not run on one machine (gh#392). ADR-0007 settled that
/// <c>WebApplication</c> always adds Kestrel and always starts it, under both transports, and gh#76 rejected
/// not starting it — that decision stands and nothing here reopens it. What followed from it and was never
/// decided is the <i>address</i>: with no <c>ASPNETCORE_URLS</c> and no <c>launchSettings.json</c>, Kestrel
/// took the framework default <c>http://localhost:5000</c> and held it, exclusively, for a listener that
/// serves nothing — <c>MapMcp</c> is inside the HTTP branch, and the session runs over stdin and stdout.
/// </para>
/// <para>
/// The second session then died on <c>IOException: Failed to bind to address http://127.0.0.1:5000: address
/// already in use</c>, exit <c>0xE0434352</c>, before any tool was reachable and with nothing said about
/// stdio. That is the error class gh#76 removed — a Kestrel stack trace naming the service that noticed
/// rather than the thing that happened — arriving through a different door.
/// </para>
/// <para>
/// It bites this repository specifically: <c>AGENTS.md</c> is built around parallel agent sessions, one
/// worktree each, and any two of those that start the server race for the same port. The workaround was
/// already in the tree before the cause was — <see cref="HostShutdownTests"/> pins its own hosts to
/// <c>127.0.0.1:0</c> because "a fixed port would make these tests unable to run beside anything else".
/// </para>
/// <para>
/// <b>What the last three tests are for.</b> The first two would pass against a fix that simply hard-wired
/// port 0 everywhere, which would silently break the deployed server: <c>docker-compose.yml</c> sets
/// <c>ASPNETCORE_HTTP_PORTS: 8080</c>, and an operator naming an address means it under either transport. A
/// default is only a default if something explicit still beats it.
/// </para>
/// </remarks>
public sealed class TransportBindingTests
{
    /// <summary>A host configured exactly as <c>Program.Main</c> configures one under stdio.</summary>
    private static WebApplication BuildStdioHost()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        Program.ConfigureDefaultBinding(builder, McpTransport.Stdio);
        return builder.Build();
    }

    /// <summary>A builder with no address configured, and no logging to corrupt stdout.</summary>
    private static WebApplicationBuilder NewBuilder()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        return builder;
    }

    [Fact]
    public async Task TwoStdioHostsBindTogether_WhenNeitherNamesAnAddress()
    {
        // The bug, stated as the thing an operator does: start a second session while the first is running.
        // Before the fix the second StartAsync threw AddressInUse, because both took :5000.
        await using WebApplication first = BuildStdioHost();
        await using WebApplication second = BuildStdioHost();

        await first.StartAsync();
        await second.StartAsync();

        first.Urls.Should().NotBeEmpty("the host still starts a listener under stdio (ADR-0007)");
        second.Urls.Should().NotBeEmpty("the host still starts a listener under stdio (ADR-0007)");
        first.Urls.Should().NotIntersectWith(second.Urls, "two sessions must not contend for one port");

        await first.StopAsync();
        await second.StopAsync();
    }

    [Fact]
    public async Task TheStdioHostAvoidsTheFrameworkDefaultPort_SoItCannotSquatOnFiveThousand()
    {
        // Port 5000 is not merely contended by a second copy of this server -- it is the default for a great
        // many dev servers, so taking it for a listener that serves nothing is a cost paid by whatever else
        // the operator is running.
        await using WebApplication app = BuildStdioHost();

        await app.StartAsync();

        app.Urls.Should().NotBeEmpty();
        app.Urls.Should().AllSatisfy(url => url.Should().NotEndWith(":5000"));

        await app.StopAsync();
    }

    [Fact]
    public void AnExplicitUrlSurvives_UnderStdio()
    {
        // Asserted on configuration rather than by binding: naming a fixed port in a test is the very thing
        // this issue is about.
        WebApplicationBuilder builder = NewBuilder();
        builder.Configuration[WebHostDefaults.ServerUrlsKey] = "http://127.0.0.1:5199";

        Program.ConfigureDefaultBinding(builder, McpTransport.Stdio);

        builder.Configuration[WebHostDefaults.ServerUrlsKey].Should().Be("http://127.0.0.1:5199");
    }

    [Fact]
    public void ExplicitHttpPortsSurvive_UnderStdio()
    {
        // ASPNETCORE_HTTP_PORTS is a second way to name an address, and overriding `urls` would defeat it
        // just as thoroughly as overwriting `urls` itself.
        WebApplicationBuilder builder = NewBuilder();
        builder.Configuration[WebHostDefaults.HttpPortsKey] = "8080";

        Program.ConfigureDefaultBinding(builder, McpTransport.Stdio);

        builder.Configuration[WebHostDefaults.ServerUrlsKey].Should().BeNullOrEmpty(
            "an explicitly named port is the address, and nothing should be layered over it");
    }

    [Fact]
    public void TheHttpTransportIsLeftAlone()
    {
        // The composed server is the HTTP one and it is told its port by docker-compose.yml. This method must
        // be invisible to it.
        WebApplicationBuilder builder = NewBuilder();

        Program.ConfigureDefaultBinding(builder, McpTransport.Http);

        builder.Configuration[WebHostDefaults.ServerUrlsKey].Should().BeNullOrEmpty(
            "the HTTP transport keeps the framework's own defaulting");
    }
}
