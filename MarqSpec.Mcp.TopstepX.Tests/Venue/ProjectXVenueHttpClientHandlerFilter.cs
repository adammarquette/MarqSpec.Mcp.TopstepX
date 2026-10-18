using Microsoft.Extensions.Http;

namespace MarqSpec.Mcp.TopstepX.Tests.Venue;

/// <summary>
/// Replaces HTTP primary handlers with the test double for this isolated provider (gh#734).
/// </summary>
/// <remarks>
/// The Refit ProjectX client name does not always contain <c>ProjectX</c>, so this filter applies to every
/// handler built in the composition test host (no embedding HTTP traffic in these tests).
/// </remarks>
internal sealed class ProjectXVenueHttpClientHandlerFilter(ProjectXVenueHttpPrimaryHandler handler) : IHttpMessageHandlerBuilderFilter
{
    public Action<HttpMessageHandlerBuilder> Configure(Action<HttpMessageHandlerBuilder> next) => builder =>
    {
        next(builder);
        builder.PrimaryHandler = handler;
    };
}
