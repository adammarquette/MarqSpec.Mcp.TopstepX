using System.Reflection;
using MarqSpec.Client.ProjectX.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace MarqSpec.Mcp.TopstepX.Venue;

/// <summary>
/// Host-side retry policy for the ProjectX HTTP client (MarqSpec.Client.ProjectX 3.0.0, gh#734).
/// </summary>
/// <remarks>
/// The package's <c>ShouldRetryOutcome</c> exempts only <see cref="ProjectXVenueWriteRoutes.Place"/>.
/// This host wraps the registered resilience handler so modify, close and partial-close never retry on
/// transport faults, <c>429</c> or <c>5xx</c>.
/// </remarks>
internal static class ProjectXHttpResilienceConfiguration
{
    private static readonly MethodInfo _clientShouldRetryOutcome = typeof(ServiceCollectionExtensions)
        .GetMethod(
            "ShouldRetryOutcome",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            [typeof(HttpRequestMessage), typeof(Exception), typeof(HttpResponseMessage)],
            modifiers: null)!;

    /// <summary>Chains the client retry predicate after the non-idempotent write guard.</summary>
    public static IServiceCollection SuppressTransportRetriesOnVenueWrites(this IServiceCollection services)
    {
        services.PostConfigureAll<HttpStandardResilienceOptions>(options =>
        {
            HttpRetryStrategyOptions retry = options.Retry;
            var previous = retry.ShouldHandle;

            retry.ShouldHandle = args =>
            {
                HttpRequestMessage? request = args.Context.GetRequestMessage();
                if (ProjectXVenueWriteRoutes.IsNonIdempotentWrite(request))
                {
                    return ValueTask.FromResult(false);
                }

                if (previous is not null)
                {
                    return previous(args);
                }

                return ValueTask.FromResult(
                    ClientShouldRetry(request, args.Outcome.Exception, args.Outcome.Result));
            };
        });

        return services;
    }

    /// <summary>Evaluates the effective retry decision used by the host (unit-tested).</summary>
    public static bool ShouldRetryHttpOutcome(
        HttpRequestMessage? request,
        Exception? exception,
        HttpResponseMessage? response) =>
        !ProjectXVenueWriteRoutes.IsNonIdempotentWrite(request)
        && ClientShouldRetry(request, exception, response);

    private static bool ClientShouldRetry(
        HttpRequestMessage? request,
        Exception? exception,
        HttpResponseMessage? response) =>
        (bool)_clientShouldRetryOutcome.Invoke(null, [request, exception, response])!;
}
