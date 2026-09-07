using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace MarqSpec.Mcp.TopstepX.Tools;

/// <summary>
/// The argument checks the market-data tools share.
/// </summary>
/// <remarks>
/// <para>
/// Centralised so the rules cannot drift between tools. A cap enforced in three of four places is not a cap;
/// it is a cap plus one tool that quietly returns everything.
/// </para>
/// <para>
/// <b>Each rule sits at the narrowest thing it is about</b>, which is what stops that drift being reintroduced
/// by shape. The resolution check spent its first life inside <see cref="ValidateWindow"/> and so was reachable
/// only by the tools that validate a window — leaving four that build their own to fall past it (gh#69).
/// </para>
/// </remarks>
public sealed class ToolGuards(IOptions<MarketDataOptions> options)
{
    private readonly MarketDataOptions _options = options.Value;

    /// <summary>
    /// The coarsest bar this server serves, in minutes — one minute short of a session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A product bound, not an arithmetic one.</b> It is not <c>int.MaxValue</c> divided by something that
    /// happens to survive; it is the longest bucket that can still <i>close inside one session</i>. A session
    /// runs 24 hours less the venue's maintenance window — the one hour
    /// <see cref="BarSessionCalendar.DefaultMaintenanceWindow"/> holds — so it is 1,380 minutes long, and the
    /// coarsest bar that fits within one is 1,379. Hence the shape of the expression below: a
    /// <c>const int</c> is a compile-time constant and cannot read a <see cref="TimeSpan"/> field, so the
    /// maintenance hour is written out as <c>60</c> and named only here.
    /// </para>
    /// <para>
    /// <b>A bucket of a session's length or longer can never be a complete bar.</b>
    /// <see cref="BarSessionCalendar.IsExpectedBucket"/> expects a bucket only when it closes at or before the
    /// session's close, and <see cref="BarGapDetector.AlignUp"/> anchors buckets on a fixed UTC-midnight grid
    /// the session does not sit on — so nothing that wide is ever expected, and until gh#498 the day and the
    /// week sailed past this ceiling and were served as an <b>empty series</b> with nothing said. An empty
    /// answer to a question of the wrong shape is indistinguishable from an instrument that produced no data.
    /// </para>
    /// <para>
    /// <b>The day and the week are session bars, not bar resolutions.</b> They are not unavailable and they
    /// are not out of range — they are a different thing, defined on the CME trade date rather than on the
    /// bucket grid, and the session-bars epic gh#496 serves them. That is why the refusal in
    /// <see cref="ValidateResolution"/> names where the answer lives rather than only saying no.
    /// </para>
    /// <para>
    /// The overflow that prompted the ceiling's first version is a consequence, not the reason. See
    /// <see cref="LookbackWindow"/>: the ceiling on its own does not make that arithmetic safe.
    /// </para>
    /// </remarks>
    public const int MaxResolutionMinutes = (24 * 60) - 60 - 1;

    /// <summary>
    /// How far past a window's end the session calendar reasons, on top of the bucket grid's own reach.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Three days, and it is a calendar fact rather than a margin.</b>
    /// <see cref="BarSessionCalendar"/> maps an evening bucket onto the <b>next</b> trade date, and then
    /// expresses that trade date's close as a Central wall-clock time converted back to UTC — a chain that
    /// reaches up to two days and six hours past the bucket it started from, and whose last step is a
    /// <c>DateOnly.AddDays(1)</c> that throws on 9999-12-31.
    /// </para>
    /// <para>
    /// Rounded <i>up</i> to whole days rather than tuned to the hour. Six hours of headroom at the end of
    /// year 9999 buys nothing, and a bound derived to the hour is one the next session-rule change
    /// invalidates in silence.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan CalendarReachBeyondAWindow = TimeSpan.FromDays(3);

    /// <summary>
    /// The last instant this server's session calendar can reason about.
    /// </summary>
    /// <remarks>
    /// Not a bar bound and not a window bound — it is where the <i>session rules</i> stop being expressible,
    /// so it is the floor under every other instant bound here. <see cref="LastServableEnd"/> subtracts the
    /// bucket grid's own reach from it; <see cref="ValidateInstant"/> uses it directly, for the tools that
    /// take a moment and no window at all.
    /// </remarks>
    public static readonly DateTimeOffset CalendarHorizon =
        new(DateTimeOffset.MaxValue.UtcTicks - CalendarReachBeyondAWindow.Ticks, TimeSpan.Zero);

