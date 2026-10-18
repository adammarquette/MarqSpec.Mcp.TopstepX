using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace MarqSpec.Mcp.TopstepX.Venue;

/// <summary>
/// Host-side retry policy for the ProjectX HTTP client (MarqSpec.Client.ProjectX 3.0.0, gh#734).
/// </summary>
/// <remarks>
/// The package's built-in retry predicate exempts only the place-order route from auto-retry. This host wraps
/// the registered resilience handler so every gated execution POST under the order and position API prefixes
/// (except allowlisted reads) never retries on transport faults, <c>429</c> or <c>5xx</c>.
/// </remarks>
internal static class ProjectXHttpResilienceConfiguration
{
    /// <summary>Chains the client retry predicate after the execution write guard.</summary>
    public static IServiceCollection SuppressTransportRetriesOnVenueWrites(this IServiceCollection services)
    {
        services.PostConfigureAll<HttpStandardResilienceOptions>(options =>
        {
            HttpRetryStrategyOptions retry = options.Retry;
            var previous = retry.ShouldHandle;

            retry.ShouldHandle = args =>
            {
                HttpRequestMessage? request = args.Context.GetRequestMessage();
                if (ProjectXVenueWriteRoutes.IsExecutionGatewayWriteRoute(request))
                {
                    return ValueTask.FromResult(false);
                }

                if (previous is not null)
                {
                    return previous(args);
                }

                return ValueTask.FromResult(
                    PinnedClientShouldRetryOutcome(
                        request,
                        args.Outcome.Exception,
                        args.Outcome.Result));
            };
        });

        return services;
    }

    /// <summary>
    /// Explicit copy of MarqSpec.Client.ProjectX 3.0.0 <c>ShouldRetryOutcome</c> (internal on the package).
    /// </summary>
    private static bool PinnedClientShouldRetryOutcome(
        HttpRequestMessage? request,
        Exception? exception,
        HttpResponseMessage? response)
    {
        if (ProjectXVenueWriteRoutes.IsPinnedClientOrderPlacementExempt(request))
        {
            return false;
        }

        if (exception is HttpRequestException
            || response?.StatusCode == HttpStatusCode.TooManyRequests)
        {
            return true;
        }

        if (response is null)
        {
            return false;
        }

        return response.StatusCode >= HttpStatusCode.InternalServerError;
    }
}
