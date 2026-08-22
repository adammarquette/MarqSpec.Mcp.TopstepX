using MarqSpec.Client.ProjectX.DependencyInjection;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
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

        if (mcp.Transport == McpTransport.Http)
        {
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

        if (venue.IsConfigured && venue.DataTier != ProjectXDataTier.Unspecified)
        {
            services.AddProjectXApiClient(builder.Configuration);
            services.AddSingleton<IMarketDataGateway, ProjectXMarketDataGateway>();
        }
        else
        {
            services.AddSingleton<IMarketDataGateway, UnconfiguredMarketDataGateway>();
        }

        string connection = builder.Configuration.GetConnectionString("Default")
            ?? "Host=localhost;Port=5432;Database=topstepx_mcp;Username=topstepx;Password=changeme-local";

        services.AddDbContext<TopstepXDbContext>(options =>
            options.UseNpgsql(connection, npgsql => npgsql.UseVector()));

        services.AddScoped<IndicatorProjector>();
        services.AddScoped<BarCacheService>();

        // One registration, one tool set, two ways in (ADR-0007). The transport is the only thing that
        // differs, and it is chosen here rather than by a second AddMcpServer call — registering the server
        // twice would build two of everything and leave which one answers to configuration order.
        IMcpServerBuilder server = services.AddMcpServer()
            .WithToolsFromAssembly(typeof(Program).Assembly);

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

    private static async Task<int> RebuildIndicatorsAsync(WebApplication app, string[] args)
    {
        using IServiceScope scope = app.Services.CreateScope();
        IServiceProvider sp = scope.ServiceProvider;

        TopstepXDbContext database = sp.GetRequiredService<TopstepXDbContext>();
        IndicatorProjector projector = sp.GetRequiredService<IndicatorProjector>();
        IMarketDataGateway gateway = sp.GetRequiredService<IMarketDataGateway>();
        InstrumentRegistry registry = sp.GetRequiredService<InstrumentRegistry>();
        TimeProvider clock = sp.GetRequiredService<TimeProvider>();
        ILogger logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger("rebuild-indicators");

        string? only = args.Length > 1 ? args[1].Trim().ToUpperInvariant() : null;
        DateTimeOffset now = clock.GetUtcNow();

        // Every (instrument, resolution) the store actually holds, rather than every configured one: a
        // resolution nobody has fetched has nothing to rebuild, and asking for it would be a no-op that looks
        // like a result.
        var series = await database.Bars
            .Select(b => new { b.Venue, b.Instrument, b.ResolutionMinutes })
            .Distinct()
            .ToListAsync()
            .ConfigureAwait(false);

        int total = 0;
        foreach (var s in series)
        {
            if (only is not null && !string.Equals(s.Instrument, only, StringComparison.Ordinal))
            {
                continue;
            }

            if (!registry.IsServed(s.Instrument))
            {
                logger.LogWarning(
                    "Skipping {Instrument}: it is in the store but not in MarketData__Instruments.",
                    s.Instrument);
                continue;
            }

            int written = await projector
                .ProjectAsync(s.Venue, registry.Resolve(s.Instrument), s.ResolutionMinutes, now, default)
                .ConfigureAwait(false);

            await database.SaveChangesAsync().ConfigureAwait(false);
            total += written;

            logger.LogInformation(
                "Rebuilt {Count} values for {Instrument} {Resolution}m.",
                written,
                s.Instrument,
                s.ResolutionMinutes);
        }

        logger.LogInformation("Rebuild complete: {Total} values written across {Series} series.",
            total, series.Count);

        _ = gateway; // The rebuild is a replay over stored bars; the venue is deliberately never reached.
        return 0;
    }
}
