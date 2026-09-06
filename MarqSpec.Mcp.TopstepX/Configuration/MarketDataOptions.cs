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
    /// widens no <i>windowed</i> read; it only changes which cap the refusal names. It does still widen the
    /// two reads that take their bound from here with no window and no detection pass —
    /// <c>get_key_levels</c>' look-back and <c>search_observations</c>' limit. The range is left open to
    /// 1,000,000 anyway, because the disagreement is reported at the tool boundary as an error naming both
    /// numbers rather than refused at startup — a server that will not boot on a number it can simply
    /// explain is a server an operator cannot inspect (gh#96).
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

    /// <summary>
    /// Whether the process may subscribe to the market hub and write prints to
    /// <c>Trades</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Off by default, and HTTP is not consent.</b> Choosing the HTTP transport does not start
    /// the recorder. A Cowork stdio child against the same store a deployed HTTP instance already
    /// writes would double every volume, and a doubled delta looks like order flow (ADR-0016). The
    /// recorder therefore starts only when the transport is HTTP <i>and</i> this switch is on.
    /// </para>
    /// <para>
    /// Two subscribers on one tape is the failure this exists to prevent. Leaving this on in two
    /// places is now <i>refused</i> rather than tolerated: a start takes an exclusive
    /// per-instrument claim before it subscribes, the second is refused and records nothing, and a
    /// holder writes no print past its own claim's expiry (gh#404). What the claim cannot rule out
    /// is two hosts whose clocks differ by more than the claim's term: each compares its own clock
    /// to one stored expiry, so a taker running far enough ahead can acquire while the holder still
    /// believes it is inside its term, and both write. <b>Those duplicates are counted as
    /// volume</b> — the footprint projection reads every stored print for the instrument, with no
    /// coverage join — so the claim does not make this switch safe to leave on twice. Set it on the
    /// one process meant to record. The claim is the backstop, not the configuration.
    /// </para>
    /// </remarks>
    public bool RecordTape { get; init; }

    /// <summary>
    /// Whether the process may replay stored indicator series at startup.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Off by default, and HTTP is not consent.</b> Choosing the HTTP transport does not start
    /// the warmup. A Cowork stdio child against a large store would stall the handshake paying the
    /// first-read projection (ADR-0014, gh#350). Warmup therefore runs only when the transport is
    /// HTTP <i>and</i> this switch is on.
    /// </para>
    /// <para>
    /// The numbers do not change: the pass is <c>IndicatorRebuilder</c>, so a subsequent
    /// <c>rebuild-indicators</c> is an empty diff (<c>R-2.2</c>). A failure is logged and the host
    /// keeps serving — the first cold read then pays the projection, the same as today.
    /// </para>
    /// </remarks>
    public bool WarmIndicators { get; init; }

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
