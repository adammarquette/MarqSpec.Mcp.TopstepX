using System.Collections;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Telemetry;
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
/// <para>
/// <b>In <see cref="HostTelemetryCollection"/> because a <c>TracerProvider</c> built here subscribes to
/// <c>MarqSpec.Mcp.TopstepX</c> process-wide.</b> An <see cref="System.Diagnostics.ActivityListener"/> is
/// global, so while one of these tests holds a provider, a suite asserting that nothing listens to the
/// app-owned source sees one that does — and fails somewhere else entirely (gh#536).
/// </para>
/// </remarks>
[Collection(HostTelemetryCollection.Name)]
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
    public void TheAppOwnedMeterAndActivitySourceAreBothSubscribed()
    {
        // THE SUBSCRIPTION END OF gh#536, AND IT IS THE HALF THAT WAS OPEN.
        //
        // HostTelemetryTests pins that each instrument records the right measurement with the right tags,
        // against a collector the TEST attaches. Nothing pinned that the PIPELINE attaches one. Delete
        // `.AddSource(HostTelemetry.Name)` or `.AddMeter(HostTelemetry.Name)` from ConfigureTelemetry and
        // every instrument and both spans stop reaching the exporter -- no metric, no span, and no error
        // anywhere, because an unlistened instrument is silent BY DESIGN. That is the exact failure
        // HostTelemetryTests' own header warns about ("a meter the pipeline never subscribes to"), avoided
        // at the instrument end and left open at this one. The compose-stack measurement on the pull request
        // proved it worked that day; this is what makes it survive the next edit to that method.
        //
        // ONE TEST FOR BOTH SIGNALS, deliberately. They are two lines in one method and they are deleted or
        // mistyped by the same edit; splitting them would double the fixture and pin nothing extra.
        // SPANS UNDER A LOCK, METRICS NOT, and the asymmetry is the point: this provider's ActivityListener is
        // process-global, so any suite in another xUnit collection that serves a request or makes one exports
        // ITS spans into this collection too, from its own thread (gh#591). Nothing writes the metric list but
        // the ForceFlush below, on this thread — the in-memory metric reader has no timer of its own.
        ExportedSpans spans = new();
        List<Metric> metrics = [];

        using ServiceProvider provider = Build(
            new Dictionary<string, string?> { ["Otel:Endpoint"] = Endpoint },
            alsoRegister: services =>
            {
                services.ConfigureOpenTelemetryTracerProvider(
                    tracing => tracing.AddInMemoryExporter(spans));
                services.ConfigureOpenTelemetryMeterProvider(
                    meterProvider => meterProvider.AddInMemoryExporter(metrics));
            });

        // Resolving them is what starts the listeners. The container owns both; disposing either here would
        // shut the pipeline down before the measurement that is the point of the test.
        _ = provider.GetRequiredService<TracerProvider>();
        MeterProvider meters = provider.GetRequiredService<MeterProvider>();

        // THE SINGLETON THE HOST REGISTERED, not one this test built. A fresh HostTelemetry would carry its
        // own Meter, and AddMeter matches by NAME -- so the assertion would pass over a composition root
        // that never registered the singleton at all.
        HostTelemetry telemetry = provider.GetRequiredService<HostTelemetry>();

        telemetry.CacheRead(CacheSeries.Bars, "ES", 5, CacheOutcome.Hit);

        using (telemetry.VenueCall(VenueOperation.GetBars))
        {
        }

        // Metrics are batched; nothing is exported until the provider is asked to flush.
        //
        // The RETURN VALUE IS DELIBERATELY IGNORED. This host registers the OTLP exporter too, pointed at an
        // endpoint nothing is listening on, so the aggregate flush reports `false` for that reader while the
        // in-memory one beside it has exported perfectly well. Asserting on it would pin "a collector is
        // reachable", which is a compose-stack fact and not this test's claim.
        meters.ForceFlush();

        spans.Snapshot().Should().Contain(
            span => span.Source.Name == HostTelemetry.Name,
            "ConfigureTelemetry must AddSource(HostTelemetry.Name), or every venue and cache-aside span this "
            + "repository owns is dropped silently");

        metrics.Should().Contain(
            metric => metric.MeterName == HostTelemetry.Name
                && metric.Name == HostTelemetry.CacheReadsInstrument,
            "ConfigureTelemetry must AddMeter(HostTelemetry.Name), or every instrument this repository owns "
            + "is dropped silently");
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

        ExportedSpans exported = new();

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

        // Resolving it is what subscribes the ActivityListener. The container owns it, and it is disposed
        // BELOW rather than here: shutting the pipeline down before the request would leave nothing to assert
        // over, and leaving it running past the assertion is the race gh#591 was filed for.
        TracerProvider tracer = provider.GetRequiredService<TracerProvider>();

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

        // DRAINED BEFORE READ, AND READ FROM A SNAPSHOT — the two halves of gh#591, and neither is a timeout.
        //
        // Disposing the provider shuts the pipeline down: the process-global ActivityListener is unsubscribed,
        // so no further span — this test's, or one another suite happens to be emitting — can be exported into
        // `exported` after this line. The container disposes it moments later anyway, so this pays nothing; it
        // only moves the shutdown to BEFORE the assertion instead of after it.
        //
        // The snapshot is what makes the race IMPOSSIBLE rather than unlikely: it is taken under the same lock
        // every exporter write takes, so even a span already inside the exporter when Dispose was called
        // cannot be appended while the loop below is walking. Enumerating the live collection and hoping the
        // pipeline is quiet is exactly what reddened two unrelated pull requests.
        tracer.Dispose();

        IReadOnlyList<Activity> spans = exported.Snapshot();

        spans.Should().NotBeEmpty(
            "the HttpClient instrumentation must be subscribed at all, or this test asserts over an empty list "
            + "and passes forever");

        foreach (Activity activity in spans)
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

    /// <summary>
    /// The collection an in-memory span exporter writes into — every write and every read under one lock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A bare <see cref="List{T}"/> here is a race, not a slow machine (gh#591).</b> A provider built by
    /// <see cref="Build"/> subscribes a <b>process-global</b> <see cref="ActivityListener"/> to
    /// <c>Microsoft.AspNetCore</c> and <c>System.Net.Http</c>. Every span any suite in <i>another</i> xUnit
    /// collection produces while one of these tests holds a provider is therefore exported into that test's
    /// collection as well, appended <b>from that suite's thread</b> — and
    /// <see cref="List{T}"/> enumerated under a concurrent <c>Add</c> throws
    /// <see cref="InvalidOperationException"/> ("Collection was modified"). Measured on the full unit suite in
    /// Release: <b>33</b> spans arrived where <see cref="NoRequestHeaderReachesASpanAttribute"/> made
    /// <b>one</b> request, 32 of them foreign — chiefly the real Kestrel host and real
    /// <see cref="System.Net.Http.HttpClient"/> that <c>HealthEndpointTests</c>, <c>OAuthBearerGateTests</c>
    /// and <c>ProtectedResourceMetadataTests</c> run.
    /// </para>
    /// <para>
    /// <b><see cref="HostTelemetryCollection"/> cannot close this and is not meant to.</b> It serialises the
    /// suites that <i>subscribe</i> a listener; the writers here are the suites that merely <i>emit</i>, which
    /// is every suite that touches HTTP.
    /// </para>
    /// <para>
    /// <see cref="GetEnumerator"/> hands back a snapshot too, so a later assertion written as a bare
    /// <c>foreach</c> over this collection is safe by construction rather than by remembering to call
    /// <see cref="Snapshot"/>.
    /// </para>
    /// </remarks>
    private sealed class ExportedSpans : ICollection<Activity>
    {
        private readonly Lock _gate = new();
        private readonly List<Activity> _spans = [];

        /// <summary>How many spans have been exported so far.</summary>
        public int Count
        {
            get
            {
                lock (_gate)
                {
                    return _spans.Count;
                }
            }
        }

        /// <summary>Always <see langword="false"/> — the exporter writes to this.</summary>
        public bool IsReadOnly => false;

        /// <summary>The spans exported so far, as a list the exporter cannot append to.</summary>
        /// <returns>A copy taken under the same lock every write takes.</returns>
        public IReadOnlyList<Activity> Snapshot()
        {
            lock (_gate)
            {
                return [.. _spans];
            }
        }

        /// <summary>Records one exported span.</summary>
        /// <param name="item">The span the exporter is handing over.</param>
        public void Add(Activity item)
        {
            lock (_gate)
            {
                _spans.Add(item);
            }
        }

        /// <summary>Drops every span recorded so far.</summary>
        public void Clear()
        {
            lock (_gate)
            {
                _spans.Clear();
            }
        }

        /// <summary>Whether a span has been exported.</summary>
        /// <param name="item">The span to look for.</param>
        /// <returns><see langword="true"/> if it is present.</returns>
        public bool Contains(Activity item) => Snapshot().Contains(item);

        /// <summary>Copies the exported spans into an array.</summary>
        /// <param name="array">The destination.</param>
        /// <param name="arrayIndex">Where in the destination to start.</param>
        public void CopyTo(Activity[] array, int arrayIndex)
        {
            lock (_gate)
            {
                _spans.CopyTo(array, arrayIndex);
            }
        }

        /// <summary>Removes one exported span.</summary>
        /// <param name="item">The span to remove.</param>
        /// <returns><see langword="true"/> if it was present.</returns>
        public bool Remove(Activity item)
        {
            lock (_gate)
            {
                return _spans.Remove(item);
            }
        }

        /// <summary>Enumerates a snapshot, never the live list.</summary>
        /// <returns>An enumerator over a copy.</returns>
        public IEnumerator<Activity> GetEnumerator() => Snapshot().GetEnumerator();

        /// <inheritdoc/>
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
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
