using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace MarqSpec.Mcp.TopstepX.Venue;

/// <summary>
/// Host-side retry policy for the ProjectX HTTP client (MarqSpec.Client.ProjectX 3.0.0, gh#734).
/// </summary>
/// <remarks>
/// <para>
/// The package always registers its own <c>ShouldHandle</c> predicate (including the
/// <c>/Order/place</c> non-retry exemption). This host wraps that handler: gated execution POSTs under the
/// order and position API prefixes never retry, and every other request delegates to the client's predicate.
/// </para>
/// </remarks>
internal static class ProjectXHttpResilienceConfiguration
{
    /// <summary>Chains the execution write guard ahead of the client's retry predicate.</summary>
    public static IServiceCollection SuppressTransportRetriesOnVenueWrites(this IServiceCollection services)
    {
        services.PostConfigureAll<HttpStandardResilienceOptions>(options =>
        {
            HttpRetryStrategyOptions retry = options.Retry;
            var previous = retry.ShouldHandle!;

            retry.ShouldHandle = args =>
            {
                HttpRequestMessage? request = args.Context.GetRequestMessage();
                if (ProjectXVenueWriteRoutes.IsExecutionGatewayWriteRoute(request))
                {
                    return ValueTask.FromResult(false);
                }

                return previous(args);
            };
        });

        return services;
    }
}