    /// <summary>The row cap a single windowed read may return.</summary>
    public int MaxRows => _options.MaxRows;

    /// <summary>
    /// Refuses a bare instant the session calendar cannot reason about.
    /// </summary>
    /// <param name="instant">The instant.</param>
    /// <param name="ask">The parameter name the caller can actually change.</param>
    /// <returns>The instant.</returns>
    /// <exception cref="McpException">The instant is past <see cref="CalendarHorizon"/>.</exception>
    /// <remarks>
    /// <b>A window guard was never going to reach this.</b> <c>get_market_session</c> takes a moment and no
    /// window, so every bound built around <see cref="ValidateWindow"/> swept straight past it — and
    /// <c>BarSessionCalendar</c> reads an evening instant as belonging to the <i>next</i> trade date, which
    /// on 9999-12-31 is a date <see cref="DateOnly"/> cannot hold. Same axis, same raw
    /// <see cref="ArgumentOutOfRangeException"/>, one tool along (gh#110).
    /// </remarks>
    public static DateTimeOffset ValidateInstant(DateTimeOffset instant, string ask) =>
        instant > CalendarHorizon
            ? throw new McpException(
                ask + " " + instant.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
                + " is past the last instant this server's session calendar can reason about, "
                + CalendarHorizon.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
                + ". An evening instant belongs to the NEXT trade date, whose close is a Central wall-clock "
                + "time converted back to UTC, and past that horizon those are dates no calendar can hold. "
                + "Move it back.")
            : instant;

    /// <summary>
    /// The last instant a window may end at, at a given resolution.
    /// </summary>
    /// <param name="resolutionMinutes">The bar size in minutes.</param>
    /// <returns>The last servable end, inclusive.</returns>
    /// <exception cref="McpException">The resolution is unservable.</exception>
    /// <remarks>
    /// <para>
    /// <b>Two bar spans, not one.</b> The bucket grid is aligned <i>up</i> from the window's start
    /// (<see cref="BarGapDetector.AlignUp"/>), so a window narrower than one bucket names a first bucket up
    /// to a full span past its own <i>end</i> — and the enumerator then tests one span beyond that one. A
    /// window a single tick wide at the end of year 9999 spans <b>zero</b> buckets, so it clears
    /// <see cref="MaxRows"/> at its default of 5,000 and clears
    /// <see cref="BarGapDetector.MaxBucketsPerPass"/> too, and <c>AlignUp</c> still built a
    /// <see cref="DateTimeOffset"/> past <see cref="DateTimeOffset.MaxValue"/> (gh#110).
    /// </para>
    /// <para>
    /// <b>This is a bound on representability, not on size, which is why gh#69, gh#81 and gh#96 all left it
    /// open.</b> Each of those bounded how <i>much</i> a caller may ask for — a resolution, a count, a bucket
    /// span. None of them bounded <i>where</i>, and the failure fires at the <b>default</b> configuration
    /// rather than at an extreme one.
    /// </para>
    /// <para>
    /// <b>It moves with the resolution, which is why it is a function and not a constant.</b> It is
    /// <see cref="CalendarHorizon"/> less those two spans: at one minute, two minutes plus three days before
    /// the end of the calendar; at the 1,379-minute bar <see cref="MaxResolutionMinutes"/> allows, two spans
    /// are 2,758 minutes, so nearly five days before the end of the calendar.
    /// </para>
    /// </remarks>
    public static DateTimeOffset LastServableEnd(int resolutionMinutes)
    {
        // In ticks, next to the validation that bounds them. The subtraction is safe only because
        // MaxResolutionMinutes bounds the bar span, so the two facts are kept in one place rather than one
        // layer apart -- and the resolution is validated HERE as well, because a public guard that trusts
        // its caller is how gh#69 happened.
        long barTicks = TimeSpan.FromMinutes(ValidateResolution(resolutionMinutes)).Ticks;

        return new DateTimeOffset(CalendarHorizon.UtcTicks - (2 * barTicks), TimeSpan.Zero);
    }

