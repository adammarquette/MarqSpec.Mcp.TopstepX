using FluentAssertions;
using MarqSpec.Client.ProjectX;
using MarqSpec.Client.ProjectX.DependencyInjection;
using MarqSpec.Mcp.TopstepX.Tests.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace MarqSpec.Mcp.TopstepX.Tests.Venue;

/// <summary>Proves the host write guard runs ahead of the client's retry predicate (gh#734, gh#740).</summary>
[Trait("Gate", "ADR-0024")]
public sealed class ProjectXHttpResilienceHandlerChainTests
{
    private static readonly Dictionary<string, string?> _venueSettings = new()
    {
        ["ProjectX:ApiKey"] = "a-username",
        ["ProjectX:ApiSecret"] = "an-api-key",
        ["ProjectX:DataTier"] = "Simulated",
        ["ProjectX:BaseUrl"] = "https://gateway.test/",
    };

    [Fact]
    public async Task WithoutHostWriteGuard_ModifyRouteRetriesOnServiceUnavailable()
    {
        ProjectXVenueHttpPrimaryHandler handler = new();
        using ServiceProvider provider = BuildClientOnly(handler);

        using IServiceScope scope = provider.CreateScope();
        IProjectXApiClient client = scope.ServiceProvider.GetRequiredService<IProjectXApiClient>();

        await client
            .Invoking(c => c.ModifyOrderAsync(
                new MarqSpec.Client.ProjectX.Api.Models.ModifyOrderRequest
                {
                    AccountId = 1,
                    OrderId = 1,
                    Size = 1,
                },
                CancellationToken.None))
            .Should()
            .ThrowAsync<Exception>();

        handler.AttemptsForPath("/api/Order/modify").Should().BeGreaterThan(
            1,
            "the pinned 3.0.0 client retries non-place writes on 503 unless the host guard suppresses them");
    }

    [Fact]
    public async Task WithHostWriteGuard_ModifyRouteDoesNotRetryOnServiceUnavailable()
    {
        ProjectXVenueHttpPrimaryHandler handler = new();
        using ServiceProvider provider = CompositionRootTestHost.BuildVenueConfigured(
            _venueSettings,
            services =>
            {
                services.AddSingleton(handler);
                services.AddSingleton<IHttpMessageHandlerBuilderFilter, ProjectXVenueHttpClientHandlerFilter>();
            });

        using IServiceScope scope = provider.CreateScope();
        IProjectXApiClient client = scope.ServiceProvider.GetRequiredService<IProjectXApiClient>();

        await client
            .Invoking(c => c.ModifyOrderAsync(
                new MarqSpec.Client.ProjectX.Api.Models.ModifyOrderRequest
                {
                    AccountId = 1,
                    OrderId = 1,
                    Size = 1,
                },
                CancellationToken.None))
            .Should()
            .ThrowAsync<Exception>();

        handler.AttemptsForPath("/api/Order/modify").Should().Be(1);
    }

    private static ServiceProvider BuildClientOnly(ProjectXVenueHttpPrimaryHandler handler)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(_venueSettings)
            .Build();

        ServiceCollection services = new();
        services.AddProjectXApiClient(configuration);
        services.AddSingleton(handler);
        services.AddSingleton<IHttpMessageHandlerBuilderFilter, ProjectXVenueHttpClientHandlerFilter>();

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }
}
