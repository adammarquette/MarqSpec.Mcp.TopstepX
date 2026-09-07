using System.Reflection;
using MarqSpec.Client.ProjectX.DependencyInjection;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.Embeddings;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using MarqSpec.Mcp.TopstepX.Tools;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace MarqSpec.Mcp.TopstepX;

/// <summary>The composition root.</summary>
public static class Program
{
    /// <summary>
    /// Where the stdio transport listens when nothing names an address: loopback, port assigned by the OS.
    /// </summary>
    private const string StdioLoopbackAddress = "http://127.0.0.1:0";

    /// <summary>
    /// The MCP SDK's own activity source and meter — one name, carrying both.
    /// </summary>
    /// <remarks>
    /// Read off <c>ModelContextProtocol.Core</c> 2.2.0's assembly rather than assumed, and it is called
    /// <i>Experimental</i> because it is: per-request spans tagged <c>mcp.method.name</c>,
    /// <c>mcp.session.id</c> and <c>mcp.protocol.version</c>, plus <c>mcp.server.session.duration</c>, all of
    /// which may be renamed on an SDK bump. ADR-0019 accepts that — subscribing is nearly free — and names
    /// gh#536's app-owned instruments as the stable surface a dashboard should be built on instead.
    /// </remarks>
    private const string McpTelemetryName = "Experimental.ModelContextProtocol";

    /// <summary>Npgsql's meter. Its activity source is subscribed by the driver's own extension.</summary>
    private const string NpgsqlTelemetryName = "Npgsql";

    /// <summary>
    /// The health probe's path — the one request not worth a span.
    /// </summary>
    /// <remarks>
    /// A path name rather than a reference to an endpoint, because there is no endpoint yet: gh#513 owns it.
    /// </remarks>
    private const string HealthProbePath = "/health";

    /// <summary>Runs the server, or a CLI verb.</summary>
    /// <param name="args">Command-line arguments.</param>
    /// <returns>The process exit code.</returns>
    public static async Task<int> Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

        McpOptions mcp = builder.Configuration.GetSection(McpOptions.SectionName).Get<McpOptions>()
            ?? new McpOptions();

        ConfigureLogging(builder, mcp.Transport);

        // Beside ConfigureLogging and after it, because it is the same subject seen from further out: the
        // console is where lines go, this is where they go BEYOND the console (ADR-0019). Bound here rather
        // than resolved from DI for the same reason McpOptions is — the providers have to be wired before
        // Build(), and there is no container yet.
        ConfigureTelemetry(
            builder,
            builder.Configuration.GetSection(OtelOptions.SectionName).Get<OtelOptions>() ?? new OtelOptions());

        // Before Build(), because it is the builder that carries the address into Kestrel (gh#392).
        ConfigureDefaultBinding(builder, mcp.Transport);
        ConfigureServices(builder, mcp);

        WebApplication app = builder.Build();

        // A CLI verb rather than a separate script, so the rebuild path cannot drift from the code it re-runs
        // (ADR-0006).
        if (args.Length > 0 && string.Equals(args[0], "rebuild-indicators", StringComparison.Ordinal))
        {
            return await RebuildIndicatorsAsync(app, args).ConfigureAwait(false);
        }

        // Beside rebuild-indicators and above StoreAvailabilityHolder.Set for the same reason the precedent
        // is: the verb validates its symbol against InstrumentRegistry directly and exits the process, so
        // the holder the MCP tool surface consults is never set on this path. It does NOT skip the
        // migration the way the rebuild does -- see ReselectBarsAsync (gh#506).
        if (args.Length > 0 && string.Equals(args[0], "reselect-bars", StringComparison.Ordinal))
        {
            return await ReselectBarsAsync(app, args).ConfigureAwait(false);
        }

        // The result is published into DI rather than thrown: the tools that need a store ask it, and the
        // ones that do not are unaffected.
        StoreAvailability store = await MigrateAsync(app).ConfigureAwait(false);
        app.Services.GetRequiredService<StoreAvailabilityHolder>().Set(store);

        // Probed once, after the store is known, because availability means a key AND somewhere to put the
        // vector. Doing it lazily would put a database round trip inside a tool call.
        app.Services.GetRequiredService<EmbeddingAvailabilityHolder>()
            .Set(await ProbeEmbeddingsAsync(app, store).ConfigureAwait(false));

        if (mcp.Transport == McpTransport.Http)
        {
            MapHttpTransport(app, mcp);
        }

