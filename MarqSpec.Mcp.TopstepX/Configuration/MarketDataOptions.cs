using System.ComponentModel.DataAnnotations;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// What this server will serve, and the session model it serves it against.
/// </summary>
/// <remarks>
/// <para>
/// The list values are single comma-separated strings rather than arrays. .NET's environment-variable provider
/// binds an array from indexed keys (<c>Instruments__0</c>, <c>Instruments__1</c>), which is unpleasant to
/// write in a compose file and easy to get subtly wrong — a skipped index silently truncates the list. One
/// string split here is the smaller surface.
/// </para>
/// </remarks>
public sealed class MarketDataOptions
{
    /// <summary>The configuration section this binds to.</summary>
    public const string SectionName = "MarketData";

    /// <summary>
    /// The daily session close in Central <b>wall-clock</b> time, <c>HH:mm</c>.
    /// </summary>
    /// <remarks>
    /// Central rather than UTC because that is how the exchange states it: the close is 16:00 Central in both
    /// January and July, so a fixed UTC offset is wrong for half the year.
    /// <para>
    /// This value decides what counts as missing data. An hour late and the last hour of every day looks like
    /// a permanent gap the server re-fetches forever; an hour early and real bars are never requested. It is
    /// parsed strictly and fails startup rather than being guessed at.
    /// </para>
    /// </remarks>
    [Required]
    public string SessionCloseCentral { get; init; } = "16:00";

    /// <summary>
    /// Declared non-trading days, comma-separated <c>yyyy-MM-dd</c>.
    /// </summary>
    /// <remarks>
    /// Configuration rather than a feed. An undeclared holiday is re-requested all day — a bounded and visible
    /// cost, which is the safe direction for this to be wrong in. A feed would be a dependency and a sync
    /// problem for a handful of dates a year.
    /// </remarks>
    public string Holidays { get; init; } = string.Empty;

    /// <summary>
    /// The largest number of rows a single windowed read returns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Over this, a read <b>refuses and reports the real count</b>. It never truncates: a silently shortened
    /// series is a chart with the interesting part cut off, and it arrives looking exactly like a complete one.
    /// </para>
    /// <para>
    /// <b>It is not the only cap on that quantity, and above 250,000 it stops being the binding one.</b>
    /// <c>BarGapDetector.MaxBucketsPerPass</c> bounds what a single detection pass will enumerate, at a fixed
    /// 250,000, so the effective ceiling on a windowed read is the lesser of the two. Setting this higher
    /// widens no read; it only changes which cap the refusal names. The range is left open to 1,000,000
    /// anyway, because the disagreement is reported at the tool boundary as an error naming both numbers
    /// rather than refused at startup — a server that will not boot on a number it can simply explain is a
    /// server an operator cannot inspect (gh#96).
    /// </para>
    /// </remarks>
    [Range(1, 1_000_000)]
    public int MaxRows { get; init; } = 5_000;

    /// <summary>
    /// The instrument symbols this server will serve, comma-separated.
    /// </summary>
    /// <remarks>
    /// A closed list, so an unlisted symbol is an error naming these rather than an empty series. A wrong
    /// symbol and a quiet market must not be indistinguishable (`R-5.3`).
    /// </remarks>
    [Required]
    public string Instruments { get; init; } = "ES,NQ";

    /// <summary>The declared holidays, parsed.</summary>
    /// <returns>The holiday strings, trimmed and non-empty.</returns>
    public IReadOnlyList<string> HolidayList() => SplitList(Holidays);

    /// <summary>The configured instruments, parsed.</summary>
    /// <returns>The symbols, trimmed and non-empty.</returns>
    public IReadOnlyList<string> InstrumentList() => SplitList(Instruments);

    private static IReadOnlyList<string> SplitList(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
