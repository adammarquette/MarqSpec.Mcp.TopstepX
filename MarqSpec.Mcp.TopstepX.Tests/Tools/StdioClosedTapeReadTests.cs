using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using MarqSpec.Mcp.TopstepX.Tests.Configuration;
using MarqSpec.Mcp.TopstepX.Tests.MarketData;
using MarqSpec.Mcp.TopstepX.Tools;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.Mcp.TopstepX.Tests.Tools;

/// <summary>
/// stdio reads tape a separate recorder wrote. The store tier already serves a closed run;
/// the tool must not refuse it because this process is not the recorder (gh#728).
/// </summary>
/// <remarks>
/// Built through <see cref="Program.ConfigureServices"/> under stdio, with the EF in-memory
/// store standing in for the shared database. The unconfigured gateway refuses every venue
/// call, and <c>front</c> asks for one, so the test registers <see cref="CountingGateway"/> —
/// the same double the other tape-tool tests use. The defect under test is the tape gate,
/// which fires before that call.
/// </remarks>
public sealed class StdioClosedTapeReadTests : IDisposable
{
    private const string Venue = "test";
    private const string ContractId = "CON.F.US.EP.Z26";
    private const int FiveMinutes = 5;

    private static readonly DateTimeOffset _runStart = new(2026, 10, 16, 13, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _runEnd = new(2026, 10, 16, 14, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset _recorded = new(2026, 10, 16, 14, 30, 0, TimeSpan.Zero);

    private readonly WebApplication _app;

    public StdioClosedTapeReadTests()
    {
        string databaseName = Guid.NewGuid().ToString();
        WebApplicationBuilder builder = CompositionRootTestHost.CreateIsolatedBuilder();
        builder.Configuration.AddInMemoryCollection(CompositionRootTestHost.BaseSettings);
        Program.ConfigureServices(builder, new McpOptions { Transport = McpTransport.Stdio });
        UseInMemoryStore(builder.Services, databaseName);
        ReplaceGateway(builder.Services);

        _app = builder.Build();
        _app.Services.GetRequiredService<TapeAvailabilityHolder>()
            .Set(TapeAvailability.NeverStartedBecauseStdio());

        using IServiceScope scope = _app.Services.CreateScope();
        TopstepXDbContext database = scope.ServiceProvider.GetRequiredService<TopstepXDbContext>();
        database.TapeCoverage.Add(new TapeCoverageRecord
        {
            Venue = Venue,
            Instrument = "ES",
            ContractId = ContractId,
            RangeStart = _runStart,
            RangeEnd = _runEnd,
            RecordedAt = _recorded,
        });
        database.FootprintCells.AddRange(Cells());
        database.Trades.Add(new TradeRecord
        {
            Venue = Venue,
            Instrument = "ES",
            ContractId = ContractId,
            TradeTimeUtc = _runStart.AddMinutes(1),
            Sequence = 1,
            Price = 6800.00m,
            Size = 50,
            Direction = MarqSpec.Mcp.TopstepX.Domain.MarketData.TradeDirection.Buy,
            RecordedAt = _recorded,
        });
        database.SaveChanges();
    }

    public void Dispose() => _app.DisposeAsync().AsTask().GetAwaiter().GetResult();

    [Fact]
    public async Task GetFootprint_WhenStdioIsNotTheRecorder_ServesTapeRecordedByAnotherProcess()
    {
        await using AsyncServiceScope scope = _app.Services.CreateAsyncScope();
        TapeTools tools = ActivatorUtilities.CreateInstance<TapeTools>(scope.ServiceProvider);

        ToolPayloads.FootprintSeries payload = await tools.GetFootprint(
            "ES", FiveMinutes, _runStart, _runEnd, CancellationToken.None);

        payload.Cells.Should().ContainSingle().Which.Buy.Should().Be(50);
        payload.Cells.Single().P.Should().Be(6800.00m);
        payload.Covered.Start.Should().Be(_runStart);
        payload.Covered.End.Should().Be(_runEnd);
        payload.Covered.Narrowed.Should().BeFalse();
        payload.Live.Should().BeFalse();
        payload.Reason.Should().Be(TapeClosedReadReason.NeverStarted);
        scope.ServiceProvider.GetRequiredService<TopstepXDbContext>().FootprintCells.Count()
            .Should().Be(24, "the stale projection is not rewritten");
    }

    [Fact]
    public async Task GetVolumeProfile_WhenStdioIsNotTheRecorder_ServesClosedCoverage()
    {
        await using AsyncServiceScope scope = _app.Services.CreateAsyncScope();
        TapeTools tools = ActivatorUtilities.CreateInstance<TapeTools>(scope.ServiceProvider);

        ToolPayloads.VolumeProfileSeries payload = await tools.GetVolumeProfile(
            "ES", FiveMinutes, _runStart, _runEnd, CancellationToken.None);

        payload.PointOfControl.Should().Be(6800.00m);
        payload.TotalVolume.Should().Be(50);
        payload.Covered.Start.Should().Be(_runStart);
        payload.Covered.End.Should().Be(_runEnd);
        payload.Live.Should().BeFalse();
        payload.Reason.Should().Be(TapeClosedReadReason.NeverStarted);
    }

    private static void UseInMemoryStore(IServiceCollection services, string name)
    {
        ServiceDescriptor[] stale = services
            .Where(descriptor =>
                descriptor.ServiceType == typeof(TopstepXDbContext)
                || descriptor.ServiceType == typeof(DbContextOptions)
                || descriptor.ServiceType == typeof(DbContextOptions<TopstepXDbContext>)
                || (descriptor.ServiceType.IsGenericType
                    && descriptor.ServiceType.GenericTypeArguments.Contains(typeof(TopstepXDbContext))))
            .ToArray();

        foreach (ServiceDescriptor descriptor in stale)
        {
            services.Remove(descriptor);
        }

        services.AddDbContext<TopstepXDbContext>(options => options.UseInMemoryDatabase(name));
    }

    private static void ReplaceGateway(IServiceCollection services)
    {
        ServiceDescriptor registered = services.Single(descriptor =>
            descriptor.ServiceType == typeof(IMarketDataGateway));
        services.Remove(registered);
        services.AddScoped<IMarketDataGateway>(_ => new CountingGateway([]));
    }

    private static FootprintCellRecord[] Cells()
    {
        FootprintCellRecord[] cells = new FootprintCellRecord[24];
        for (int bucket = 0; bucket < 12; bucket++)
        {
            DateTimeOffset open = _runStart.AddMinutes(FiveMinutes * bucket);
            cells[bucket * 2] = Cell(open, 6800.00m, buy: 10, sell: 0);
            cells[(bucket * 2) + 1] = Cell(open, 6800.25m, buy: 1, sell: 0);
        }

        return cells;
    }

    private static FootprintCellRecord Cell(DateTimeOffset bucket, decimal price, long buy, long sell) => new()
    {
        Venue = Venue,
        Instrument = "ES",
        ResolutionMinutes = FiveMinutes,
        BucketStart = bucket,
        Price = price,
        BuyVolume = buy,
        SellVolume = sell,
        RecordedAt = _recorded,
    };
}
