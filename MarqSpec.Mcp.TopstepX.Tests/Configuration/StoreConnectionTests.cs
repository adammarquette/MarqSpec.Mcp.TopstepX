using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using Microsoft.Extensions.Configuration;

namespace MarqSpec.Mcp.TopstepX.Tests.Configuration;

/// <summary>
/// The store connection string has one source, including the local fallback (gh#843).
/// </summary>
public sealed class StoreConnectionTests
{
    [Fact]
    public void Resolve_WhenUnset_UsesTheLocalFallback()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();

        StoreConnection.Resolve(configuration).Should().Be(StoreConnection.LocalFallback);
        StoreConnection.LocalFallback.Should().Contain("changeme-local");
    }

    [Fact]
    public void Resolve_WhenConfigured_UsesThatString()
    {
        const string Configured =
            "Host=db;Port=5432;Database=topstepx_mcp;Username=topstepx;Password=secret";
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = Configured,
            })
            .Build();

        StoreConnection.Resolve(configuration).Should().Be(Configured);
    }

    /// <summary>
    /// EF and the migration lock both call <see cref="StoreConnection.Resolve"/>. A second copy of
    /// the lookup, or of the fallback literal, is the drift this guards.
    /// </summary>
    [Fact]
    public void Program_ResolvesTheStoreConnectionOncePerCallSite()
    {
        string program = File.ReadAllText(Path.Combine(FindRepoRoot(), "MarqSpec.Mcp.TopstepX", "Program.cs"));

        Count(program, "StoreConnection.Resolve(").Should().Be(2);
        program.Should().NotContain("GetConnectionString(\"Default\")");
        program.Should().NotContain("changeme-local");
    }

    private static int Count(string text, string value)
    {
        int count = 0;
        int from = 0;
        while ((from = text.IndexOf(value, from, StringComparison.Ordinal)) >= 0)
        {
            count++;
            from += value.Length;
        }

        return count;
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MarqSpec.Mcp.TopstepX.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not find MarqSpec.Mcp.TopstepX.slnx above " + AppContext.BaseDirectory);
    }
}
