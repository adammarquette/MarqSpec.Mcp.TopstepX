using System.Globalization;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// The command line of the <c>reselect-bars</c> verb, once it has been checked (gh#506).
/// </summary>
/// <remarks>
/// <para>
/// <b>A type rather than three out parameters, and a static <see cref="Parse"/> rather than validation
/// inside the composition root.</b> The verb branch in <c>Program</c> runs before the store is migrated and
/// exits the process; a private static there cannot be run by a test, and this repository has already
/// shipped a CLI verb that had never been executed anywhere (gh#37). Everything the verb refuses is refused
/// here, where a test can watch it happen without a container.
/// </para>
/// <para>
/// <b>Nothing here reaches a store.</b> That is the whole contract: an operator who mistypes a symbol or
/// inverts a window learns so before a schema is migrated and before a single row of provenance is
/// rewritten.
/// </para>
/// </remarks>
/// <param name="Instrument">The instrument to re-decide, normalised through <see cref="InstrumentId"/>.</param>
/// <param name="Window">The window the operator named, in UTC.</param>
public sealed record ReselectArguments(InstrumentId Instrument, BarRange Window)
{
    /// <summary>The one line an operator needs when the arguments are wrong.</summary>
    public const string Usage = "reselect-bars <symbol> <fromUtc> <toUtc>";

    /// <summary>
    /// How far <i>before</i> a window the session calendar has to be able to reach.
    /// </summary>
    /// <remarks>
    /// A session opens on the calendar day <b>before</b> its trade date, and its open is a Central
    /// wall-clock time converted back to UTC — so widening a window to whole trade dates asks the calendar
    /// for an instant up to one day plus one offset earlier than the instant that was typed. Two days,
    /// rounded up from that, for the same reason <c>ToolGuards.CalendarReachBeyondAWindow</c> is three at
    /// the other end: a bound that is a little generous costs an operator nothing, and one that is a little
    /// tight is the fault it exists to stop.
    /// </remarks>
    private static readonly TimeSpan _calendarReachBeforeAWindow = TimeSpan.FromDays(2);

    /// <summary>
    /// The earliest instant this verb accepts — the floor under <c>ToolGuards.CalendarHorizon</c>.
    /// </summary>
    /// <remarks>
    /// <b>The same failure as the horizon, one bound along.</b> <c>BarReselector.EffectiveWindowFor</c>
    /// widens the asked window to the whole trade dates it intersects, and a session opens on the previous
    /// calendar day — so a window starting at <see cref="DateTimeOffset.MinValue"/> asks
    /// <see cref="DateOnly"/> for a day before its own minimum and throws
    /// <see cref="ArgumentOutOfRangeException"/> from inside a verb that has already migrated the store.
    /// There is no data before this instant under any circumstances; the bound exists so the refusal is a
    /// sentence rather than a stack trace.
    /// </remarks>
    public static readonly DateTimeOffset CalendarFloor =
        new(DateTimeOffset.MinValue.UtcTicks + _calendarReachBeforeAWindow.Ticks, TimeSpan.Zero);

    /// <summary>
    /// The instant forms accepted, in the order they are tried.
    /// </summary>
    /// <remarks>
    /// <b>Exact, not lenient.</b> <c>DateTimeOffset.TryParse</c> would accept <c>16/06/2026</c> on one host
    /// and <c>06/16/2026</c> on another and read a different real instant from each — a window silently
    /// shifted by months, reported as a clean run. The round-trip <c>"O"</c> form is what a previous run's
    /// log line hands back; <c>yyyy-MM-ddTHH:mm:ssK</c> is what a person types, with or without a zone.
    /// </remarks>
    private static readonly string[] _instantFormats = ["O", "yyyy-MM-ddTHH:mm:ssK"];

