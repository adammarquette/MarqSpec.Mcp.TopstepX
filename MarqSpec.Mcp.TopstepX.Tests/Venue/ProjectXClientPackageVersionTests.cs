using System.Reflection;
using System.Xml.Linq;
using FluentAssertions;
using MarqSpec.Client.ProjectX;

namespace MarqSpec.Mcp.TopstepX.Tests.Venue;

/// <summary>Pins the ProjectX client package the host retry wrapper and gateway adapter target (gh#734).</summary>
public sealed class ProjectXClientPackageVersionTests
{
    [Fact]
    public void MarqSpecClientProjectX_InformationalVersion_IsPinnedAt3_0_0()
    {
        Assembly assembly = typeof(IProjectXApiClient).Assembly;
        string informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        AssertInformationalVersionPinned(informational);
    }

    [Fact]
    public void DirectoryPackagesProps_PinsMarqSpecClientProjectXAt3_0_0()
    {
        string propsPath = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Directory.Packages.props"));
        XDocument props = XDocument.Load(propsPath);

        string? version = props.Root?
            .Elements("ItemGroup")
            .SelectMany(group => group.Elements("PackageVersion"))
            .FirstOrDefault(element => element.Attribute("Include")?.Value == "MarqSpec.Client.ProjectX")
            ?.Attribute("Version")
            ?.Value;

        version.Should().Be("3.0.0");
    }

    [Fact]
    public void InformationalVersionPin_WouldFailFor3_1_0()
    {
        const string bumped = "3.1.0+abc123";
        Action act = () => AssertInformationalVersionPinned(bumped);

        act.Should().Throw<Exception>(
            because: "3.1.0 must miss the same comparison the 3.0.0 informational-version pin uses");
    }

    /// <summary>The comparison both pin facts apply. A looser prefix accepts 3.1.0 and the sibling fact fails.</summary>
    private static void AssertInformationalVersionPinned(string informational) =>
        informational.Should().StartWith("3.0.0");
}