    /// <summary>
    /// Refuses a window that spans more buckets than one gap-detection pass will enumerate.
    /// </summary>
    /// <param name="window">The window a read is about to be issued over.</param>
    /// <param name="resolutionMinutes">The bar size in minutes.</param>
    /// <param name="ask">
    /// How the caller expressed the request, so the refusal names the value they can actually change — a
    /// window for <see cref="ValidateWindow"/>, a bar count for <see cref="LookbackWindow"/>.
    /// </param>
    /// <returns>The window.</returns>
    /// <exception cref="McpException">
    /// The window spans more than <see cref="BarGapDetector.MaxBucketsPerPass"/> buckets, ends past
    /// <see cref="LastServableEnd"/>, or the resolution is unservable.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b><see cref="MaxRows"/> and <see cref="BarGapDetector.MaxBucketsPerPass"/> are two independent caps on
    /// the same quantity, and only one of them was ever an <see cref="McpException"/>.</b> The row cap is
    /// operator configuration ranging to 1,000,000; the detection cap is a fixed 250,000. Configure the first
    /// above the second and a request legal on every axis this boundary checked still faulted one layer down,
    /// in <see cref="BarGapDetector.ExpectedBuckets"/>, and crossed the boundary as a raw
    /// <see cref="ArgumentOutOfRangeException"/> (gh#96).
    /// </para>
    /// <para>
    /// <b>The rule is on the bucket count, not on the configuration, because bounding the configuration would
    /// have closed one of the two ways in and left the other open.</b> <c>get_latest_bars</c> never validates
    /// a window — it sizes one from a count, reaching four bar spans per bar wanted plus four days — so a
    /// <c>MaxRows</c> of 100,000, comfortably <i>inside</i> the detection cap, still names 405,760 buckets.
    /// The quantity that reaches the detector is the window, so the window is what is bounded.
    /// </para>
    /// <para>
    /// <b>It refuses rather than narrowing the window to fit.</b> A read trimmed to the cap answers with a
    /// series that is short at one end and says so nowhere — indistinguishable from a complete one, which is
    /// the failure <see cref="ValidateWindow"/> and <see cref="LookbackWindow"/> already refuse to commit.
    /// </para>
    /// <para>
    /// <b>It validates the resolution itself rather than trusting the caller to have done it.</b> The bucket
    /// count is a division by the bar size, so a <c>0</c> arriving here would be a <c>DivideByZeroException</c>
    /// — the same shape of fault, in the guard written to remove it. A public guard that trusts its caller is
    /// how gh#69 happened.
    /// </para>
    /// <para>
    /// <b>It also refuses a window the calendar cannot represent, and that check comes first.</b> Size and
    /// representability are different faults, and the size refusal's advice does not fix the other one:
    /// narrowing a window moves its <i>start</i>, and it is the <i>end</i> that has left the calendar
    /// (gh#110).
    /// </para>
    /// </remarks>
    public static BarRange ValidateBucketSpan(BarRange window, int resolutionMinutes, string ask) =>
        ValidateBucketSpan(
            window,
            resolutionMinutes,
            ask,
            "Narrow the window, ask for fewer bars, or use a coarser resolution.");