    /// <summary>
    /// Reads the verb's arguments, or refuses with a message naming the argument and the rule.
    /// </summary>
    /// <param name="args">The process arguments, <paramref name="args"/><c>[0]</c> being the verb itself.</param>
    /// <param name="registry">The served instruments — the closed list a symbol is checked against.</param>
    /// <returns>The parsed arguments.</returns>
    /// <exception cref="ArgumentException">
    /// The argument count is wrong, the symbol is not served, an instant is not ISO-8601, the window is
    /// empty or inverted, or it falls outside what the session calendar can reason about at either end.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b><see cref="InstrumentRegistry"/> and not <c>InstrumentResolver</c>.</b> The resolver consults
    /// <c>StoreAvailabilityHolder</c>, which this path never sets and whose getter defaults to
    /// <i>available</i> — so its guard would pass vacuously — and it translates a bad symbol into
    /// <c>McpException</c>, an MCP tool-surface type thrown out of a command-line process.
    /// </para>
    /// <para>
    /// <b>Every refusal is an <see cref="ArgumentException"/>,</b> and that is load-bearing: it is the one
    /// exception the verb turns into exit code 2 (<c>ReselectExit</c>). A refusal thrown as anything else
    /// would reach the operator as an unhandled fault instead of an answer.
    /// </para>
    /// </remarks>
    public static ReselectArguments Parse(string[] args, InstrumentRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(registry);

        // Four, exactly: the verb, the symbol and the two instants. An extra argument is refused rather than
        // ignored -- a verb that quietly drops a fourth word is a verb that rewrites a window nobody asked
        // for while looking as though it obeyed.
        if (args.Length != 4)
        {
            // Clamped at zero: `Parse([])` is reachable -- nothing in this method promises the verb itself is
            // present -- and "was given -1 arguments" reads as a bug in the parser rather than as a command
            // line the operator can fix.
            int given = Math.Max(0, args.Length - 1);

            throw new ArgumentException(
                "The reselect-bars verb takes exactly three arguments and was given "
                + given.ToString(CultureInfo.InvariantCulture) + ". Usage: " + Usage + ".",
                nameof(args));
        }

        InstrumentId instrument = ServedInstrument(args[1], registry);
        DateTimeOffset from = Instant(args[2], "fromUtc");
        DateTimeOffset to = Instant(args[3], "toUtc");

        if (to <= from)
        {
            throw new ArgumentException(
                "The window is empty or inverted: fromUtc must be strictly before toUtc. Got "
                + from.ToString("O", CultureInfo.InvariantCulture) + " .. "
                + to.ToString("O", CultureInfo.InvariantCulture) + ". Usage: " + Usage + ".",
                nameof(args));
        }

        // Both ends of the calendar's reach. Only the earlier instant can be under the floor and only the
        // later one can be past the horizon, because the window is already known to run forwards.
        Floor(from, "fromUtc");
        Horizon(to, "toUtc");

        return new ReselectArguments(instrument, new BarRange(from, to));
    }

    /// <summary>Refuses an instant earlier than the session calendar can reason about.</summary>
    /// <param name="instant">The instant.</param>
    /// <param name="ask">The argument name the operator can actually change.</param>
    /// <exception cref="ArgumentException"><paramref name="instant"/> is before <see cref="CalendarFloor"/>.</exception>
    /// <remarks>
    /// Written here rather than delegated the way <see cref="Horizon"/> is, because the tool surface has no
    /// lower bound to borrow: every windowed tool reaches this range only through a stored series, and there
    /// are none. This verb is the one caller that hands the calendar an instant nothing has ever been
    /// fetched for.
    /// </remarks>
    private static void Floor(DateTimeOffset instant, string ask)
    {
        if (instant < CalendarFloor)
        {
            throw new ArgumentException(
                ask + " " + instant.ToString("O", CultureInfo.InvariantCulture)
                + " is before the earliest instant this server's session calendar can reason about, "
                + CalendarFloor.ToString("O", CultureInfo.InvariantCulture)
                + ". A trade date's session opens on the previous calendar day, and below this bound that is "
                + "a day no calendar can hold. There is no stored history down there in any case; move it "
                + "forward.",
                nameof(instant));
        }
    }

