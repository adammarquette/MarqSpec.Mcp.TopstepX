using System.ComponentModel.DataAnnotations;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// How long recorded prints stay in <c>Trades</c> (gh#763).
/// </summary>
/// <remarks>
/// <para>
/// The default is 90 days, which is the window the operator chose. The recorder deletes rows
/// older than that. A row at the cutoff stays. Timescale <c>policy_retention</c> stays empty;
/// this process does the delete, and only the process that records.
/// </para>
/// <para>
/// Zero and a negative number are refused. They do not turn pruning off. A window shorter
/// than <see cref="MinimumDays"/> is refused for the same reason a one-day typo must not
/// wipe the tape. <see cref="MaximumDays"/> is a typo guard, not a promise that ten years
/// of prints fit on the volume.
/// </para>
/// </remarks>
public sealed class TapeRetentionOptions : IValidatableObject
{
    /// <summary>The configuration section these bind to (<c>TapeRetention__*</c>).</summary>
    public const string SectionName = "TapeRetention";

    /// <summary>Days of tape kept when the key is absent. Binds from <c>TapeRetention__Days</c>.</summary>
    public const int DefaultDays = 90;

    /// <summary>The shortest window startup accepts. One week, so a typo cannot empty the tape.</summary>
    public const int MinimumDays = 7;

    /// <summary>The longest window startup accepts. Ten years.</summary>
    public const int MaximumDays = 3_650;

    /// <summary>
    /// How many days of <c>Trades</c> to keep. Default <see cref="DefaultDays"/>.
    /// Rows with <c>TradeTimeUtc</c> strictly before this age are deleted.
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
                + " through " + MaximumDays + ". Zero and a negative number do not turn pruning off.",
                [nameof(Days)]);
        }
    }
}
