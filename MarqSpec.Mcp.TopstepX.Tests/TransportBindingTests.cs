using System.Net;
using System.Net.Sockets;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MarqSpec.Mcp.TopstepX.Tests;

/// <summary>
/// Under stdio the host must bind no HTTP port, unless the operator named one for health or metrics.
/// </summary>
/// <remarks>
/// <para>
/// This exists because two stdio containers sharing a network namespace collided on 8080 (gh#696). The
/// aspnet base image bakes <c>ASPNETCORE_HTTP_PORTS=8080</c>, and <c>ConfigureDefaultBinding</c> treated
/// that inherited variable as an operator-named address, so Kestrel bound every container interface
/// even though <c>MapMcp</c> is inside the HTTP branch and the session runs over stdin and stdout.
/// </para>
/// <para>
/// gh#392 already stopped two <i>host</i> processes colliding on the framework default <c>:5000</c> by
/// giving stdio an ephemeral loopback address. That default never applies inside the image — an address
/// is always named — and the 2026-09-03 ADR-0007 update recorded the container bind as accepted. Host
/// networking, or a second container on the same namespace, makes it a collision again.
/// </para>
/// <para>
/// gh#76 rejected a second host type for stdio. These tests do not reopen that: <c>WebApplication</c>
/// still starts. They pin the <i>address</i> — there is none — so a second session cannot contend.
/// </para>
/// <para>
/// <b>What the last three tests are for.</b> A fix that suppressed every address, including one the
/// operator named, would silently break a stdio process that opted into a health or metrics listener
/// via <c>ASPNETCORE_URLS</c> or <c>ASPNETCORE_HTTPS_PORTS</c>. The image-baked HTTP port is not that
/// opt-in. The HTTP transport must keep the framework's own defaulting, because
/// <c>docker-compose.yml</c> and the deployed task both tell it where to listen.
/// </para>
/// </remarks>
public sealed class TransportBindingTests
{
    /// <summary>A host configured exactly as <c>Program.Main</c> configures one under stdio.</summary>
    private static WebApplication BuildStdioHost()
    {
        WebApplicationBuilder builder = NewBuilder();
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

    /// <summary>A TCP port the OS has just assigned, then released — used only to name a collision.</summary>
    private static int FreePort()
    {
        TcpListener probe = new(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>Holds <paramref name="port"/> exclusively on loopback so a second bind must fail.</summary>
    private static Socket BindExclusive(int port)
    {
        Socket socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            ExclusiveAddressUse = true,
        };
        socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
        socket.Listen(1);
        return socket;
    }

    [Fact]
    public async Task StdioHostBindsNoPort_WhenNoAddressIsNamed()
    {
        await using WebApplication app = BuildStdioHost();

        await app.StartAsync();

        app.Urls.Should().BeEmpty("stdio is the pipe; nothing should listen unless an address was named");

        await app.StopAsync();
    }

    [Fact]
    public async Task TwoStdioHostsStartTogether_WhenNeitherNamesAnAddress()
    {
        await using WebApplication first = BuildStdioHost();
        await using WebApplication second = BuildStdioHost();

        await first.StartAsync();
        await second.StartAsync();

        first.Urls.Should().BeEmpty();
        second.Urls.Should().BeEmpty();

        await first.StopAsync();
        await second.StopAsync();
    }

    [Fact]
    public async Task StdioHostBindsNoPort_WhenTheAspNetImageNamesHttpPorts()
    {
        // The bug, stated as the thing an operator does: launch the published image over stdio. The
        // base image always names 8080, which used to make HasExplicitAddress return true and Kestrel
        // bind every container interface. A second instance on host networking then died on AddressInUse.
        int port = FreePort();
        WebApplicationBuilder builder = NewBuilder();
        builder.Configuration[WebHostDefaults.HttpPortsKey] = port.ToString();
        Program.ConfigureDefaultBinding(builder, McpTransport.Stdio);

        await using WebApplication app = builder.Build();
        await app.StartAsync();

        app.Urls.Should().BeEmpty("the image-baked HTTP port is not an operator-named listener");

        using Socket held = BindExclusive(port);
        held.IsBound.Should().BeTrue("the stdio host must have left the inherited port free");

        await app.StopAsync();
    }

    [Fact]
    public async Task TwoStdioHostsStartTogether_WhenBothInheritTheImageHttpPorts()
    {
        int port = FreePort();

        WebApplicationBuilder firstBuilder = NewBuilder();
        firstBuilder.Configuration[WebHostDefaults.HttpPortsKey] = port.ToString();
        Program.ConfigureDefaultBinding(firstBuilder, McpTransport.Stdio);

        WebApplicationBuilder secondBuilder = NewBuilder();
        secondBuilder.Configuration[WebHostDefaults.HttpPortsKey] = port.ToString();
        Program.ConfigureDefaultBinding(secondBuilder, McpTransport.Stdio);

        await using WebApplication first = firstBuilder.Build();
        await using WebApplication second = secondBuilder.Build();

        await first.StartAsync();
        await second.StartAsync();

        first.Urls.Should().BeEmpty();
        second.Urls.Should().BeEmpty();

        await first.StopAsync();
        await second.StopAsync();
    }

    [Fact]
    public void AnExplicitUrlSurvives_UnderStdio()
    {
        // Asserted on configuration rather than by binding: naming a fixed port in a test is the very thing
        // this issue is about. An operator who wants a health or metrics listener under stdio names one.
        WebApplicationBuilder builder = NewBuilder();
        builder.Configuration[WebHostDefaults.ServerUrlsKey] = "http://127.0.0.1:5199";

        Program.ConfigureDefaultBinding(builder, McpTransport.Stdio);

        builder.Configuration[WebHostDefaults.ServerUrlsKey].Should().Be("http://127.0.0.1:5199");
    }

    [Fact]
    public void ExplicitHttpsPortsSurvive_UnderStdio()
    {
        // ASPNETCORE_HTTPS_PORTS is an operator-named address, the same way docker-compose.yml names it
        // for the composed server since gh#416's TLS move. Suppressing every key would not be a default
        // — it would be a default that ate the opt-in.
        WebApplicationBuilder builder = NewBuilder();
        builder.Configuration[WebHostDefaults.HttpsPortsKey] = "8443";

        Program.ConfigureDefaultBinding(builder, McpTransport.Stdio);

        builder.Configuration[WebHostDefaults.ServerUrlsKey].Should().BeNullOrEmpty(
            "an explicitly named HTTPS port is the address, and nothing should be layered over it");
        builder.Configuration[WebHostDefaults.HttpsPortsKey].Should().Be("8443");
    }

    [Fact]
    public void TheHttpTransportIsLeftAlone()
    {
        // The composed and deployed servers are the HTTP ones, and they are told their port by
        // configuration — including the aspnet image's inherited 8080. This method must be invisible
        // to that path; clearing HTTP_PORTS here is the change ADR-0007 measured and rejected.
        WebApplicationBuilder builder = NewBuilder();
        builder.Configuration[WebHostDefaults.HttpPortsKey] = "8080";

        Program.ConfigureDefaultBinding(builder, McpTransport.Http);

        builder.Configuration[WebHostDefaults.ServerUrlsKey].Should().BeNullOrEmpty(
            "the HTTP transport keeps the framework's own defaulting");
        builder.Configuration[WebHostDefaults.HttpPortsKey].Should().Be("8080");
    }
}
