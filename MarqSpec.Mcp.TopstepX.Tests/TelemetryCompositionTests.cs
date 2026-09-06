using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace MarqSpec.Mcp.TopstepX.Tests;

/// <summary>
/// What the telemetry wiring must do, and — more sharply — what it must never do (ADR-0019).
/// </summary>
/// <remarks>
/// <para>
/// Sibling of <see cref="CompositionRootTests"/> and built the same way, with one addition: the runtime calls
/// <c>ConfigureTelemetry</c> from <c>Main</c> rather than from <c>ConfigureServices</c>, so
/// <see cref="Build"/> here mirrors <b>both</b> calls. A helper that ran only the second would leave every
/// assertion below testing a host the process never builds.
/// </para>
/// <para>
/// <b>Nothing here reaches a network but the loopback listener one test starts for itself.</b> The unit tier
/// needs no container and must stay that way; a collector is a compose-stack measurement, recorded on the
/// pull request, not a test dependency.
/// </para>
/// </remarks>
public sealed class TelemetryCompositionTests
{
    /// <summary>An endpoint that parses and is never dialled — no test here exports anything real.</summary>
    private const string Endpoint = "http://127.0.0.1:4317";

    private static readonly Dictionary<string, string?> _baseSettings = new()
    {
        ["ConnectionStrings:Default"] = "Host=localhost;Database=x;Username=u;Password=p",
        ["MarketData:Instruments"] = "ES,NQ",
        ["MarketData:SessionCloseCentral"] = "16:00",
        ["MarketData:MaxRows"] = "5000",
    };

