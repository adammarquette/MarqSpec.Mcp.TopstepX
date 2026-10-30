using System.Net;
using System.Reflection;
using FluentAssertions;
using MarqSpec.Client.ProjectX.DependencyInjection;

namespace MarqSpec.Mcp.TopstepX.Tests.Venue;

/// <summary>Probes the pinned ProjectX client retry predicate (gh#710, gh#734).</summary>
public sealed class ProjectXClientRetryProbeTests
{
    private static readonly MethodInfo _shouldRetryOutcome = typeof(ServiceCollectionExtensions)
        .GetMethod("ShouldRetryOutcome", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!;

    [Theory]
    [InlineData("/api/Order/modify")]
    [InlineData("/api/Position/closeContract")]
    [InlineData("/api/Position/partialCloseContract")]
    public void PinnedClient_StillRetriesNonIdempotentWrites_OnTransientFaults(string path)
    {
        _shouldRetryOutcome.Should().NotBeNull();

        using HttpRequestMessage request = new(HttpMethod.Post, "https://gateway.example" + path);
        using HttpResponseMessage response = new(HttpStatusCode.ServiceUnavailable);

        bool retry = (bool)_shouldRetryOutcome.Invoke(null, [request, null, response])!;
        retry.Should().BeTrue("the pinned 3.0.0 client only exempts /api/Order/place from retries");
    }
}
