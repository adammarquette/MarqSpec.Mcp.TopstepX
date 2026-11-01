using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// Checks backfill instruments against the registry, and refuses a <c>NotBeforeUtc</c> in the future
/// (gh#899).
/// </summary>
public sealed class BarBackfillOptionsValidator(InstrumentRegistry registry, TimeProvider clock)
    : IValidateOptions<BarBackfillOptions>
{
    private readonly InstrumentRegistry _registry = registry;
    private readonly TimeProvider _clock = clock;

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, BarBackfillOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> failures = [];
        if (!string.IsNullOrWhiteSpace(options.Instruments))
        {
            string[] symbols = options.Instruments.Split(
                ',',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (string symbol in symbols)
            {
                try
                {
                    _registry.Resolve(symbol);
                }
                catch (KeyNotFoundException)
                {
                    failures.Add(
                        "Backfill instruments include '" + symbol
                        + "', which this server does not serve.");
                }
            }
        }

        if (options.NotBeforeUtc is DateTimeOffset floor && floor > _clock.GetUtcNow())
        {
            failures.Add(
                "Backfill NotBeforeUtc is in the future, so the pass would ask for nothing. "
                + "Leave it unset, or set it at or before the current instant.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
