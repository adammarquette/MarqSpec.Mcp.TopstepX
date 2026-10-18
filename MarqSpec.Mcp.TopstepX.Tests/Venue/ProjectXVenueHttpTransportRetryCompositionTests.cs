using FluentAssertions;
using MarqSpec.Client.ProjectX;
using MarqSpec.Mcp.TopstepX.Tests.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace MarqSpec.Mcp.TopstepX.Tests.Venue;

/// <summary>
/// Pins transport retry behaviour through the real <see cref="Program.ConfigureServices"/> pipeline (gh#734).
/// </summary>
[Trait("Gate", "ADR-0024")]
public sealed class ProjectXVenueHttpTransportRetryCompositionTests
{
    [Fact]
    public async Task ConfiguredCompositionRoot_ReachesTheInjectedPrimaryHandler()
    {
        ProjectXVenueHttpPrimaryHandler handler = new();
        using ServiceProvider provider = BuildWithHandler(handler);

        using IServiceScope scope = provider.CreateScope();
        IProjectXApiClient client = scope.ServiceProvider.GetRequiredService<IProjectXApiClient>();

        try
        {
            await client.PingAsync(CancellationToken.None);
        }
        catch (Exception)
        {
            // Ping may still throw after transport attempts; the assertion is on the handler.
        }

        handler.AttemptsForPath("/api/Status/ping").Should().BeGreaterThan(0);
    }

    private static readonly Dictionary<string, string?> _venueSettings = new()
    {
        ["ProjectX:ApiKey"] = "a-username",
        ["ProjectX:ApiSecret"] = "an-api-key",
        ["ProjectX:DataTier"] = "Simulated",
        ["ProjectX:BaseUrl"] = "https://gateway.test/",
    };

    [Theory]
    [InlineData("/api/Order/place")]
    [InlineData("/api/Order/modify")]
    [InlineData("/api/Order/cancel")]
    [InlineData("/api/Position/closeContract")]
    [InlineData("/api/Position/partialCloseContract")]
    public async Task ConfiguredCompositionRoot_SendsEachWriteRouteOnce_OnServiceUnavailable(string pathSuffix)
    {
        ProjectXVenueHttpPrimaryHandler handler = new();
        using ServiceProvider provider = BuildWithHandler(handler);

        using IServiceScope scope = provider.CreateScope();
        IProjectXApiClient client = scope.ServiceProvider.GetRequiredService<IProjectXApiClient>();

        Func<Task> invoke = pathSuffix switch
        {
            "/api/Order/place" => () => client.PlaceOrderAsync(
                new MarqSpec.Client.ProjectX.Api.Models.PlaceOrderRequest
                {
                    AccountId = 1,
                    ContractId = "CON.F.US.EP.Z26",
                    Type = MarqSpec.Client.ProjectX.Api.Models.OrderType.Market,
                    Side = MarqSpec.Client.ProjectX.Api.Models.OrderSide.Bid,
                    Size = 1,
                },
                CancellationToken.None),
            "/api/Order/modify" => () => client.ModifyOrderAsync(
                new MarqSpec.Client.ProjectX.Api.Models.ModifyOrderRequest
                {
                    AccountId = 1,
                    OrderId = 1,
                    Size = 1,
                },
                CancellationToken.None),
            "/api/Order/cancel" => () => client.CancelOrderAsync(1, 1, CancellationToken.None),
            "/api/Position/closeContract" => () => client.ClosePositionAsync(
                1,
                "CON.F.US.EP.Z26",
                CancellationToken.None),
            "/api/Position/partialCloseContract" => () => client.PartialClosePositionAsync(
                1,
                "CON.F.US.EP.Z26",
                1,
                CancellationToken.None),
            _ => throw new ArgumentOutOfRangeException(nameof(pathSuffix), pathSuffix, null),
        };

        await invoke.Should().ThrowAsync<Exception>();

        handler.AttemptsForPath(pathSuffix).Should().Be(1);
    }

    [Fact]
    public async Task ConfiguredCompositionRoot_RetriesReadRoutes_OnServiceUnavailable()
    {
        ProjectXVenueHttpPrimaryHandler handler = new();
        using ServiceProvider provider = BuildWithHandler(handler);

        using IServiceScope scope = provider.CreateScope();
        IProjectXApiClient client = scope.ServiceProvider.GetRequiredService<IProjectXApiClient>();

        Func<Task> read = () => client.GetAccountsAsync(true, CancellationToken.None);

        await read.Should().ThrowAsync<Exception>();

        handler.AttemptsForPath("/api/Account/search").Should().BeGreaterThan(1);
    }

    private static ServiceProvider BuildWithHandler(ProjectXVenueHttpPrimaryHandler handler) =>
        CompositionRootTestHost.BuildVenueConfigured(
            _venueSettings,
            services =>
            {
                services.AddSingleton(handler);
                services.AddSingleton<IHttpMessageHandlerBuilderFilter, ProjectXVenueHttpClientHandlerFilter>();
            });
}
