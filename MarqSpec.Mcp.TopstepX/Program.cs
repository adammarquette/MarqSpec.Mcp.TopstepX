using MarqSpec.Client.ProjectX.DependencyInjection;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.Embeddings;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Tools;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX;

/// <summary>The composition root.</summary>
public static class Program
{
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
        ConfigureServices(builder, mcp);

        WebApplication app = builder.Build();

        // A CLI verb rather than a separate script, so the rebuild path cannot drift from the code it re-runs
        // (ADR-0006).
        if (args.Length > 0 && string.Equals(args[0], "rebuild-indicators", StringComparison.Ordinal))
        {
            return await RebuildIndicatorsAsync(app, args).ConfigureAwait(false);
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
            // BEFORE MapMcp, so the gate sits in front of the endpoint rather than beside it. Options
            // validation already refuses to start the HTTP transport without a token; this is what makes that
            // requirement mean something at request time (ADR-0007).
            app.UseBearerTokenGate(mcp.HttpBearerToken);
            app.MapMcp("/mcp");
            await app.RunAsync().ConfigureAwait(false);
        }
        else
        {
            // Stdio: the host still builds, it simply never listens.
            await app.RunAsync().ConfigureAwait(false);
        }

        return 0;
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

        services.AddOptions<McpOptions>()
            .Bind(builder.Configuration.GetSection(McpOptions.SectionName))
            .Validate(
                o => o.Transport != McpTransport.Http || !string.IsNullOrWhiteSpace(o.HttpBearerToken),
                "Mcp__HttpBearerToken is required when the HTTP transport is enabled. Nothing here can trade, "
                + "but an open endpoint still exposes balances, positions and trade history.")
            .ValidateOnStart();

        services.AddSingleton(TimeProvider.System);

        // Parsed once, at startup, and shared. It is a pure value, and parsing refuses a malformed session
        // close rather than guessing -- this value decides what counts as missing data.
        services.AddSingleton(sp =>
        {
            MarketDataOptions options = sp.GetRequiredService<IOptions<MarketDataOptions>>().Value;
            return BarSessionCalendar.Parse(options.SessionCloseCentral, options.HolidayList());
        });

        services.AddSingleton<InstrumentRegistry>();
        services.AddSingleton<IndicatorCatalog>();
        services.AddSingleton<IndicatorCatalogNames>();
        services.AddSingleton<ToolGuards>();
        services.AddSingleton<StoreAvailabilityHolder>();
        services.AddSingleton<EmbeddingAvailabilityHolder>();
        services.AddSingleton<EmbeddingAvailabilityProbe>();

        // The embedding seam. Only the keyless default exists today (gh#45); a real provider is selected here
        // once one is chosen (gh#44). An unset key is a supported state, so this is never a startup failure.
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
        services.AddScoped<BarCacheService>();

        // The tool types themselves. The SDK activates a tool per call with ActivatorUtilities, which resolves
        // constructor parameters from DI but does NOT recursively activate unregistered types -- so a tool that
        // composes another tool (SnapshotTools takes MarketDataTools and ReferenceTools) fails at CALL time
        // with "unable to resolve service", while startup and tools/list both look perfectly healthy.
        //
        // Registering them explicitly is what makes that a startup-time guarantee rather than a per-tool
        // surprise the first time someone calls the one composed tool.
        services.AddScoped<ReferenceTools>();
        services.AddScoped<MarketDataTools>();
        services.AddScoped<AccountTools>();
        services.AddScoped<SnapshotTools>();
        services.AddScoped<ObservationTools>();

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

        if (!await database.Database.CanConnectAsync().ConfigureAwait(false))
        {
            // One line, not a stack trace. This is the first thing a new operator meets, and the stack trace
            // it used to print named a socket rather than the thing they need to do.
            StoreAvailability unavailable = StoreAvailability.Unavailable("Nothing answered on the configured connection string.");
            logger.LogWarning("{Explanation}", unavailable.Explanation);
            return unavailable;
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
        using IServiceScope scope = app.Services.CreateScope();
        IServiceProvider sp = scope.ServiceProvider;

        // The loop itself lives in IndicatorRebuilder rather than here, so the verb can be run by a test. A
        // private static in the composition root cannot be, and this verb shipped in Phase 2 having never
        // been executed anywhere.
        string? only = args.Length > 1 ? args[1] : null;

        await sp.GetRequiredService<IndicatorRebuilder>()
            .RebuildAsync(only, CancellationToken.None)
            .ConfigureAwait(false);

        return 0;
    }
}
