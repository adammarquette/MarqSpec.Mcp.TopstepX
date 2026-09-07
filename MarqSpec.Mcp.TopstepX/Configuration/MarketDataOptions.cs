using System.ComponentModel.DataAnnotations;
using System.Globalization;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// One named session, as an operator states it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Window"/> is <c>HH:mm-HH:mm</c> in Central <b>wall-clock</b> time — the same coordinate
/// <see cref="MarketDataOptions.SessionCloseCentral"/> is stated in, and for the same reason: the exchange
/// states its rules in wall-clock, so a UTC offset would be right for half the year.
/// </para>
/// <para>
/// <see cref="BaseResolutionMinutes"/> is the resolution the session's bars are DERIVED from, not a display
/// choice, and it must divide 60 (ADR-0022 §5). <see cref="SessionWindows.Validate"/> owns that rule and
/// every other one; nothing here re-states them.
/// </para>
/// </remarks>
public sealed class SessionOptions
{
    /// <summary>The session's bounds, <c>HH:mm-HH:mm</c> in Central wall-clock time.</summary>
    public string Window { get; init; } = string.Empty;

    /// <summary>The base bar resolution the session is aggregated from, in minutes.</summary>
    public int BaseResolutionMinutes { get; init; }
}

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
public sealed class MarketDataOptions : IValidatableObject
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

    /// <summary>
    /// The named sessions this server serves, keyed by name — <c>MarketData__Sessions__rth__Window</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The dictionary-by-name shape <c>KeyLevels__Weights__&lt;name&gt;</c> already uses, and
    /// <see cref="StringComparer.OrdinalIgnoreCase"/> for the same reason: the environment-variable provider
    /// preserves whatever case an operator typed, and two entries differing only in case would be two storage
    /// keys the store cannot tell apart.
    /// </para>
    /// <para>
    /// <b>An entry OVERLAYS the shipped set rather than replacing it.</b> A configured name that matches one
    /// of <see cref="SessionDefinition.Defaults"/> replaces it, a new name is added beside them, and a
    /// default nobody configured stands — so leaving all eight keys out ships the four defaults.
    /// </para>
    /// <para>
    /// <b>Per-instrument overrides are out of scope.</b> The shape admits them without a key change when they
    /// are wanted — <c>MarketData__Sessions__rth__Instruments__ES__Window</c> is a property on
    /// <see cref="SessionOptions"/>, not a new section — so nothing here has to be redesigned for it.
    /// </para>
    /// </remarks>
    public Dictionary<string, SessionOptions> Sessions { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The declared holidays, parsed.</summary>
    /// <returns>The holiday strings, trimmed and non-empty.</returns>
    public IReadOnlyList<string> HolidayList() => SplitList(Holidays);

    /// <summary>The configured instruments, parsed.</summary>
    /// <returns>The symbols, trimmed and non-empty.</returns>
    public IReadOnlyList<string> InstrumentList() => SplitList(Instruments);

    /// <summary>
    /// The configured sessions, parsed and overlaid on <see cref="SessionDefinition.Defaults"/>.
    /// </summary>
    /// <returns>
    /// The shipped sessions in their shipped order, each replaced by a configured entry of the same name,
    /// followed by any configured name that is not one of them, in configured order.
    /// </returns>
    /// <exception cref="FormatException">
    /// A configured <see cref="SessionOptions.Window"/> is not two <c>HH:mm</c> times separated by <c>-</c>.
    /// <see cref="Validate"/> refuses that at startup, so options bound through the composition root cannot
    /// reach this — but options built by hand can, and skipping the entry would leave the shipped default
    /// standing under a name the operator had asked to mean something else.
    /// </exception>
    public IReadOnlyList<SessionDefinition> SessionList()
    {
        List<SessionDefinition> definitions = [.. SessionDefinition.Defaults];

        foreach (KeyValuePair<string, SessionOptions> pair in Sessions)
        {
            SessionOptions configured = pair.Value ?? new SessionOptions();
            if (!TryParseWindow(configured.Window, out TimeOnly start, out TimeOnly end))
            {
                throw new FormatException(MalformedWindowRefusal(pair.Key, configured.Window));
            }

            SessionDefinition definition =
                new(pair.Key, start, end, configured.BaseResolutionMinutes);

            int existing = definitions.FindIndex(
                shipped => string.Equals(shipped.Name, pair.Key, StringComparison.OrdinalIgnoreCase));

            if (existing >= 0)
            {
                definitions[existing] = definition;
            }
            else
            {
                definitions.Add(definition);
            }
        }

        return definitions;
    }

    /// <summary>
    /// Refuses a configured session that does not describe a servable one, naming the key and the rule.
    /// </summary>
    /// <param name="validationContext">The validation context.</param>
    /// <returns>One result per effective session that breaks a rule, otherwise none.</returns>
    /// <remarks>
    /// <para>
    /// <b>On the type rather than in a <c>.Validate(...)</c> lambda at the composition root</b>, for the
    /// reason <see cref="KeyLevelDetectionOptions.Validate"/> gives: the rule travels with the value, so a
    /// second place that binds this section cannot bind it unchecked. <c>ValidateDataAnnotations</c> runs
    /// this and <c>ValidateOnStart</c> makes it a boot failure — the right place for it to be loud, because
    /// the alternative is a server serving session bars cut on a window nobody chose (ADR-0022).
    /// </para>
    /// <para>
    /// <b>The EFFECTIVE set is checked, in two passes, because the two halves are fixed by different keys.</b>
    /// A configured entry is refused under its own <c>__Window</c> or <c>__BaseResolutionMinutes</c>; a
    /// shipped default nobody replaced is refused under <see cref="SessionCloseCentral"/>, because that is
    /// the key the operator actually set and the one that made a session stated against the shipped 16:00
    /// close stop describing a session at all. Checking only the configured half would pass a boot with an
    /// unservable <c>full</c> in it and leave the refusal to <c>SessionCatalog</c>'s constructor, which
    /// resolves lazily — a tool error at first request rather than a failure at start. Dropping the session
    /// instead is the substitution this repository forbids.
    /// </para>
    /// <para>
    /// <b>Only against a calendar that parses.</b> A malformed <see cref="SessionCloseCentral"/> is not a
    /// startup failure today — it faults when the calendar singleton is first resolved — so when
    /// <see cref="BarSessionCalendar.Parse"/> throws, session validation is skipped entirely rather than
    /// quietly moving that failure to boot under a different key.
    /// </para>
    /// <para>
    /// The one thing decided here rather than in <c>Domain</c> is WHICH key to name. The base resolution is
    /// tested for the rule <see cref="SessionWindows.Validate"/> states about it purely to choose the suffix;
    /// the rule itself, and the sentence an operator reads, still come from there.
    /// </para>
    /// </remarks>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        List<ValidationResult> results = [];

        BarSessionCalendar calendar;
        try
        {
            calendar = BarSessionCalendar.Parse(SessionCloseCentral, HolidayList());
        }
        catch (FormatException)
        {
            return results;
        }

        foreach (KeyValuePair<string, SessionOptions> pair in Sessions)
        {
            SessionOptions configured = pair.Value ?? new SessionOptions();

            if (!TryParseWindow(configured.Window, out TimeOnly start, out TimeOnly end))
            {
                results.Add(new ValidationResult(
                    MalformedWindowRefusal(pair.Key, configured.Window), [nameof(Sessions)]));
                continue;
            }

            try
            {
                SessionWindows.Validate(
                    new SessionDefinition(pair.Key, start, end, configured.BaseResolutionMinutes), calendar);
            }
            catch (ArgumentException exception)
            {
                results.Add(new ValidationResult(
                    KeyFor(pair.Key, configured) + " is refused. " + exception.Message, [nameof(Sessions)]));
            }
        }

        // Second pass: the shipped sessions NOBODY replaced, against the operator's own calendar. The four
        // defaults are stated against the shipped 16:00 close, and a close the operator moved can leave one of
        // them describing no session at all -- at 13:20 the open is 14:20, and `full` and `rth` then run
        // backwards or past the session measured from there. Without this the boot passes (nothing configured
        // is invalid) and the refusal lands in SessionCatalog's constructor instead, which resolves lazily and
        // surfaces as a tool error on some later request. The alternative -- quietly dropping a session the
        // calendar cannot state -- is the substitution this repository forbids: a served set missing `full` is
        // indistinguishable from a market that produced nothing under it.
        foreach (SessionDefinition shipped in SessionDefinition.Defaults)
        {
            bool replaced = Sessions.Keys.Any(
                key => string.Equals(key, shipped.Name, StringComparison.OrdinalIgnoreCase));

            if (replaced)
            {
                continue;
            }

            try
            {
                SessionWindows.Validate(shipped, calendar);
            }
            catch (ArgumentException exception)
            {
                results.Add(new ValidationResult(
                    SectionName + "__SessionCloseCentral is '" + SessionCloseCentral + "'. The shipped session"
                    + " '" + shipped.Name + "' ("
                    + shipped.StartCentral.ToString("HH:mm", CultureInfo.InvariantCulture) + "-"
                    + shipped.EndCentral.ToString("HH:mm", CultureInfo.InvariantCulture)
                    + ") cannot be stated against it: " + exception.Message + " Set " + SectionName
                    + "__Sessions__" + shipped.Name + "__" + nameof(SessionOptions.Window)
                    + ", or restore the close.",
                    [nameof(SessionCloseCentral), nameof(Sessions)]));
            }
        }

        return results;
    }

    /// <summary>The key an operator edits to fix a refused entry.</summary>
    private static string KeyFor(string name, SessionOptions configured) =>
        SectionName + "__Sessions__" + name + "__"
        + (configured.BaseResolutionMinutes is < 1 or > 60 || 60 % configured.BaseResolutionMinutes != 0
            ? nameof(SessionOptions.BaseResolutionMinutes)
            : nameof(SessionOptions.Window));

    /// <summary>The refusal for a window that is not two <c>HH:mm</c> times separated by <c>-</c>.</summary>
    private static string MalformedWindowRefusal(string name, string window) =>
        SectionName + "__Sessions__" + name + "__" + nameof(SessionOptions.Window) + " is '" + window
        + "', which is not HH:mm-HH:mm in Central wall-clock time. Both halves are parsed EXACTLY, so"
        + " '8:30-15:00' and '08:30:00-15:00:00' are refused rather than read charitably: a session boundary"
        + " that is not the one the operator typed produces a wrong session bar, not a rough one.";

    /// <summary>
    /// Parses a <c>HH:mm-HH:mm</c> window, strictly.
    /// </summary>
    /// <remarks>
    /// <see cref="TimeOnly.TryParseExact(ReadOnlySpan{char}, ReadOnlySpan{char}, IFormatProvider,
    /// DateTimeStyles, out TimeOnly)"/> rather than <c>TryParse</c>, which would accept <c>8:30 AM</c> and
    /// <c>08:30:00</c> — each of which reads as the operator's intent and is not what they wrote.
    /// </remarks>
    private static bool TryParseWindow(string? window, out TimeOnly start, out TimeOnly end)
    {
        start = default;
        end = default;

        string[] halves = (window ?? string.Empty).Split('-');
        return halves.Length == 2
            && TimeOnly.TryParseExact(
                halves[0], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out start)
            && TimeOnly.TryParseExact(
                halves[1], "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out end);
    }

    private static IReadOnlyList<string> SplitList(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : [.. value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