    /// <summary>Refuses an instant the session calendar cannot reason about.</summary>
    /// <param name="instant">The instant.</param>
    /// <param name="ask">The argument name the operator can actually change.</param>
    /// <exception cref="ArgumentException"><paramref name="instant"/> is past the calendar's horizon.</exception>
    /// <remarks>
    /// <para>
    /// <b>Delegated to <c>ToolGuards.ValidateInstant</c> so the bound and its explanation have one
    /// definition.</b> The sentence is a long one — an evening instant belongs to the <i>next</i> trade date,
    /// whose close is a Central wall-clock time converted back to UTC, and past the horizon those are dates
    /// <see cref="DateOnly"/> cannot hold (gh#110) — and a second copy of it here would be free to drift from
    /// the tool surface's while describing the same calendar.
    /// </para>
    /// <para>
    /// <b>The exception type is translated, and that is the whole reason for the wrapper.</b> The guard
    /// throws <c>McpException</c>, which is the MCP tool-surface type; out of a command-line process it says
    /// nothing, and the verb's exit-code mapping turns only <see cref="ArgumentException"/> into "you asked
    /// for something wrong". The message is carried across verbatim.
    /// </para>
    /// <para>
    /// This matters here rather than only at the tool boundary because the reselector widens the window to
    /// whole trade dates through exactly that calendar: an instant admitted here surfaces as an
    /// <see cref="ArgumentOutOfRangeException"/> from inside a verb that has already migrated the store.
    /// </para>
    /// </remarks>
    private static void Horizon(DateTimeOffset instant, string ask)
    {
        try
        {
            Tools.ToolGuards.ValidateInstant(instant, ask);
        }
        catch (ModelContextProtocol.McpException ex)
        {
            throw new ArgumentException(ex.Message, nameof(instant), ex);
        }
    }

    /// <summary>Resolves the symbol against the served list, or refuses naming both.</summary>
    /// <param name="symbol">The symbol as the operator wrote it.</param>
    /// <param name="registry">The served instruments.</param>
    /// <returns>The normalised instrument id.</returns>
    /// <exception cref="ArgumentException">The symbol is blank or is not one this server serves.</exception>
    private static InstrumentId ServedInstrument(string symbol, InstrumentRegistry registry)
    {
        try
        {
            return registry.Resolve(symbol);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or ArgumentException)
        {
            // Translated rather than allowed out: KeyNotFoundException from a command line reads as a bug in
            // the verb rather than as a symbol the operator can correct, and the exit-code mapping turns
            // only ArgumentException into "you asked for something wrong".
            throw new ArgumentException(
                "The symbol argument is not one this server serves. " + ex.Message + " Usage: " + Usage + ".",
                nameof(symbol),
                ex);
        }
    }

    /// <summary>Reads one ISO-8601 instant as UTC, or refuses naming the argument and what was written.</summary>
    /// <param name="text">The instant as the operator wrote it.</param>
    /// <param name="ask">The argument name the operator can actually change.</param>
    /// <returns>The instant, in UTC.</returns>
    /// <exception cref="ArgumentException"><paramref name="text"/> is not one of the accepted forms.</exception>
    /// <remarks>
    /// <c>AssumeUniversal | AdjustToUniversal</c>: an instant written with no zone is UTC, because UTC is
    /// what is on the wire and in the store — reading it as the host's local time would shift the whole
    /// window by the operator's offset and re-decide a different set of days. One written <i>with</i> an
    /// offset keeps its meaning and is normalised, so the window carried onwards is always UTC.
    /// </remarks>
    private static DateTimeOffset Instant(string text, string ask) =>
        DateTimeOffset.TryParseExact(
            text,
            _instantFormats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset parsed)
            ? parsed
            : throw new ArgumentException(
                ask + " '" + text + "' is not an ISO-8601 instant. Write it as 2026-06-16T09:00:00Z, "
                + "2026-06-16T04:00:00-05:00 or 2026-06-16T09:00:00 (no zone means UTC). Usage: "
                + Usage + ".",
                nameof(text));
}
