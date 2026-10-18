using FluentAssertions;
using MarqSpec.Client.ProjectX;

namespace MarqSpec.Mcp.TopstepX.Tests.Venue;

/// <summary>Pins the ProjectX client package the host retry wrapper and gateway adapter target (gh#734).</summary>
public sealed class ProjectXClientPackageVersionTests
{
    [Fact]
    public void MarqSpecClientProjectX_AssemblyVersion_IsPinnedAt3_0_0()
    {
        Version version = typeof(IProjectXApiClient).Assembly.GetName().Version!;

        version.Major.Should().Be(3);
        version.Minor.Should().Be(0);
        version.Build.Should().Be(0);
    }
}
