using System.ComponentModel.DataAnnotations;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// One-shot historical bar backfill (gh#899). Off unless the switch is set.
/// </summary>
/// <remarks>
/// There is no Railway variable for this. Turning it on is an operator setting in the environment
/// or an appsettings overlay, documented in the deployment notes, and the process runs the pass once.
/// </remarks>
public sealed class BarBackfillOptions : IValidatableObject
{
    /// <summary>The configuration section (<c>Backfill__*</c>).</summary>
    public const string SectionName = "Backfill";

    /// <summary>When false, the hosted pass returns without reading the store or the venue.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Comma-separated symbols. Empty uses <c>MarketData__Instruments</c>.
    /// </summary>
    public string Instruments { get; init; } = "";

    /// <summary>Comma-separated bar sizes in minutes. The default is one minute and one hour.</summary>
    public string ResolutionsMinutes { get; init; } = "1,60";

    /// <summary>
    /// Do not request bars older than this instant. A value older than the measured edge is clamped
    /// forward to that edge. Absent means the edge itself.
    /// </summary>
    public DateTimeOffset? NotBeforeUtc { get; init; }

    /// <summary>The parsed resolutions. Empty when <see cref="ResolutionsMinutes"/> does not parse.</summary>
    public IReadOnlyList<int> Resolutions => ParseResolutions(ResolutionsMinutes, out _, out _);

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        ParseResolutions(ResolutionsMinutes, out bool ok, out string? error);
        if (!ok)
        {
            yield return new ValidationResult(error, [nameof(ResolutionsMinutes)]);
        }
    }

    private static List<int> ParseResolutions(string text, out bool ok, out string? error)
    {
        List<int> resolutions = [];
        ok = true;
        error = null;
        string[] parts = text.Split(
            ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string part in parts)
        {
            if (!int.TryParse(
                    part,
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out int minutes)
                || minutes <= 0)
            {
                ok = false;
                error = "Backfill__ResolutionsMinutes must be a comma-separated list "
                    + "of positive minute counts.";
                resolutions.Clear();
                return resolutions;
            }

            resolutions.Add(minutes);
        }

        if (resolutions.Count == 0)
        {
            ok = false;
            error = "Backfill__ResolutionsMinutes must name at least one resolution.";
        }

        return resolutions;
    }
}
