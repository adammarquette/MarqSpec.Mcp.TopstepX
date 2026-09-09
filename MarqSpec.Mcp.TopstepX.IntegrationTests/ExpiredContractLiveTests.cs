using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// Opt-in checks against the real ProjectX gateway (ADR-0020, gh#494, gh#507).
/// </summary>
/// <remarks>
/// <para>
/// The roll policy rests on a measured vendor fact — an expired contract still answers by id and serves
/// hourly bars — recorded once by the gh#494 probe. These tests turn that dated measurement into something
/// an operator can re-verify on demand.
/// </para>
/// <para>
/// CI excludes <c>Category=Live</c> (<c>--filter "Category!=Live"</c>). Credentials come from the
/// environment or user secrets only — never from a tracked file.
/// </para>
/// </remarks>
[Trait("Category", "Live")]
public sealed class ExpiredContractLiveTests
{
    /// <summary>
    /// The expiry the probe confirmed still answers: <c>MES.M26</c> on 2026-05-04 carried 23 hourly bars
    /// (ADR-0020, wiki — expired contracts and history depth).
    /// </summary>
    private const string ExpiredExpiryCode = "M26";

    /// <summary>
    /// A trade date inside that contract's liquid window, when it carried volume the probe measured.
    /// </summary>
    private static readonly DateOnly _liquidTradeDate = new(2026, 5, 4);

    private static IConfiguration LiveConfiguration() =>
        new ConfigurationBuilder()
            .AddEnvironmentVariables()
            .AddUserSecrets<ExpiredContractLiveTests>(optional: true)
            .Build();

    private static bool LiveCredentialsUnset(IConfiguration configuration) =>
        string.IsNullOrWhiteSpace(configuration["ProjectX:ApiKey"]);

    private static ServiceProvider BuildGatewayProvider(IConfiguration configuration)
    {
        Dictionary<string, string?> settings = new()
        {
            ["ConnectionStrings:Default"] = "Host=localhost;Database=x;Username=u;Password=p",
            ["MarketData:Instruments"] = "MES",
            ["MarketData:SessionCloseCentral"] = "16:00",
            ["MarketData:MaxRows"] = "5000",
        };

        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Configuration
            .AddInMemoryCollection(settings)
            .AddConfiguration(configuration);

        Program.ConfigureServices(builder, new McpOptions { Transport = McpTransport.Stdio });

        return builder.Services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    /// <summary>
    /// An expired contract still resolves by id and serves a day of hourly history (gh#507).
    /// </summary>
    [Fact]
    public async Task AnExpiredContract_StillAnswersOneDayOfHourlyBars()
    {
        IConfiguration configuration = LiveConfiguration();
        if (LiveCredentialsUnset(configuration))
        {
            return;
        }

        using ServiceProvider provider = BuildGatewayProvider(configuration);
        using IServiceScope scope = provider.CreateScope();
        IMarketDataGateway gateway = scope.ServiceProvider.GetRequiredService<IMarketDataGateway>();

        gateway.Should().BeOfType<ProjectXMarketDataGateway>();

        InstrumentId mes = new("MES");
        ContractExpiry.TryParse(ExpiredExpiryCode, out ContractExpiry expiry).Should().BeTrue();

        VenueContract? contract = await gateway.FindContractAsync(mes, expiry, CancellationToken.None);

        contract.Should().NotBeNull(
            "the probe showed MES.M26 still resolves by id after expiry (ADR-0020, gh#494)");

        BarRange window = OneTradingDay(_liquidTradeDate);
        IReadOnlyList<Bar> bars = await gateway.GetBarsAsync(
            contract!.ContractId,
            window,
            TimeSpan.FromHours(1),
            CancellationToken.None);

        bars.Should().NotBeEmpty(
            "the probe measured 23 hourly bars for MES.M26 on " + _liquidTradeDate.ToString("O"));
    }

    /// <summary>
    /// One futures trade date as a half-open UTC window — the session that contains the probe's sample day.
    /// </summary>
    private static BarRange OneTradingDay(DateOnly tradeDate) =>
        new(
            MarketClock.FromMarket(tradeDate.AddDays(-1), new TimeOnly(17, 0)).ToUniversalTime(),
            MarketClock.FromMarket(tradeDate, new TimeOnly(16, 0)).ToUniversalTime());
}
