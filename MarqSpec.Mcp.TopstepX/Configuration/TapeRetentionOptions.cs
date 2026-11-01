using System.ComponentModel.DataAnnotations;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// How long an exported trade day may stay in <c>Trades</c> (gh#763, ADR-0027).
/// </summary>
/// <remarks>
/// <para>
/// The default is 90 days. The recorder deletes a UTC day only after an <c>uploaded</c> trades
/// receipt for that day and contract. No receipt means no delete. Zero and a negative number are
/// refused. They do not turn pruning off.
/// </para>
/// <para>
/// A window shorter than <see cref="MinimumDays"/> is refused so a one-day typo cannot empty an
/// exported tape. <see cref="MaximumDays"/> is a typo guard, not a promise that ten years of
/// prints fit on the volume.
/// </para>
/// </remarks>
public sealed class TapeRetentionOptions : IValidatableObject
{
    /// <summary>The configuration section these bind to (<c>TapeRetention__*</c>).</summary>
    public const string SectionName = "TapeRetention";

    /// <summary>Days kept when the key is absent. Binds from <c>TapeRetention__Days</c>.</summary>
    public const int DefaultDays = 90;

    /// <summary>The shortest window startup accepts.</summary>
    public const int MinimumDays = 7;

    /// <summary>The longest window startup accepts. Ten years.</summary>
    public const int MaximumDays = 3_650;

    /// <summary>
    /// How many days of <c>Trades</c> to keep. Rows on a UTC day that ended before this age are
    /// eligible. A receipt is still required.
    /// </summary>
    public int Days { get; init; } = DefaultDays;

    /// <summary>
    /// Refuses a window outside <see cref="MinimumDays"/> through <see cref="MaximumDays"/>.
    /// </summary>
    /// <param name="validationContext">The validation context.</param>
    /// <returns>One result when <see cref="Days"/> is outside the window, otherwise none.</returns>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Days < MinimumDays || Days > MaximumDays)
        {
            yield return new ValidationResult(
                SectionName + "__Days is " + Days + ". Accepted values are " + MinimumDays
                + " through " + MaximumDays
                + ". Zero and a negative number do not turn pruning off.",
                [nameof(Days)]);
        }
    }
}
