using Microsoft.Extensions.Configuration;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// Binds <see cref="TradingExecutionOptions"/> while treating an empty <c>Enabled</c> value as absent (ADR-0024).
/// </summary>
internal static class TradingExecutionOptionsConfiguration
{
    /// <summary>Binds trading options from configuration.</summary>
    /// <param name="options">The instance to populate.</param>
    /// <param name="configuration">The application configuration root.</param>
    internal static void Bind(TradingExecutionOptions options, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(configuration);

        IConfigurationSection section = configuration.GetSection(TradingExecutionOptions.SectionName);
        List<KeyValuePair<string, string?>> pairs = [];

        foreach (IConfigurationSection child in section.GetChildren())
        {
            if (child.Key.Equals("Enabled", StringComparison.OrdinalIgnoreCase)
                && string.IsNullOrEmpty(child.Value))
            {
                continue;
            }

            pairs.Add(
                new KeyValuePair<string, string?>(
                    TradingExecutionOptions.SectionName + ConfigurationPath.KeyDelimiter + child.Key,
                    child.Value));
        }

        if (pairs.Count == 0)
        {
            return;
        }

        IConfigurationRoot scratch = new ConfigurationBuilder().AddInMemoryCollection(pairs).Build();
        scratch.GetSection(TradingExecutionOptions.SectionName).Bind(options);
    }
}