    private static ServiceProvider Build(
        Dictionary<string, string?> extra,
        McpTransport transport = McpTransport.Stdio,
        Action<IServiceCollection>? alsoRegister = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(_baseSettings.Concat(extra));

        McpOptions mcp = new() { Transport = transport, HttpBearerToken = "a-token" };

        // The order Main uses. ConfigureLogging is in the mirror deliberately: R-5.5 is a claim about what the
        // two calls do TOGETHER, and it cannot be tested from either one alone.
        Program.ConfigureLogging(builder, transport);
        Program.ConfigureServices(builder, mcp);

        OtelOptions otel = builder.Configuration.GetSection(OtelOptions.SectionName).Get<OtelOptions>()
            ?? new OtelOptions();

        Program.ConfigureTelemetry(builder, otel);

        alsoRegister?.Invoke(builder.Services);

        return builder.Services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    [Fact]
    public void NothingIsRegistered_WhenNoEndpointIsConfigured()
    {
        // ADR-0019 decision 3: absent configuration is TODAY'S BEHAVIOUR, EXACTLY. Not a disabled exporter, not
        // a provider with no processor -- nothing built at all, so there is no background exporter thread
        // dialling a collector that is not there and no startup warning about one. Under stdio the host is a
        // child process on a laptop launched by an MCP client, and the only place an operator can see anything
        // is the stderr stream a retry loop would fill.
        using ServiceProvider provider = Build([]);

        provider.GetService<TracerProvider>().Should().BeNull();
        provider.GetService<MeterProvider>().Should().BeNull();
        provider.GetServices<ILoggerProvider>().Should().NotContain(p => p is OpenTelemetryLoggerProvider);

        // And R-5.5 is untouched: the one provider left is the stderr console ConfigureLogging installs.
        // A console EXPORTER would corrupt the protocol frame; this is the console LOGGER, on stderr, and the
        // two are only a word apart.
        provider.GetServices<ILoggerProvider>().Should().ContainSingle()
            .Which.Should().BeOfType<ConsoleLoggerProvider>();
        provider.GetRequiredService<IOptions<ConsoleLoggerOptions>>().Value
            .LogToStandardErrorThreshold.Should().Be(LogLevel.Trace);
    }

    [Fact]
    public void AllThreeSignalsRegister_AndTheResourceNamesTheService_WhenAnEndpointIsConfigured()
    {
        // Three signals behind ONE exporter and one trace id is the whole reason this is OpenTelemetry rather
        // than a logging library (ADR-0019 decision 1). Two of the three registering is not two thirds of the
        // feature: a span that cannot be led back to its log lines is the state the epic started in.
        using ServiceProvider provider = Build(new Dictionary<string, string?> { ["Otel:Endpoint"] = Endpoint });

        TracerProvider tracer = provider.GetRequiredService<TracerProvider>();
        provider.GetRequiredService<MeterProvider>().Should().NotBeNull();
        provider.GetServices<ILoggerProvider>().Should().Contain(p => p is OpenTelemetryLoggerProvider);

        Resource resource = tracer.GetResource();
        Dictionary<string, object> attributes = resource.Attributes
            .ToDictionary(a => a.Key, a => a.Value, StringComparer.Ordinal);

        attributes.Should().ContainKey("service.name")
            .WhoseValue.Should().Be(OtelOptions.DefaultServiceName);

        // service.version is the ASSEMBLY's, and it is present rather than correct: ADR-0001 makes the git tag
        // the version and the container stamps 0.0.0-alpha.0 by decision, so what this pins is that the
        // attribute is always there and never blank. gh#513's Deployment__Version is what will make it mean
        // something on a deployed instance.
        attributes.Should().ContainKey("service.version");
        attributes["service.version"].As<string>().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task NoRequestHeaderReachesASpanAttribute()
    {
        // TheEmbeddingKeyIsRedactedFromHttpLogging (CompositionRootTests) for SPANS. THIS REPOSITORY IS PUBLIC
        // and the sibling ProjectX client has already leaked and rotated a real credential once; a trace
        // backend is one more place a header can come to rest, and it is one an operator shares screenshots of.
        //
        // This pins the REQUIREMENT -- no header value reaches a span -- not the mechanism. HttpClient
        // instrumentation records URLs and status codes and not headers TODAY; the way this breaks is an
        // enrichment callback added later that copies request headers onto the activity because one of them
        // was interesting.
        const string EmbeddingKey = "embedding-key-that-must-never-be-traced";
        const string VenueKey = "venue-key-that-must-never-be-traced";

        List<Activity> exported = [];

        using ServiceProvider provider = Build(
            new Dictionary<string, string?>
            {
                ["Otel:Endpoint"] = Endpoint,
                ["Embeddings:ApiKey"] = EmbeddingKey,
                ["ProjectX:ApiKey"] = VenueKey,
                ["ProjectX:ApiSecret"] = "an-api-key",
                ["ProjectX:DataTier"] = "Simulated",
            },
            alsoRegister: services => services.ConfigureOpenTelemetryTracerProvider(
                tracing => tracing.AddInMemoryExporter(exported)));

        // Resolving it is what subscribes the ActivityListener. The container owns it; disposing it here would
        // shut the pipeline down before the request that is the point of the test.
        _ = provider.GetRequiredService<TracerProvider>();

        using TcpListener listener = new(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        Task served = RespondOnceAsync(listener);

        using (HttpClient client = new())
        using (HttpRequestMessage request = new(HttpMethod.Get, $"http://127.0.0.1:{port}/v2/embed"))
        {
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {EmbeddingKey}");
            request.Headers.TryAddWithoutValidation("X-Api-Key", VenueKey);

            using HttpResponseMessage response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        await served;

        exported.Should().NotBeEmpty(
            "the HttpClient instrumentation must be subscribed at all, or this test asserts over an empty list "
            + "and passes forever");

        foreach (Activity activity in exported)
        {
            activity.DisplayName.Should().NotContain(EmbeddingKey).And.NotContain(VenueKey);

            foreach (KeyValuePair<string, object?> tag in activity.TagObjects)
            {
                string? value = tag.Value?.ToString();
                value.Should().NotContain(EmbeddingKey, "span attribute '{0}' carried a credential", tag.Key);
                value.Should().NotContain(VenueKey, "span attribute '{0}' carried a credential", tag.Key);
            }
        }
    }

    [Fact]
    public void TheHealthProbeIsNotTraced()
    {
        // An ALB probes /health every 30 seconds. A span per probe is 2,880 spans a day that say nothing, and
        // they arrive on the same bill and in the same search results as the tool calls somebody is looking
        // for. gh#513 owns the endpoint; the filter lands here on the PATH NAME, so it is already in place
        // when the endpoint arrives rather than being the thing everyone forgets afterwards.
        using ServiceProvider provider = Build(new Dictionary<string, string?> { ["Otel:Endpoint"] = Endpoint });

        _ = provider.GetRequiredService<TracerProvider>();

        AspNetCoreTraceInstrumentationOptions options = provider
            .GetRequiredService<IOptionsMonitor<AspNetCoreTraceInstrumentationOptions>>()
            .Get(Options.DefaultName);

        options.Filter.Should().NotBeNull();
        options.Filter!(RequestFor("GET", "/health")).Should().BeFalse();

        // The other half of the gate: it must stay green on the traffic that IS the point of tracing this
        // server at all. A filter narrowed until it drops everything reports the same clean dashboard as one
        // that works.
        options.Filter!(RequestFor("POST", "/mcp")).Should().BeTrue();
        options.Filter!(RequestFor("GET", "/healthz")).Should().BeTrue();
    }

    private static HttpContext RequestFor(string method, string path)
    {
        DefaultHttpContext context = new();
        context.Request.Method = method;
        context.Request.Path = path;
        return context;
    }

    /// <summary>Answers exactly one request with a bare 200, then closes.</summary>
    /// <remarks>
    /// A real socket rather than a stubbed <see cref="HttpMessageHandler"/>, because the instrumentation reads
    /// <c>System.Net.Http</c>'s own <see cref="DiagnosticSource"/> events — which a replaced handler never
    /// raises. A test that swapped the handler out would export no spans and assert nothing.
    /// </remarks>
    private static async Task RespondOnceAsync(TcpListener listener)
    {
        using TcpClient accepted = await listener.AcceptTcpClientAsync();
        await using NetworkStream stream = accepted.GetStream();

        byte[] buffer = new byte[4096];
        _ = await stream.ReadAsync(buffer);

        byte[] response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");

        await stream.WriteAsync(response);
        await stream.FlushAsync();
    }
}
