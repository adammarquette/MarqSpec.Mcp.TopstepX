using System.Reflection;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// The release this process is, read from this assembly.
/// </summary>
/// <remarks>
/// <para>
/// <b>One reader for both answers.</b> <c>GET /health</c>'s <c>version</c> and the MCP
/// <c>initialize</c> result's <c>serverInfo.version</c> both come from here (gh#867). The SDK's own
/// default is the <em>entry</em> assembly's <c>AssemblyVersion</c>. Under <c>dotnet test</c> that is the
/// test host; in an image whose publish saw no git history it is <c>0.0.0.0</c>. Neither is the release.
/// </para>
/// <para>
/// <b>The informational version, without build metadata.</b> MinVer writes
/// <c>AssemblyInformationalVersion</c>, and the SDK appends <c>+&lt;sha&gt;</c> when it has a commit.
/// A deploy check compares <c>/health</c> to the tag, which has no <c>+</c> suffix, so the suffix is
/// removed here and therefore removed for <c>serverInfo</c> as well. The image build does not see
/// <c>.git</c> (<c>.dockerignore</c>); <c>MINVER_VERSION</c> is passed in as
/// <c>MinVerVersionOverride</c> so a release image carries the tag rather than <c>0.0.0-alpha.0</c>.
/// </para>
/// </remarks>
internal static class ProductVersion
{
    /// <summary>The informational version of this assembly, without <c>+</c> build metadata.</summary>
    /// <returns>The release string. <c>0.0.0</c> only when the assembly carries no version at all.</returns>
    public static string Read()
    {
        string? informational = typeof(ProductVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
        {
            return typeof(ProductVersion).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        }

        // MinVer and the SDK append "+<sha>". /health is compared to the tag, which has none.
        int metadata = informational.IndexOf('+', StringComparison.Ordinal);
        return metadata < 0 ? informational : informational[..metadata];
    }
}