    /// <summary>
    /// Refuses a window that spans more buckets than one gap-detection pass will enumerate, with the remedy
    /// the calling tool can actually offer.
    /// </summary>
    /// <param name="window">The window a read is about to be issued over.</param>
    /// <param name="resolutionMinutes">The bar size in minutes.</param>
    /// <param name="ask">How the caller expressed the request.</param>
    /// <param name="remedy">
    /// The action sentence, ending in a full stop — what THIS tool's caller can change. Named rather than
    /// fixed because the three-way advice above is <c>get_bars</c>'s: a session-bar caller has no bar count
    /// and no resolution argument, and advice pointing at parameters that do not exist reads as a dead end
    /// (gh#500).
    /// </param>
    /// <returns>The window.</returns>
    /// <exception cref="McpException">
    /// The window spans more than <see cref="BarGapDetector.MaxBucketsPerPass"/> buckets, ends past
    /// <see cref="LastServableEnd"/>, or the resolution is unservable.
    /// </exception>
    /// <remarks>
    /// Only the <b>size</b> refusal takes the remedy. The representability one keeps its own advice, because
    /// "move the end back" is the only thing that fixes an end past the calendar whichever tool asked.
    /// </remarks>
    public static BarRange ValidateBucketSpan(
        BarRange window, int resolutionMinutes, string ask, string remedy)
    {
        ArgumentNullException.ThrowIfNull(window);
        ValidateResolution(resolutionMinutes);

        // Representability BEFORE size. A window one tick wide at the end of year 9999 spans zero buckets, so
        // every cap below is satisfied and the read went on to fault in BarGapDetector.AlignUp (gh#110).
        DateTimeOffset lastServable = LastServableEnd(resolutionMinutes);
        if (window.End > lastServable)
        {
            throw new McpException(
                ask + " ends at " + window.End.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
                + ", past the last instant this server can serve at "
                + resolutionMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "-minute resolution, "
                + lastServable.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
                + ". Serving a window reaches PAST its end: the bucket grid is aligned up from the start, the "
                + "gap detector tests one bucket beyond the last it yields, and the session calendar maps an "
                + "evening bucket onto the next trade date. Past that instant those are times no calendar "
                + "can express. Move the end back. The read is refused rather than moved back for you, "
                + "because a series short at one end is indistinguishable from a complete one.");
        }

        // The SAME arithmetic ExpectedBuckets performs, deliberately: a guard that computes the count a
        // different way is a guard that disagrees with the thing it is guarding at the boundary.
        long buckets = (window.End - window.Start).Ticks / TimeSpan.FromMinutes(resolutionMinutes).Ticks;

        return buckets > BarGapDetector.MaxBucketsPerPass
            ? throw new McpException(
                ask + " needs " + buckets.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " buckets at " + resolutionMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "-minute resolution, over the "
                + BarGapDetector.MaxBucketsPerPass.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " a single gap-detection pass will enumerate. " + remedy
                + " The read is refused rather than shortened to fit, because a series "
                + "cut at one end is indistinguishable from a complete one.")
            : window;
    }

