using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Venue;

namespace MarqSpec.Mcp.TopstepX.Tests.Venue;

[Trait("Gate", "ADR-0024")]
public sealed class ProjectXVenueWriteRoutesTests
{
    [Fact]
    public void NewOrderPostRoutes_DefaultToNoRetry_UnlessAllowlistedAsRead()
    {
        using HttpRequestMessage write = new(HttpMethod.Post, "https://gateway.test/api/Order/futureWrite");
        ProjectXVenueWriteRoutes.IsExecutionGatewayWriteRoute(write).Should().BeTrue();
    }

    [Fact]
    public void OrderSearchPost_IsAllowlistedAsRead()
    {
        using HttpRequestMessage read = new(HttpMethod.Post, "https://gateway.test/api/Order/search");
        ProjectXVenueWriteRoutes.IsExecutionGatewayWriteRoute(read).Should().BeFalse();
    }

    [Fact]
    public void WriteRoutes_WithPrefixedBasePath_StillMatchOrderAndPositionSegments()
    {
        using HttpRequestMessage modify = new(HttpMethod.Post, "https://gateway.test/proxy/api/Order/modify");
        using HttpRequestMessage close = new(HttpMethod.Post, "https://gateway.test/proxy/api/Position/closeContract");

        ProjectXVenueWriteRoutes.IsExecutionGatewayWriteRoute(modify).Should().BeTrue();
        ProjectXVenueWriteRoutes.IsExecutionGatewayWriteRoute(close).Should().BeTrue();
    }
}