        // Both transports run through the same call. The shutdown-during-startup race it absorbs is reachable
        // from either -- it is a property of the host, not of stdio -- and a second `RunAsync` here is how the
        // HTTP branch would quietly keep the old behaviour (gh#76).
        //
        // Stdio needs no branch of its own -- and NOT because the host never starts a listener. It does:
        // `WebApplication` always adds Kestrel as a hosted service and always starts it, under both
        // transports. Under stdio Kestrel is simply not the transport, nothing is mapped in front of it, and
        // the session runs over stdin and stdout (ADR-0007).
        return await RunHostAsync(app).ConfigureAwait(false);
    }

    /// <summary>Builds the HTTP transport's request pipeline, in the one order that is correct.</summary>
    /// <param name="app">The built host.</param>
    /// <param name="mcp">The transport options, carrying which authentication mode the gate runs in.</param>
    /// <remarks>
    /// One method rather than a few lines inline, so the ordering is a thing a test can call. It is the
    /// ordering that carries the whole of the carve-out, and nothing about reading the calls tells you that.
    /// </remarks>
    public static void MapHttpTransport(WebApplication app, McpOptions mcp)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(mcp);

        // BEFORE the gate, and it works only because it is a terminal branch rather than a mapped endpoint:
        // `WebApplication` runs every endpoint after every middleware whatever order they were added in, so
        // a MapGet here would be answered 401 by the gate below and never reached. An ALB target-group probe
        // carries no credential and is what this is for (gh#513, ADR-0021). Under EITHER mode: the load
        // balancer has no credential under OAuth any more than it had a static token.
        app.UseHealthEndpoint();

        // BEFORE MapMcp, so the gate sits in front of the endpoint rather than beside it. Options validation
        // already refuses to start the HTTP transport with no mode configured, or with both; this is what
        // makes that requirement mean something at request time (ADR-0007). The gate is global in both
        // modes, and the only things past it are the terminal branches above it.
        if (mcp.Auth.Mode == McpAuthMode.OAuth)
        {
            // The RFC 9728 document the 401 names; a connector reads it before it has a token, so it is the
            // second terminal branch in front of the gate and the last one (gh#512).
            app.UseProtectedResourceMetadata(mcp.OAuth);
            app.UseOAuthBearerGate(mcp.OAuth);
        }
        else
        {
            app.UseBearerTokenGate(mcp.HttpBearerToken);
        }

        app.MapMcp(McpOptions.McpEndpointPath);
    }

    /// <summary>Runs the built host, treating a shutdown asked for during startup as a shutdown.</summary>
    /// <param name="app">The built host.</param>
    /// <returns>The process exit code — always 0, because reaching here is a clean stop.</returns>
    /// <remarks>
    /// <para>
    /// <b>The bug this closes (gh#76).</b> <c>docker run --rm &lt;image&gt;</c> without <c>-i</c> — the ordinary
    /// way an operator checks that an image starts — hands the container an already-closed stdin. The stdio
    /// transport reads EOF immediately, completes, and asks the host to stop; the host is still inside
    /// <c>StartAsync</c>, which runs the remaining hosted services against a token linked to
    /// <c>ApplicationStopping</c>. So Kestrel's <c>BindAsync</c> is cancelled and throws, nothing catches it,
    /// and the runtime aborts: unhandled <c>TaskCanceledException</c>, exit 139 (128 + SIGSEGV), measured
    /// three times out of three. It is a race — hold stdin open a couple of seconds and the same image starts,
    /// answers <c>initialize</c> and <c>tools/list</c>, and exits 0.
    /// </para>
    /// <para>
    /// <b>Which of gh#76's two options this is, and why it is neither.</b> The issue offered "do not start
    /// Kestrel under stdio" or "defer the shutdown request until <c>StartAsync</c> completes". Both were
    /// rejected, and what is here instead is narrower than either.
    /// </para>
    /// <para>
    /// <i>Not starting Kestrel</i> treats the symptom's location as its cause. <see cref="WebApplication"/>
    /// always adds <c>GenericWebHostService</c>, so avoiding it means a second host type for stdio and a
    /// composition root that forks — against ADR-0007's one registration, one tool set, two ways in. And it
    /// would not fix the class: <i>any</i> hosted service that starts after the transport meets the same
    /// cancelled token. Kestrel is merely the one that is there today.
    /// </para>
    /// <para>
    /// <i>Deferring the request</i> is worse on its own terms. EOF on stdin means the client is gone; there is
    /// nothing left to serve, so finishing startup first would bind a port for a session that does not exist,
    /// purely to reach the same stop. It also risks a hang, which
    /// <c>scripts/check-image-entrypoint.sh</c> names as its reason for hard-bounding the run.
    /// </para>
    /// <para>
    /// <b>So: honour the request, and record that honouring it is not a failure.</b> The filter matches on two
    /// facts rather than one — this host <b>has been asked to stop</b>, <b>and</b> no
    /// <see cref="BackgroundService"/> it started has faulted.
    /// </para>
    /// <para>
    /// <b>Why the second fact is there.</b> "A stop was requested" on its own is a permissive discriminator:
    /// <see cref="IHostApplicationLifetime.StopApplication"/> is called by success and by failure alike. Under
    /// stdio the only caller is the SDK's <c>SingleSessionMcpServerHostedService</c>, and it reaches that call
    /// by two routes — its read loop completing on EOF, which is gh#76 and is clean, or its
    /// <c>ExecuteAsync</c> <b>faulting</b> after its first await, which the host's default
    /// <see cref="BackgroundServiceExceptionBehavior.StopHost"/> turns into a <c>crit</c> log line followed by
    /// that same <c>StopApplication()</c>. The two leave identical state, so discriminating on the state alone
    /// exits 0 for a server that faulted and never served: <c>docker run</c> prints success and
    /// <c>restart: on-failure</c> does not restart. The fault is therefore <b>observed</b> —
    /// <see cref="BackgroundService.ExecuteTask"/> is read — never assumed.
    /// </para>
    /// <para>
    /// <b>What this still does not distinguish.</b> A cancellation raised while a stop is pending is swallowed
    /// whatever asked for the stop, provided no background service faulted; the faulted background service is
    /// singled out because it is the one failing cause the host itself makes observable. Every
    /// non-cancellation startup failure — a port already in use, a broken migration, a captive dependency —
    /// still propagates and still fails the process, as does any cancellation with no stop pending. Widening
    /// this to a bare <c>catch (OperationCanceledException)</c> would turn a genuinely failed startup into a
    /// silent exit 0, which is the one outcome worse than the crash.
    /// </para>
    /// </remarks>
    public static async Task<int> RunHostAsync(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // BOTH OF THESE ARE RESOLVED BEFORE THE RUN, AND THAT IS NOT TIDINESS.
        //
        // `RunAsync` disposes the host in its own `finally`. Across an `await` the callee's finally runs
        // BEFORE the exception is re-thrown at the await point -- the two-pass ordering that would put filters
        // first does not survive an async boundary -- so by the time the catch below is reached, and by the
        // time its FILTER is evaluated, the service provider is already disposed.
        //
        // Touching `app.Lifetime` or `app.Services` from there throws ObjectDisposedException. In the filter
        // that is silent: an exception thrown inside an exception filter is swallowed and the filter reads as
        // "does not match", so the fix compiles, looks right, and restores the crash exactly. Measured, not
        // reasoned -- it is how the first version of this method failed.
        CancellationToken stopping = app.Lifetime.ApplicationStopping;
        ILogger logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("startup");

        // AND SO IS THIS, for that reason and one more.
        //
        // Hosted services are singletons, so these are the very instances the host is about to start --
        // resolving them here constructs them a moment earlier than the host would and changes nothing else.
        // Resolving them after the run is not an option, for the reason above; and a BackgroundService only
        // publishes its `ExecuteTask` once it has been started, so the reference has to be taken first and the
        // task read last.
        IHostedService[] hosted = [.. app.Services.GetServices<IHostedService>()];

        try
        {
            await app.RunAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested && !AnyFaulted(hosted))
        {
            // The framework has already logged its own "Hosting failed to start" at fail level, naming
            // Kestrel. That line is true and useless -- it names the service that noticed, not the thing that
            // happened -- so this says what actually happened, in the terms the operator can act on.
            logger.LogInformation(
                "Shutdown was requested before the server finished starting, so it stopped without "
                + "listening. On stdio this is what `docker run` WITHOUT `-i` looks like: stdin is closed "
                + "before the handshake, so there is no client to serve. Pass `-i` (or start the server "
                + "from an MCP client, which holds stdin open) to keep a session. No background service "
                + "faulted on the way here, so this exit code is not covering for one.");
        }

        return 0;
    }

    /// <summary>Reports whether any background service this host started ended in a fault.</summary>
    /// <param name="hosted">The hosted services, resolved before the run.</param>
    /// <returns>
    /// <c>true</c> if at least one of them is a <see cref="BackgroundService"/> whose
    /// <see cref="BackgroundService.ExecuteTask"/> has faulted.
    /// </returns>
    /// <remarks>
    /// Called from an exception filter, so it must not throw: every member it touches is a plain property read
    /// on objects resolved before the host was disposed. An exception thrown inside a filter is swallowed and
    /// reads as "does not match", which here would silently restore the crash — the same trap the comment in
    /// <see cref="RunHostAsync"/> records.
    /// </remarks>
    private static bool AnyFaulted(IHostedService[] hosted)
    {
        foreach (IHostedService service in hosted)
        {
            if (service is BackgroundService { ExecuteTask.IsFaulted: true })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Wires logging, which differs by transport and is the single most common way a .NET MCP server fails.
    /// </summary>
    /// <param name="builder">The host builder.</param>
    /// <param name="transport">The configured transport.</param>
    /// <remarks>
    /// <b>On stdio, stdout IS the protocol.</b> Anything else written there corrupts the JSON-RPC frame, and
    /// the failure surfaces as an opaque handshake or parse error that names neither logging nor stdout. The
    /// default console logger writes to stdout, so this is not a hypothetical: a server that passes every unit
    /// test fails to connect at all.
    /// <para>
    /// So every provider is cleared and a single stderr console logger is added. Clearing rather than adding
    /// is deliberate — an inherited provider from configuration would be just as fatal as the default one.
    /// </para>
    /// </remarks>
    public static void ConfigureLogging(WebApplicationBuilder builder, McpTransport transport)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (transport != McpTransport.Stdio)
        {
            return;
        }

        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
    }

    /// <summary>
    /// Subscribes the sources that already exist and exports them as OTLP — or, with no endpoint configured,
    /// does nothing at all.
    /// </summary>
    /// <param name="builder">The host builder.</param>
    /// <param name="otel">The telemetry settings, already bound from the <c>Otel</c> section.</param>
    /// <remarks>
    /// <para>
    /// <b>OTLP and no other form (ADR-0019).</b> Traces, metrics and logs leave this host over one exporter,
    /// to a collector that decides which backend they reach — so a backend swap is a deployment edit rather
    /// than a code change, and the words Loki, Tempo, Prometheus and CloudWatch appear nowhere below.
    /// </para>
    /// <para>
    /// <b>Nothing here writes a source of its own.</b> Every signal is a subscription to something that is
    /// already emitting and going unread: the MCP SDK's <c>Experimental.ModelContextProtocol</c> activity
    /// source and meter, Npgsql's, ASP.NET Core's, HttpClient's and the runtime's. App-owned meters and spans
    /// — cache hit/miss, venue calls, hub reconnects — are gh#536, and they are the surface a dashboard that
    /// must not break is built on: the SDK's is named <i>Experimental</i> and its instrument names may move on
    /// a bump.
    /// </para>
    /// <para>
    /// <b>Logs go through <see cref="ILogger{TCategoryName}"/> exactly as they already do.</b> The
    /// OpenTelemetry logging provider attaches to the factory, so not one of the ~60 log sites changes, and
    /// each record written while one of those spans is current is stamped with its trace and span id. That
    /// stamp is the whole point — it is what lets a slow span lead to its log lines and back.
    /// </para>
    /// <para>
    /// <b>NO CONSOLE EXPORTER, UNDER ANY TRANSPORT, BEHIND NO FLAG.</b> Under stdio stdout IS the protocol
    /// frame (R-5.5), so telemetry written there does not degrade the trace — it corrupts the handshake, and
    /// surfaces as an opaque protocol error naming neither telemetry nor stdout. The package is not
    /// referenced either (ADR-0019, invariant 4).
    /// </para>
    /// <para>
    /// <b>An absent endpoint returns before anything is registered.</b> Not a disabled exporter and not a
    /// provider with no processor: no exporter thread, no retry queue, no startup warning about a collector
    /// that is not there. That is ADR-0007's degradation rule applied to a fourth dependency, and it is what
    /// keeps a stdio session on a laptop exactly as quiet as it is today.
    /// </para>
    /// </remarks>
    public static void ConfigureTelemetry(WebApplicationBuilder builder, OtelOptions otel)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(otel);

        if (!otel.IsConfigured)
        {
            return;
        }

        Uri endpoint = otel.ResolveEndpoint();
        OtlpExportProtocol protocol = otel.ResolveProtocol();
        string headers = otel.Headers;
        string serviceName = otel.ResolveServiceName();
        string serviceVersion = ServiceVersion();

        // One local, applied to all three exporters, so a protocol or a header set can never be right for
        // traces and wrong for logs.
        void ConfigureExporter(OtlpExporterOptions options)
        {
            options.Endpoint = endpoint;
            options.Protocol = protocol;

            if (!string.IsNullOrWhiteSpace(headers))
            {
                options.Headers = headers;
            }
        }

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                serviceName: serviceName,
                serviceVersion: serviceVersion))
            .WithTracing(tracing =>
            {
                tracing
                    // FIRST, and the only one of these named by this repository. The rest are subscriptions
                    // to sources that were already emitting; this one is the venue calls and the cache-aside
                    // reads gh#536 added, and it is the source a `tools/call` trace hangs its detail off.
                    .AddSource(HostTelemetry.Name)
                    .AddSource(McpTelemetryName)
                    .AddAspNetCoreInstrumentation(options => options.Filter = IsTraced)
                    .AddHttpClientInstrumentation();

                // FULLY QUALIFIED, and it has to be. `AddNpgsql` is also the name of EF Core's
                // IServiceCollection extension, whose namespace is imported at the top of this file, so the
                // unqualified call binds to that one and fails asking for a connection string — an error that
                // names a parameter this line has no business having.
                Npgsql.TracerProviderBuilderExtensions.AddNpgsql(tracing);

                tracing.AddOtlpExporter(ConfigureExporter);
            })
            .WithMetrics(metrics =>
            {
                metrics
                    // THE STABLE SURFACE. The SDK's meter below is named `Experimental` by its own authors
                    // and may be renamed on a bump; a dashboard that must not break is built on this one
                    // (ADR-0019 decision 6).
                    .AddMeter(HostTelemetry.Name)
                    .AddMeter(McpTelemetryName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();

                // The driver's own subscription rather than a bare AddMeter(NpgsqlTelemetryName): it starts
                // Npgsql's metrics reporter as well as listening to the meter, and a meter nothing reports to
                // exports an empty set that reads exactly like an idle pool.
                Npgsql.MeterProviderBuilderExtensions.AddNpgsqlInstrumentation(metrics);

                metrics.AddOtlpExporter(ConfigureExporter);
            });

        builder.Logging.AddOpenTelemetry(logging =>
        {
            // Scopes and parsed state are what make a log record searchable beside the span it belongs to.
            // Without them a record arrives as a rendered sentence and the structured fields the call site
            // already passed — the instrument, the resolution, the window — are gone.
            logging.IncludeScopes = true;
            logging.ParseStateValues = true;

            logging.AddOtlpExporter(ConfigureExporter);
        });
    }

    /// <summary>Whether an ASP.NET Core request is worth a span.</summary>
    /// <param name="context">The incoming request.</param>
    /// <returns><see langword="false"/> for the health probe, <see langword="true"/> for everything else.</returns>
    /// <remarks>
    /// <para>
    /// One exclusion, and it is a volume argument rather than a privacy one: a load balancer probes
    /// <c>/health</c> every 30 seconds, which is 2,880 spans a day carrying no information, arriving in the
    /// same search results and on the same bill as the tool calls somebody is looking for.
    /// </para>
    /// <para>
    /// <b>Matched on the exact path, not a prefix.</b> A prefix match would silently swallow a future
    /// <c>/health/detail</c> — an endpoint whose whole purpose would be to be worth reading. gh#513 owns the
    /// endpoint itself and has not landed; this lands on the path name now so it is already in place when the
    /// endpoint arrives, rather than being the thing everyone forgets afterwards.
    /// </para>
    /// </remarks>
    private static bool IsTraced(HttpContext context) =>
        !context.Request.Path.Equals(HealthProbePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>The <c>service.version</c> resource attribute.</summary>
    /// <returns>The assembly's informational version, without build metadata.</returns>
    /// <remarks>
    /// <para>
    /// <b>The assembly's own stamp, and it is present rather than meaningful.</b> ADR-0001 makes the tag the
    /// version and nothing declares one in a file; the container build never sees the repository's history, so
    /// inside the published image this reads <c>0.0.0-alpha.0</c> by decision, with the release number carried
    /// by the image tag and <c>org.opencontainers.image.version</c> instead.
    /// </para>
    /// <para>
    /// <b>gh#513's <c>Deployment__Version</c> is what will make it mean something on a deployed instance</b>,
    /// and it is deliberately not read here: that section does not exist yet, and a configuration key this
    /// server reads while no document describes it and <c>docker-compose.yml</c> does not forward it is a
    /// setting that silently does nothing in a container — the exact defect <c>.env.example</c>'s own header
    /// warns about. It is one line to add when that card lands.
    /// </para>
    /// </remarks>
    private static string ServiceVersion()
    {
        string? informational = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
        {
            return typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        }

        // MinVer appends "+<sha>" build metadata. A resource attribute is a grouping key, and a version that
        // changes with every commit groups nothing.
        int metadata = informational.IndexOf('+', StringComparison.Ordinal);
        return metadata < 0 ? informational : informational[..metadata];
    }

    /// <summary>
    /// Gives the stdio transport an ephemeral loopback address, unless one has been named explicitly.
    /// </summary>
    /// <param name="builder">The host builder.</param>
    /// <param name="transport">The transport that was selected.</param>
    /// <remarks>
    /// <para>
    /// <b>Kestrel still starts.</b> ADR-0007 settled that and gh#76 rejected not starting it; this changes
    /// which address it takes, not whether it takes one. Under stdio the listener serves nothing —
    /// <c>MapMcp</c> is inside the HTTP branch and the session runs over stdin and stdout — but it was still
    /// holding the framework default <c>http://localhost:5000</c>, exclusively, for the life of the process.
    /// </para>
    /// <para>
    /// So a second session could not start (gh#392): <c>IOException: Failed to bind to address
    /// http://127.0.0.1:5000: address already in use</c>, raised before any tool was reachable and saying
    /// nothing about stdio. That is an ordinary thing to do here — this repository runs parallel agent
    /// sessions by design — and :5000 is contended by a great many other dev servers besides.
    /// </para>
    /// <para>
    /// Port <b>0</b> rather than another well-known number, because any fixed choice is the same bug at a
    /// different address; loopback rather than any interface, because nothing should reach this listener.
    /// </para>
    /// <para>
    /// <b>An explicitly named address still wins</b>, under either transport — <c>ASPNETCORE_URLS</c>,
    /// <c>ASPNETCORE_HTTP_PORTS</c> and <c>ASPNETCORE_HTTPS_PORTS</c> (which <c>docker-compose.yml</c> uses
    /// to place the composed server on 8443, over TLS, since gh#416 — it named <c>ASPNETCORE_HTTP_PORTS</c>
    /// and 8080 until then). A default that overrode any of them would not be a default.
    /// </para>
    /// <para>
    /// Worth knowing while reading the three variables above: <c>mcr.microsoft.com/dotnet/aspnet:10.0</c>
    /// sets <c>ASPNETCORE_HTTP_PORTS=8080</c> in the image, so inside the container an address is <i>always</i>
    /// named and this method's stdio default never applies. Outside it — a client launching
    /// <c>dotnet run</c> — nothing names one and the ephemeral loopback address is what gh#392 needs.
    /// </para>
    /// </remarks>
    public static void ConfigureDefaultBinding(WebApplicationBuilder builder, McpTransport transport)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // The HTTP transport is the one that is meant to be reachable, and it is told where to listen by
        // configuration. Leave the framework's own defaulting alone.
        if (transport == McpTransport.Http)
        {
            return;
        }

        if (HasExplicitAddress(builder.Configuration))
        {
            return;
        }

        builder.WebHost.UseUrls(StdioLoopbackAddress);
    }

    /// <summary>Whether an address has been named by any of the ways the framework reads one.</summary>
    /// <param name="configuration">The configuration to read.</param>
    /// <returns><see langword="true"/> when the operator has named an address.</returns>
    private static bool HasExplicitAddress(IConfiguration configuration)
        => !string.IsNullOrWhiteSpace(configuration[WebHostDefaults.ServerUrlsKey])
            || !string.IsNullOrWhiteSpace(configuration[WebHostDefaults.HttpPortsKey])
            || !string.IsNullOrWhiteSpace(configuration[WebHostDefaults.HttpsPortsKey]);

    /// <summary>Registers everything the server needs.</summary>
    /// <param name="builder">The host builder.</param>
    /// <param name="mcp">The transport options.</param>
    public static void ConfigureServices(WebApplicationBuilder builder, McpOptions mcp)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(mcp);

        IServiceCollection services = builder.Services;

        services.AddOptions<MarketDataOptions>()
            .Bind(builder.Configuration.GetSection(MarketDataOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<IndicatorOptions>()
            .Bind(builder.Configuration.GetSection(IndicatorOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // The detection defaults get_key_levels falls back to. Validated on start, and the source check is
        // an IValidatableObject on the type rather than a lambda here. Source binds as a STRING, not the
        // PivotSource enum (gh#468 closed the comma-separated-list hole Enum.Parse opened: HeikinAshiBody,Body
        // used to OR onto HighLow and boot on it). An absent key leaves the HeikinAshiBody initializer
        // standing; every other value reaches Validate exactly as typed, and the server boots only if
        // PivotSources.Resolve reads it, trimmed and case-insensitive, as one of the three names. A server
        // that booted on an unresolved source would answer every level call from a source nobody chose.
        services.AddOptions<KeyLevelDetectionOptions>()
            .Bind(builder.Configuration.GetSection(KeyLevelDetectionOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Validated on start so a malformed endpoint, protocol or header list refuses at boot NAMING THE KEY,
        // rather than at export time on a background thread — where the failure is a silent absence of
        // telemetry, which is indistinguishable from the supported unconfigured state (ADR-0019).
        services.AddOptions<OtelOptions>()
            .Bind(builder.Configuration.GetSection(OtelOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // How long startup waits for a store that is not answering yet. Zero by default, which is one probe
        // and no delay -- today's behaviour, and what every stdio launch relies on. Validated on start like
        // its siblings so a typo is refused rather than clamped (gh#514).
        services.AddOptions<StoreOptions>()
            .Bind(builder.Configuration.GetSection(StoreOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Under HTTP, exactly one authentication mode, complete — the token for StaticToken, the issuer,
        // client ids and resource URL for OAuth — and both directions of "both" refused, each naming its
        // key. The rules are McpOptions.Validate, an IValidatableObject like KeyLevelDetectionOptions, so
        // the token rule that used to be a lambda here now lives beside the ones it is exclusive with
        // (ADR-0021, gh#512). Nothing under Mcp:Auth or Mcp:OAuth is read under stdio.
        services.AddOptions<McpOptions>()
            .Bind(builder.Configuration.GetSection(McpOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // The JWT bearer handler the OAuth gate authenticates with, registered only in the mode that uses
        // it: the static mode's container is byte for byte what it was, and stdio's too.
        if (mcp.Transport == McpTransport.Http && mcp.Auth.Mode == McpAuthMode.OAuth)
        {
            services.AddOAuthBearerAuthentication(mcp.OAuth);
        }

        // What the liveness probe reports as `version` and `digest`. Optional, unvalidated, and defaulting to
        // "unknown": nothing here declares a version in a file (ADR-0001), so a running task can only be told
        // what it is by the deployment that started it — and a probe that refused to answer over a missing
        // stamp would fail a healthy task for a cosmetic reason (gh#513).
        services.AddOptions<DeploymentOptions>()
            .Bind(builder.Configuration.GetSection(DeploymentOptions.SectionName));

        services.AddSingleton(TimeProvider.System);

        // THE APP-OWNED METER AND ACTIVITY SOURCE (gh#536). A SINGLETON, and it has to be: a Meter is a
        // process-wide publisher, and a scoped one would create a new publisher per request -- every counter
        // starting from zero, and every subscriber having to discover a new instrument each time.
        //
        // REGISTERED HERE, UNCONDITIONALLY, whether or not Otel__Endpoint is set. An instrument nothing
        // listens to costs a predicate and a return, and an ActivitySource nothing listens to returns null
        // without allocating -- so there is no "is telemetry on" branch at any call site, and no
        // configuration under which the counted path and the uncounted path can diverge (ADR-0019).
        services.AddSingleton<HostTelemetry>();

        // Parsed once, at startup, and shared. It is a pure value, and parsing refuses a malformed session
        // close rather than guessing -- this value decides what counts as missing data.
        services.AddSingleton(sp =>
        {
            MarketDataOptions options = sp.GetRequiredService<IOptions<MarketDataOptions>>().Value;
            return BarSessionCalendar.Parse(options.SessionCloseCentral, options.HolidayList());
        });

        // After the calendar, because it is stated against one: every definition is re-checked here against
        // the same calendar MarketDataOptions.Validate used, so options that never went through
        // ValidateOnStart cannot put an off-grid session into the served vocabulary (ADR-0022).
        services.AddSingleton<SessionCatalog>();

        services.AddSingleton<InstrumentRegistry>();
        services.AddSingleton<IndicatorCatalog>();
        services.AddSingleton<IndicatorCatalogNames>();
        services.AddSingleton<LevelMethodCatalog>();
        services.AddSingleton<ToolGuards>();
        services.AddSingleton<StoreAvailabilityHolder>();
        services.AddSingleton<TapeAvailabilityHolder>();
        services.AddSingleton<EmbeddingAvailabilityHolder>();
        services.AddSingleton<EmbeddingAvailabilityProbe>();

        // Process-lifetime: IndicatorCacheService is scoped, so its Projections reset every request.
        // This is the count startup warmup will read, and the one an operator can read without a debugger
        // (the replay log line prints it; the property is the typed form) (gh#347).
        services.AddSingleton<IndicatorReadProjectionCounter>();

        // The embedding seam. CohereEmbeddingProvider is selected when Embeddings__ApiKey is set
        // (ADR-0009). An unset key is a supported state, so this is never a startup failure.
        services.AddOptions<EmbeddingOptions>()
            .Bind(builder.Configuration.GetSection(EmbeddingOptions.SectionName));

        EmbeddingOptions embeddings =
            builder.Configuration.GetSection(EmbeddingOptions.SectionName).Get<EmbeddingOptions>()
            ?? new EmbeddingOptions();

        if (embeddings.IsConfigured)
        {
            services.AddHttpClient<IEmbeddingProvider, CohereEmbeddingProvider>(client =>
            {
                client.BaseAddress = new Uri("https://api.cohere.com/");
                client.DefaultRequestHeaders.Authorization =
                    new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", embeddings.ApiKey);

                // Bounded on purpose. An embedding is an optional index over an observation, so a slow
                // provider must degrade to text search rather than hold a tool call open.
                client.Timeout = TimeSpan.FromSeconds(20);
            });

            // NO RedactLoggedHeaders CALL HERE, DELIBERATELY -- adding one would make this WORSE.
            //
            // IHttpClientFactory's default ShouldRedactHeaderValue redacts EVERY header, not none. Calling
            // RedactLoggedHeaders(["Authorization"]) replaces that predicate with an allow-list of one, which
            // keeps the bearer token safe and starts logging every other header in the clear. Measured on the
            // runtime this targets, not assumed: on a bare container an unconfigured client reports
            // Authorization, X-Api-Key and Accept all redacted; after such a call only Authorization is.
            //
            // TheEmbeddingKeyIsRedactedFromHttpLogging pins the property that actually matters -- this
            // client's Authorization header is redacted -- so a later narrowing that forgets it fails loudly
            // rather than leaking into a log from a public repository.
        }
        else
        {
            services.AddSingleton<IEmbeddingProvider, UnconfiguredEmbeddingProvider>();
        }

        services.AddScoped<EmbeddingWriter>();
        services.AddScoped<ObservationSearchService>();

        // The venue (gh#13). Configured means BOTH credentials present AND a data tier chosen; anything less
        // and the server still starts, serving everything that needs no venue, with the venue tools refusing
        // and saying why. A trading server that will not boot without credentials is one an operator cannot
        // inspect before configuring.
        VenueOptions venue = builder.Configuration.GetSection(VenueOptions.SectionName).Get<VenueOptions>()
            ?? new VenueOptions();

        services.AddOptions<VenueOptions>()
            .Bind(builder.Configuration.GetSection(VenueOptions.SectionName))
            .Validate(
                o => !o.IsConfigured || o.DataTier != ProjectXDataTier.Unspecified,
                "ProjectX__DataTier is required (Simulated or Live) whenever credentials are set, and has no "
                + "default on purpose: the WRONG tier returns an empty universe rather than an error, so a "
                + "silent default is indistinguishable from a missing instrument.")
            .ValidateOnStart();

        // SCOPED, not singleton, and the same lifetime on both branches.
        //
        // The vendor client registers IProjectXApiClient as SCOPED, so a singleton gateway consuming it is a
        // captive dependency -- the container refuses to build, and the process dies before the transport
        // exists. It only bites when credentials ARE configured, which is exactly the path a run without them
        // never reaches.
        //
        // Both branches use one lifetime deliberately. A lifetime that varies with configuration means the
        // container is a different shape in the configured case than in the unconfigured one, which is how
        // this got shipped: everything that ran locally ran unconfigured.
        // SINGLETON, and registered on BOTH branches. It memoises "does the venue list this contract id?"
        // across scopes, for the same reason the history pacer is shared: the vendor counts the lookup pool
        // (200 / 60s) against the credential, not against a request scope. It holds no gateway -- the scoped
        // one is passed in per call -- so it is safe here whether the venue is configured or not (ADR-0020).
        services.AddSingleton<ContractDirectory>();

        if (venue.IsConfigured && venue.DataTier != ProjectXDataTier.Unspecified)
        {
            services.AddProjectXApiClient(builder.Configuration);

            // SINGLETON, while the gateway consuming it is scoped. The vendor's 50-requests-per-30-seconds
            // allowance on History/retrieveBars is counted against the CREDENTIAL, so it has to be shared
            // across scopes -- a per-scope pacer would let every concurrent tool call burst to the cap
            // independently and none of them would know (gh#43).
            services.AddSingleton(sp => VenueRequestPacer.ForHistory(sp.GetRequiredService<TimeProvider>()));
            services.AddScoped<IMarketDataGateway, ProjectXMarketDataGateway>();
        }
        else
        {
            services.AddScoped<IMarketDataGateway, UnconfiguredMarketDataGateway>();
        }

        string connection = builder.Configuration.GetConnectionString("Default")
            ?? "Host=localhost;Port=5432;Database=topstepx_mcp;Username=topstepx;Password=changeme-local";

        services.AddDbContext<TopstepXDbContext>(options =>
            options.UseNpgsql(connection, npgsql => npgsql.UseVector()));

        services.AddScoped<IndicatorProjector>();
        services.AddScoped<IndicatorRebuilder>();
        services.AddScoped<FootprintProjector>();
        services.AddScoped<FootprintCacheService>();
        services.AddScoped<VolumeProfileService>();
        services.AddScoped<TapeVolumeFrontService>();
        // Scoped, and the lifetime is load-bearing rather than conventional: this service memoises the
        // instrument's contract universe, and the scope is one request. A singleton would carry a pre-roll
        // front across a quarterly roll and go on answering the ledger's per-contract question with a
        // contract that has since retired (gh#504).
        services.AddScoped<BarCacheService>();

        // The reselect-bars verb (gh#506). Scoped because BarCacheService is, and it re-decides a window
        // through that service's seam -- the only thing on this path that reaches the venue.
        services.AddScoped<BarReselector>();

        // After BarCacheService, which it reads the base series through -- and which is the only thing on
        // this path that can reach the venue (ADR-0022 §7).
        services.AddScoped<SessionBarService>();

        // The tape recorder. Always registered so the container shape does not depend on the
        // switch — ExecuteAsync returns immediately unless the transport is HTTP and
        // MarketData__RecordTape is on. It takes no scoped venue client in the constructor;
        // every operation opens a scope (the captive-dependency case, ADR-0016).
        services.AddHostedService<TradeTapeRecorder>();

        // Indicator warmup. Same shape: HTTP and an explicit switch, both. Always registered
        // so the container does not change with MarketData__WarmIndicators; ExecuteAsync
        // returns immediately unless the transport is HTTP and the switch is on. The
        // rebuilder is scoped, so the pass opens a scope (gh#350, ADR-0014).
        services.AddHostedService<IndicatorWarmup>();

        // Scoped, and the lifetime is load-bearing rather than conventional: this service memoises which
        // series it has already found complete, and the scope is one request. A singleton would remember the
        // answer past the fill that invalidated it (gh#246).
        services.AddScoped<IndicatorCacheService>();

        // The two collaborators the market-data tool types share, and the reason they are collaborators
        // rather than a base class (gh#414). InstrumentResolver holds the symbol lookup and the
        // store-availability check every one of those tools takes; VolumeFrontReader holds the front-month
        // read that get_footprint, get_volume_profile and get_contract_roll all publish. Injected, neither
        // InstrumentRegistry nor TapeVolumeFrontService is reachable from any tool type at all -- which is
        // the boundary the compiler now holds and a partial class could not.
        //
        // The resolver is a singleton because both of its dependencies are; the front reader is scoped
        // because TapeVolumeFrontService takes the DbContext.
        services.AddSingleton<InstrumentResolver>();
        services.AddScoped<VolumeFrontReader>();

        // The tool types themselves. The SDK activates a tool per call with ActivatorUtilities, which resolves
        // constructor parameters from DI but does NOT recursively activate unregistered types -- so a tool that
        // composes another tool (SnapshotTools takes BarTools, IndicatorTools, KeyLevelTools and
        // ReferenceTools) fails at CALL time with "unable to resolve service", while startup and tools/list
        // both look perfectly healthy.
        //
        // Registering them explicitly is what makes that a startup-time guarantee rather than a per-tool
        // surprise the first time someone calls the one composed tool. Five market-data types now instead of
        // one (gh#414): the guarantee is per TYPE, so splitting the type multiplied the number of
        // registrations this comment is about rather than weakening what any one of them promises.
        services.AddScoped<ReferenceTools>();
        services.AddScoped<BarTools>();
        services.AddScoped<IndicatorTools>();
        services.AddScoped<KeyLevelTools>();
        services.AddScoped<TapeTools>();
        services.AddScoped<ContractRollTools>();
        services.AddScoped<AccountTools>();
        services.AddScoped<SnapshotTools>();
        services.AddScoped<ObservationTools>();
        services.AddScoped<SessionBarTools>();

        // One registration, one tool set, two ways in (ADR-0007). The transport is the only thing that
        // differs, and it is chosen here rather than by a second AddMcpServer call — registering the server
        // twice would build two of everything and leave which one answers to configuration order.
        IMcpServerBuilder server = services.AddMcpServer()
            .WithToolsFromAssembly(typeof(Program).Assembly)

            // The store-fault boundary (gh#89). ON THE SERVER, NOT ON A TOOL: every tools/call goes through
            // this pipeline, so a tool added tomorrow is covered by having been registered rather than by its
            // author remembering a try/catch. A guard written into a tool covers that tool, which is how a
            // 23505 from two overlapping fills reached a caller of get_bars as a raw DbUpdateException while
            // the one method that translated anything sat two tools away.
            .WithRequestFilters(filters => filters.AddCallToolFilter(StoreFaultGuard.Filter));

        if (mcp.Transport == McpTransport.Http)
        {
            server.WithHttpTransport();
        }
        else
        {
            server.WithStdioServerTransport();
        }
    }

    /// <summary>
    /// Brings the schema up to date, and reports whether the store is usable at all.
    /// </summary>
    /// <param name="app">The built host.</param>
    /// <returns>What to tell callers about the store.</returns>
    /// <remarks>
    /// <para>
    /// Two failures live here and they are not the same fact.
    /// </para>
    /// <para>
    /// <b>Unreachable</b> — nothing answered on the connection string. That is an environment fact, usually
    /// "Postgres is not running yet", and it is survivable: the server starts, the tool list is real, and the
    /// tools that need no store still work. Crashing instead would reach an MCP client as a bare transport
    /// failure, which says nothing about databases.
    /// </para>
    /// <para>
    /// <b>Broken</b> — the database answered and the migration itself failed. That is a defect in this
    /// repository, and it still fails the process. Degrading there would leave the server answering reads
    /// against a schema nobody has verified, which is worse than not starting.
    /// </para>
    /// </remarks>
    public static async Task<StoreAvailability> MigrateAsync(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        using IServiceScope scope = app.Services.CreateScope();
        TopstepXDbContext database = scope.ServiceProvider.GetRequiredService<TopstepXDbContext>();
        ILogger logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("startup");

        // The probe, the warning that names what it tried, and the bounded retry all live in StoreStartup so
        // the credential-bearing connection string is handled somewhere a unit test can assert the password
        // never reaches a log line (gh#514).
        //
        // `app.Lifetime.ApplicationStopping` rather than `CancellationToken.None`: this call runs before
        // `RunHostAsync`'s `app.RunAsync()`, so today nothing has started that could request a stop and the
        // token cannot fire AT THIS CALL SITE -- same reasoning `RunHostAsync` documents for its own read of
        // `app.Lifetime`. That is not the same as saying a changed ordering would be handled correctly: if
        // `MigrateAsync` ever moved to run after `StartAsync`, the token WOULD fire on a stop, and the
        // resulting `OperationCanceledException` would propagate out of `ReachAsync` -- which has no catch --
        // through this method and out of `Main`'s `await MigrateAsync(app)`, which sits BEFORE
        // `RunHostAsync` and therefore outside its `catch (OperationCanceledException) when (stopping...)`.
        // That is gh#76's crash shape, not its clean stop. Moving the call site would also need that catch
        // moved (or duplicated) to cover it. Passing the token now is still strictly better than
        // `CancellationToken.None` -- it costs nothing today and stops a 600-second wait from being
        // uncancellable by construction -- it just is not, on its own, a promise that a future ordering is
        // safe (PR #548 review, gh#551, gh#555 review).
        StoreAvailability reached = await StoreStartup.ReachAsync(
            token => database.Database.CanConnectAsync(token),
            database.Database.GetConnectionString(),
            TimeSpan.FromSeconds(
                scope.ServiceProvider.GetRequiredService<IOptions<StoreOptions>>().Value.StartupWaitSeconds),
            scope.ServiceProvider.GetRequiredService<TimeProvider>(),
            logger,
            app.Lifetime.ApplicationStopping).ConfigureAwait(false);

        if (!reached.IsAvailable)
        {
            return reached;
        }

        try
        {
            await database.Database.MigrateAsync().ConfigureAwait(false);
            return StoreAvailability.Available();
        }
        catch (Npgsql.NpgsqlException ex) when (ex.InnerException is System.Net.Sockets.SocketException)
        {
            // The database was there a moment ago and went away mid-migration -- still an environment fact,
            // not a schema defect, so it degrades rather than crashing.
            StoreAvailability unavailable =
                StoreAvailability.Unavailable("The connection dropped while applying migrations.");
            logger.LogWarning("{Explanation}", unavailable.Explanation);
            return unavailable;
        }
    }

    private static async Task<EmbeddingAvailability> ProbeEmbeddingsAsync(
        WebApplication app,
        StoreAvailability store)
    {
        using IServiceScope scope = app.Services.CreateScope();
        IServiceProvider sp = scope.ServiceProvider;

        return await sp.GetRequiredService<EmbeddingAvailabilityProbe>()
            .ProbeAsync(
                sp.GetRequiredService<IOptions<EmbeddingOptions>>().Value,
                store,
                sp.GetRequiredService<TopstepXDbContext>(),
                CancellationToken.None)
            .ConfigureAwait(false);
    }

    private static async Task<int> RebuildIndicatorsAsync(WebApplication app, string[] args)
    {
        try
        {
            using IServiceScope scope = app.Services.CreateScope();
            IServiceProvider sp = scope.ServiceProvider;

            // The loop itself lives in IndicatorRebuilder rather than here, so the verb can be run by a
            // test. A private static in the composition root cannot be, and this verb shipped in Phase 2
            // having never been executed anywhere.
            string? only = args.Length > 1 ? args[1] : null;

            await sp.GetRequiredService<IndicatorRebuilder>()
                .RebuildAsync(only, CancellationToken.None)
                .ConfigureAwait(false);

            return 0;
        }
        finally
        {
            await ShutDownLoggingAsync(app).ConfigureAwait(false);
        }
    }

    private static async Task<int> ReselectBarsAsync(WebApplication app, string[] args)
    {
        try
        {
            return await RunReselectAsync(app, args).ConfigureAwait(false);
        }
        finally
        {
            await ShutDownLoggingAsync(app).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Disposes the host a verb ran inside, so its report is not lost at process exit.
    /// </summary>
    /// <param name="app">The built host.</param>
    /// <returns>A task.</returns>
    /// <remarks>
    /// <para>
    /// <b>A verb returns from <c>Main</c> without ever calling <c>RunAsync</c>, so nothing else shuts the
    /// host down.</b> The console logger writes from a background thread through a queue, and the OTLP
    /// exporter batches on a timer; both flush on dispose and neither is guaranteed to have flushed when the
    /// process exits on its own. The whole report of these verbs is log lines — the rebuild's counts, the
    /// reselect's per-series numbers and its summary — so losing the tail is losing the answer, and it is
    /// exactly the last lines, the summary among them, that a truncated queue drops.
    /// </para>
    /// <para>
    /// In a <c>finally</c> around each verb body, and therefore on the refusal paths too: an operator who
    /// mistyped a symbol needs the line saying so at least as much as one whose run succeeded.
    /// </para>
    /// </remarks>
    private static ValueTask ShutDownLoggingAsync(WebApplication app) => app.DisposeAsync();

    private static async Task<int> RunReselectAsync(WebApplication app, string[] args)
    {
        // Both singletons, so they are taken from the root provider rather than from a scope: this runs
        // before the migration, and a scoped DbContext resolved here would be one nothing has verified a
        // schema for.
        ILogger logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("reselect-bars");

        ReselectArguments parsed;

        try
        {
            // BEFORE THE MIGRATION AND BEFORE ANYTHING TOUCHES THE STORE. A mistyped symbol or an inverted
            // window is the operator's to correct, and learning it after a schema has been applied and a
            // window's worth of provenance is halfway rewritten is the expensive way to be told.
            parsed = ReselectArguments.Parse(args, app.Services.GetRequiredService<InstrumentRegistry>());
        }
        catch (ArgumentException ex)
        {
            // Through the logger, not Console.Error: the stdio transport's logging is already configured
            // (ConfigureLogging), and a bare Console write would be the only one in this codebase -- on the
            // one transport where stdout is the protocol.
            logger.LogError("reselect-bars refused the command line: {Reason}", ex.Message);
            return ReselectExit.From(ex);
        }

        // THE MIGRATION RUNS FIRST HERE, WHERE rebuild-indicators SKIPS IT ENTIRELY, and the difference is
        // that this verb WRITES. A rebuild replays projections over bars already stored; a reselect rewrites
        // the bars' provenance, deletes the rows a new winner does not restate and drops coverage claims.
        // Doing that through a schema this build has not applied is a write nobody can reproduce, or a
        // failure halfway across a window.
        StoreAvailability store = await MigrateAsync(app).ConfigureAwait(false);

        if (!store.IsAvailable)
        {
            // Said in the verb's own terms rather than by quoting the tool surface's sentence: degrading is a
            // decision about READS -- the server starts, and the tools that need no store still answer. A
            // verb whose only purpose is to write has nothing to offer past this point, and the reason it
            // stopped is the operator's next step.
            logger.LogError(
                "reselect-bars cannot run: the store is unreachable or its migration did not complete "
                + "({Reason}). This verb only writes, so it stops here rather than degrading; nothing in the "
                + "window was re-decided. Bring the database up and run it again.",
                store.Explanation);

            return ReselectExit.Degraded;
        }

        using IServiceScope scope = app.Services.CreateScope();

        // Resolved OUTSIDE the try, deliberately: GetRequiredService throws InvalidOperationException for a
        // missing registration, and catching that here would report a container defect as a degraded venue
        // read. CompositionRootTests.TheReselectVerbCanBeResolved is what covers it instead.
        BarReselector reselector = scope.ServiceProvider.GetRequiredService<BarReselector>();

        try
        {
            // The report is the reselector's own log lines -- one per series and one summary naming both the
            // asked window and the whole trade dates it was widened to. Same shape as IndicatorRebuilder,
            // and for the same reason: under stdio, stdout carries the protocol.
            await reselector
                .ReselectAsync(parsed.Instrument, parsed.Window, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (ReselectPlanException ex)
        {
            // THE NARROW TYPE, not its base InvalidOperationException: EF Core raises that base for its own
            // defects, and catching it here would dress a bug in this repository up as a degraded venue plan
            // -- with a tidy exit 3, on a run that may already have committed an earlier series.
            logger.LogError(
                "reselect-bars could not re-decide the window: {Reason} Any series logged above this line "
                + "was committed before the run stopped.",
                ex.Message);

            return ReselectExit.From(ex);
        }

        return ReselectExit.Ok;
    }

}