    /// <summary>
    /// Validates a bar resolution on its own, with no window in sight.
    /// </summary>
    /// <param name="resolutionMinutes">The bar size in minutes.</param>
    /// <returns>The resolution.</returns>
    /// <exception cref="McpException">
    /// <paramref name="resolutionMinutes"/> is not positive, or is coarser than
    /// <see cref="MaxResolutionMinutes"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// Separate from <see cref="ValidateWindow"/> because half this surface never validates a window: the tools
    /// that build their own from a bar count skipped the check entirely, and a <c>0</c> reached
    /// <c>BarGapDetector.AlignDown</c> and crossed the tool boundary as an
    /// <see cref="ArgumentOutOfRangeException"/> — an unhandled fault where a readable tool error belongs
    /// (gh#69).
    /// </para>
    /// <para>
    /// <b>The bound is stated in both directions.</b> gh#69 fixed the floor and left the ceiling reading as
    /// though it were exhaustive; it was not. <c>int.MaxValue</c> minutes is a bar span of ~1.3 × 10^18 ticks,
    /// and it faulted for the same reason a <c>0</c> did — while sailing past the new guard, because it is
    /// positive (gh#81).
    /// </para>
    /// <para>
    /// <b>The ceiling refuses the day and the week, and the refusal says where they live instead.</b> Both sat
    /// <i>inside</i> the old ceiling and neither was ever servable: <see cref="MaxResolutionMinutes"/> explains
    /// why no bucket of a session's length can be a complete bar, and <c>get_bars</c> at 1,440 answered with an
    /// empty series until gh#498. Refusing them silently would swap one wrong answer for a second: they are
    /// <b>session bars</b>, not coarse resolutions, so the message names the session-bar tools of gh#496 rather
    /// than leaving a caller to read "coarser than the largest bar" as "this market has no daily data".
    /// </para>
    /// <para>
    /// <b>Static, and deliberately so.</b> Unlike the row cap this rule depends on no configuration, so it can
    /// be reached from a pure policy function — <see cref="SnapshotTools.ResolveResolutions"/> — without that
    /// function acquiring a constructor, a container, and a reason not to be pinned by a test that needs
    /// neither.
    /// </para>
    /// </remarks>
    public static int ValidateResolution(int resolutionMinutes)
    {
        if (resolutionMinutes <= 0)
        {
            throw new McpException(
                "resolutionMinutes must be positive; got "
                + resolutionMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
        }

        return resolutionMinutes > MaxResolutionMinutes
            ? throw new McpException(
                "resolutionMinutes "
                + resolutionMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " is coarser than the largest bar this server serves, "
                + MaxResolutionMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " minutes, one minute short of a session (24 hours less the venue's one-hour maintenance "
                + "window). A bucket that long or longer can never close inside a single session, "
                + "so it is a session bar rather than a bar resolution. The day and the week are not "
                + "unavailable and they are not out of range; ask the session-bar tools (gh#496, arriving in "
                + "gh#500) for them.")
            : resolutionMinutes;
    }

    /// <summary>
    /// Sizes the look-back window a bar count needs, anchored on a closed bucket.
    /// </summary>
    /// <param name="end">The window end — the last closed bucket, exclusive.</param>
    /// <param name="resolutionMinutes">The bar size in minutes, already validated.</param>
    /// <param name="count">How many bars are wanted, already validated.</param>
    /// <returns>The window to read.</returns>
    /// <exception cref="McpException">
    /// The window would start before the calendar does, end past <see cref="LastServableEnd"/>, span more
    /// buckets than one gap-detection pass will enumerate, or <paramref name="count"/> sizes no window at
    /// all.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The reach is four bar spans per bar wanted, plus four days. Sessions are shut roughly a quarter of the
    /// clock and closed for whole weekends, so a window sized to the bar count alone comes up short.
    /// </para>
    /// <para>
    /// <b>It lives here, and it is widened to <see cref="Int128"/>, because the <see cref="long"/> form was
    /// unchecked.</b> <c>barSize.Ticks * count * 4</c> wrapped negative at a large resolution and
    /// <c>end - reach</c> left the tool boundary as a raw <see cref="ArgumentOutOfRangeException"/> (gh#81).
    /// </para>
    /// <para>
    /// <b><see cref="MaxResolutionMinutes"/> does not on its own make this safe, which is why the check is
    /// here as well as there.</b> <c>MaxRows</c> is operator configuration and ranges to 1,000,000. At a
    /// 1,379-minute bar — exactly at the ceiling, nothing out of range about it — 500,000 bars <i>span</i>
    /// about 1,311 years; the reach is <b>four bar spans per bar wanted</b>, so it is about <b>5,245</b> years
    /// and the window starts before year one. <b>The 4× is the whole point</b>: it is what carries a pair that
    /// is legal on both axes past a calendar neither axis knows about — refusal in fact begins around 193,000
    /// such bars, not 500,000. A bound on either axis alone is not the rule; the bound is on the product.
    /// </para>
    /// <para>
    /// <b>The refusal is stated at both ends of the reach, because the narrowing cast back to
    /// <see cref="long"/> is unchecked.</b> A negative <paramref name="count"/> makes the product negative, so
    /// it sails past the upper comparison and wraps on the cast — reintroducing, inside this guard, the fault
    /// the guard exists to remove. Reachable only directly today, since every tool validates its count first;
    /// closed here anyway, because a public guard that trusts its caller is how gh#69 happened.
    /// </para>
    /// <para>
    /// <b>It refuses rather than clamping to the start of the calendar.</b> A clamped window answers with
    /// however many bars the store happens to hold, and a short series is indistinguishable from a complete
    /// one — the same reason <see cref="ValidateWindow"/> refuses an over-cap window instead of truncating it.
    /// </para>
    /// <para>
    /// <b>The calendar is not the only bound, which is the same lesson one level along.</b> A window can sit
    /// comfortably inside the calendar and still be wider than one gap-detection pass will enumerate: 100,000
    /// one-minute bars are inside a <c>MaxRows</c> of 100,000 and reach 405,760 buckets, past the 250,000
    /// <see cref="BarGapDetector.MaxBucketsPerPass"/> allows. So the window this produces goes through
    /// <see cref="ValidateBucketSpan(BarRange, int, string)"/> before it is returned (gh#96).
    /// </para>
    /// </remarks>
    public static BarRange LookbackWindow(DateTimeOffset end, int resolutionMinutes, int count)
    {
        Int128 reach = ((Int128)TimeSpan.FromMinutes(resolutionMinutes).Ticks * count * 4)
            + (4 * TimeSpan.TicksPerDay);

        if (reach <= 0)
        {
            throw new McpException(
                "count " + count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " at resolutionMinutes "
                + resolutionMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " sizes no window to read. A look-back reaches backwards from the last closed bucket, so "
                + "the count must be positive. Ask for at least one bar.");
        }

        if (reach > end.UtcTicks)
        {
            throw new McpException(
                "count " + count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " at resolutionMinutes "
                + resolutionMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " reaches back further than the calendar goes, so there is no window to read. "
                + "Ask for fewer bars, or a finer resolution.");
        }

        // The calendar is not the only bound on this reach. A count and a resolution can both be legal, and
        // the window they name still be wider than one gap-detection pass will enumerate -- 100,000 one-minute
        // bars sit inside a MaxRows of 100,000 and reach 405,760 buckets, four times the count plus four days
        // (gh#96). The reach guard above says nothing about that, so the window it produces is measured here
        // before it is handed to a caller who will read bars over it.
        return ValidateBucketSpan(
            new BarRange(end - TimeSpan.FromTicks((long)reach), end),
            resolutionMinutes,
            "count " + count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " at resolutionMinutes "
                + resolutionMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Validates a requested window and returns it as a range.
    /// </summary>
    /// <param name="fromUtc">The start, inclusive.</param>
    /// <param name="toUtc">The end, exclusive.</param>
    /// <param name="resolutionMinutes">The bar size, used to estimate the row count.</param>
    /// <returns>The validated range.</returns>
    /// <exception cref="McpException">
    /// The resolution is not positive or is coarser than <see cref="MaxResolutionMinutes"/>, the window is
    /// empty or inverted, it ends past <see cref="LastServableEnd"/>, or it spans more buckets than
    /// <see cref="MaxRows"/> or than <see cref="BarGapDetector.MaxBucketsPerPass"/>.
    /// </exception>
    /// <remarks>
    /// <b>The effective ceiling is the lesser of the two caps</b>, because both bound the same quantity —
    /// <see cref="MaxRows"/>, which an operator configures, and
    /// <see cref="BarGapDetector.MaxBucketsPerPass"/>, which they cannot (gh#96). Configured above 250,000 the
    /// row cap stops being the binding one, and the refusal says so rather than faulting below this boundary.
    /// <para>
    /// <b>An over-cap window refuses and reports the real count.</b> It does not truncate: a shortened series
    /// arrives looking exactly like a complete one, and the part that was cut is the part the caller was
    /// reaching for.
    /// </para>
    /// </remarks>
    public BarRange ValidateWindow(DateTimeOffset fromUtc, DateTimeOffset toUtc, int resolutionMinutes)
    {
        ValidateResolution(resolutionMinutes);

        if (toUtc <= fromUtc)
        {
            throw new McpException(
                "The window is empty or inverted: fromUtc must be strictly before toUtc. Got "
                + fromUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + " .. "
                + toUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + ".");
        }

        long buckets = (toUtc - fromUtc).Ticks / TimeSpan.FromMinutes(resolutionMinutes).Ticks;
        if (buckets > MaxRows)
        {
            throw new McpException(
                "That window spans about "
                + buckets.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " buckets at " + resolutionMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "-minute resolution, over this server's cap of "
                + MaxRows.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ". Narrow the window or ask for a coarser resolution. "
                + "The read is refused rather than truncated, because a shortened series is indistinguishable "
                + "from a complete one.");
        }

        // The row cap is checked FIRST, and that order is the message. Both caps bound the same quantity, so
        // an over-wide window can be past both -- and naming the detection cap would send an operator to a
        // constant they cannot change, past the one they configured. The tighter cap is the useful one, and
        // below MaxRows = 250,000 the row cap is always the tighter.
        return ValidateBucketSpan(
            new BarRange(fromUtc.ToUniversalTime(), toUtc.ToUniversalTime()),
            resolutionMinutes,
            "That window");
    }

    /// <summary>
    /// Validates a requested bar count.
    /// </summary>
    /// <param name="count">The count.</param>
    /// <returns>The count.</returns>
    /// <exception cref="McpException">The count is not positive, or exceeds the cap.</exception>
    public int ValidateCount(int count)
    {
        if (count <= 0)
        {
            throw new McpException(
                "count must be positive; got "
                + count.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
        }

        return count > MaxRows
            ? throw new McpException(
                "count " + count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " exceeds this server's cap of "
                + MaxRows.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".")
            : count;
    }

    /// <summary>
    /// Validates a requested session window and resolves the trade dates it wholly contains.
    /// </summary>
    /// <param name="fromUtc">The start, inclusive.</param>
    /// <param name="toUtc">The end, exclusive.</param>
    /// <param name="definition">The session being asked for.</param>
    /// <param name="calendar">The session calendar the trade dates come from.</param>
    /// <returns>The validated window and the trade dates inside it.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="McpException">
    /// The window is empty or inverted, ends past <see cref="CalendarHorizon"/>, spans more base buckets than
    /// <see cref="BarGapDetector.MaxBucketsPerPass"/>, or names more trade dates than <see cref="MaxRows"/>.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b>A session window is not a bar window, so <see cref="ValidateWindow"/> cannot be reused.</b> That one
    /// measures rows in buckets of the resolution asked for, and a session is longer than
    /// <see cref="MaxResolutionMinutes"/> — it would refuse every call before it counted anything. Here the
    /// rows are trade dates and the buckets are the session's base bars, so the two caps are read off two
    /// different quantities.
    /// </para>
    /// <para>
    /// <b>An over-cap window refuses and reports the real count</b>, on the same terms as every other read on
    /// this boundary: a series shortened to fit arrives looking exactly like a complete one.
    /// </para>
    /// </remarks>
    public SessionWindowPlan ValidateSessionWindow(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        SessionDefinition definition,
        BarSessionCalendar calendar)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(calendar);

        // Same fault, same words as ValidateWindow. SessionWindows.TradeDatesIn answers an empty window with
        // an empty list, which on this surface reads as "no session traded then" rather than "you asked for
        // nothing" -- an absence indistinguishable from an answer.
        if (toUtc <= fromUtc)
        {
            throw new McpException(
                "The window is empty or inverted: fromUtc must be strictly before toUtc. Got "
                + fromUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + " .. "
                + toUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture) + ".");
        }

        // Representability next, and it is the END that leaves the calendar: TradeDatesIn steps one day past
        // the window's last market date before it walks, so an end at the top of the DateOnly range faults
        // inside the Domain rather than refusing here (gh#110's shape, on the session surface).
        ValidateInstant(toUtc, "toUtc");

        BarRange window = new(fromUtc.ToUniversalTime(), toUtc.ToUniversalTime());

        // The base-bucket cap goes BEFORE the row cap here, which is the opposite of ValidateWindow's order,
        // and the reason is that the row count is not arithmetic on this surface: it is a calendar walk over
        // every day the window touches. The bucket span is what bounds that walk, so measuring it first is
        // what stops a window of arbitrary width being enumerated a day at a time before anything refuses it.
        // The base resolution is the session's own -- the buckets a session bar is derived from are what a
        // read of it enumerates.
        ValidateBucketSpan(
            window,
            definition.BaseResolutionMinutes,
            "That window",
            "Narrow the window, or ask the operator for a coarser base resolution for this session.");

        IReadOnlyList<DateOnly> tradeDates = SessionWindows.TradeDatesIn(calendar, definition, window);
        if (tradeDates.Count > MaxRows)
        {
            throw new McpException(
                "That window names "
                + tradeDates.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " " + definition.Name + " trade dates, over this server's cap of "
                + MaxRows.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " rows. Narrow the window. "
                + "The read is refused rather than truncated, because a shortened series is indistinguishable "
                + "from a complete one.");
        }

        return new SessionWindowPlan(window, tradeDates);
    }

    /// <summary>
    /// Validates a requested session count and resolves the trade dates behind it.
    /// </summary>
    /// <param name="count">How many closed sessions the caller asked for.</param>
    /// <param name="definition">The session being asked for.</param>
    /// <param name="calendar">The session calendar the trade dates come from.</param>
    /// <param name="now">The instant to look back from — the session in progress is never one of these.</param>
    /// <returns>The trade dates, ascending, oldest first.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="McpException">
    /// The count is not positive, exceeds <see cref="MaxRows"/>, <paramref name="now"/> is past
    /// <see cref="CalendarHorizon"/>, or the calendar carries fewer closed sessions than the count inside the
    /// bounded walk.
    /// </exception>
    /// <remarks>
    /// <para>
    /// <b><see cref="ValidateCount"/> is not the whole guard, and the gap is a live one.</b> It admits any
    /// count up to <see cref="MaxRows"/> — 5,000 by default — while
    /// <see cref="SessionWindows.LastClosedTradeDates"/> walks a bounded span of calendar days and throws a
    /// raw <see cref="ArgumentOutOfRangeException"/> when it finds fewer sessions than that inside it. A
    /// holiday-dense calendar is enough to reach it, and an unhandled Domain fault on a tool boundary is
    /// exactly what these guards exist to prevent.
    /// </para>
    /// <para>
    /// The refusal states the span rather than quoting the Domain's message, because a caller's exception
    /// text is free text and this surface carries none (ADR-0008). It blames the <b>calendar</b> rather than
    /// the walk: every one of those days is walked, and what runs out is the sessions inside them.
    /// </para>
    /// <para>
    /// <b>The catch is filtered on <c>ParamName</c>, and unfiltered it was wrong.</b> The same walk throws the
    /// same exception type for an instant <see cref="DateOnly"/> cannot hold, and swallowing that one into a
    /// count refusal tells a caller to ask for fewer sessions when the argument out of reach is
    /// <paramref name="now"/>. <see cref="ValidateInstant"/> takes that case first; the filter is the second
    /// line, for anything below that throws about something other than the count.
    /// </para>
    /// </remarks>
    public IReadOnlyList<DateOnly> ValidateSessionCount(
        int count,
        SessionDefinition definition,
        BarSessionCalendar calendar,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(calendar);

        int wanted = ValidateCount(count);

        // Representability before satisfiability, the same order and the same reason as
        // ValidateSessionWindow: the walk starts one day AHEAD of `now`'s market date, so an instant at the
        // top of the range faults inside the Domain -- and it faults with the same exception type the
        // unsatisfiable-count path uses. Judged here, the two stay distinguishable.
        ValidateInstant(now, "now");

        try
        {
            return SessionWindows.LastClosedTradeDates(calendar, definition, now, wanted);
        }
        catch (ArgumentOutOfRangeException ex) when (ex.ParamName == "count")
        {
            // RESTATED CONSTANT, deliberately and with the drift in view: the walk span is computed as
            // `(count * 4) + 15` inside SessionWindows.LastClosedTradeDates, and it cannot be read back from
            // there -- it is a local. Change it there and this number is wrong here; the two are named
            // together so the next reader of either sees the other.
            int span = (wanted * 4) + 15;

            // The message names the CALENDAR as the cause, not the walk. The server does walk every one of
            // those days; what runs out is the sessions inside them, and a refusal reading "this server only
            // walks back N days" sends a reader to widen a bound that is not the one that bit. How many it
            // did find is the other half of the story and is NOT stated: the Domain reports it only inside
            // the exception message, which is free text this surface does not repeat (ADR-0008), and reading
            // it back would take an accessor on SessionWindows -- deferred rather than smuggled in here.


            throw new McpException(
                "count " + wanted.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " asks for more closed " + definition.Name
                + " sessions than the calendar holds in the "
                + span.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " calendar days this server walks back over. Ask for fewer.");
        }
    }
}

/// <summary>
/// A validated session window and the trade dates it wholly contains.
/// </summary>
/// <param name="Window">The window, in UTC.</param>
/// <param name="TradeDates">
/// The trade dates whose <b>whole</b> session lies inside <paramref name="Window"/>, ascending. Built from the
/// calendar rather than from the store, so it never carries a duplicate.
/// </param>
/// <remarks>
/// <para>
/// Both halves are returned because both are already computed: the caller reads the dates and reports the
/// window, and recomputing either one from the other is how the two drift apart.
/// </para>
/// <para>
/// <b>A calendar walk cannot produce the same date twice</b>, so the <c>ArgumentException</c>
/// <c>SessionBarService.GetAsync</c> throws on a duplicated trade date is unreachable from a tool that asks
/// for its dates here. No tool catches it, and none should: a catch for an impossible fault is a catch nobody
/// can test, and it would hide the day this list stops coming from the calendar.
/// </para>
/// </remarks>
public sealed record SessionWindowPlan(BarRange Window, IReadOnlyList<DateOnly> TradeDates);
