using System.Net;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Venue;

namespace MarqSpec.Mcp.TopstepX.Tests.Venue;

/// <summary>Pins host transport retry policy for venue writes (gh#734).</summary>
[Trait("Gate", "ADR-0024")]
public sealed class ProjectXHttpResilienceTests
{
    [Theory]
    [InlineData("/api/Order/place")]
    [InlineData("/api/Order/modify")]
    [InlineData("/api/Position/closeContract")]
    [InlineData("/api/Position/partialCloseContract")]
    public void HostPolicy_DoesNotRetryNonIdempotentWrites_OnTransientFaults(string path)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "https://gateway.example" + path);
        using HttpResponseMessage response = new(HttpStatusCode.ServiceUnavailable);

        ProjectXHttpResilienceConfiguration.ShouldRetryHttpOutcome(request, exception: null, response)
            .Should().BeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public void HostPolicy_StillRetriesCancel_OnTransientFaults(HttpStatusCode status)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "https://gateway.example/api/Order/cancel");
        using HttpResponseMessage response = new(status);

        ProjectXHttpResilienceConfiguration.ShouldRetryHttpOutcome(request, exception: null, response)
            .Should().BeTrue("cancel is idempotent and keeps the client's standard retry policy");
    }
}
