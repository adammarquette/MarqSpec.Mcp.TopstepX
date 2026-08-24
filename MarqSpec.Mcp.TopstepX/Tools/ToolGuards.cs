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
    /// The coarsest bar this server serves, in minutes — one week.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A product bound, not an arithmetic one.</b> It is not <c>int.MaxValue</c> divided by something that
    /// happens to survive; it is the coarsest thing a minute count can mean. Timeframes run 1m through 60m,
    /// then 240m, then the day at 1,440 and the week at 10,080. Above a week the conventional units are the
    /// calendar month and the quarter, whose length in minutes is <i>not fixed</i> — no integer expresses
    /// them, so there is nothing above this a caller could be asking for.
    /// </para>
    /// <para>
    /// The overflow that prompted it is a consequence, not the reason. See <see cref="LookbackWindow"/>: the
    /// ceiling on its own does not make that arithmetic safe.
    /// </para>
    /// </remarks>
    public const int MaxResolutionMinutes = 7 * 24 * 60;

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

    /// <summary>The row cap a single windowed read may return.</summary>
    public int MaxRows => _options.MaxRows;

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
    /// <b>It moves with the resolution, which is why it is a function and not a constant.</b> At one minute
    /// the bound is two minutes plus three days before the end of the calendar; at the weekly bar
    /// <see cref="MaxResolutionMinutes"/> allows, it is seventeen days.
    /// </para>
    /// </remarks>
    public static DateTimeOffset LastServableEnd(int resolutionMinutes)
    {
        // In ticks, next to the validation that bounds them. The subtraction is safe only because
        // MaxResolutionMinutes bounds the bar span, so the two facts are kept in one place rather than one
        // layer apart -- and the resolution is validated HERE as well, because a public guard that trusts
        // its caller is how gh#69 happened.
        long barTicks = TimeSpan.FromMinutes(ValidateResolution(resolutionMinutes)).Ticks;

        return new DateTimeOffset(
            DateTimeOffset.MaxValue.UtcTicks - (2 * barTicks) - CalendarReachBeyondAWindow.Ticks,
            TimeSpan.Zero);
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
    public static BarRange ValidateBucketSpan(BarRange window, int resolutionMinutes, string ask)
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
                + " a single gap-detection pass will enumerate. Narrow the window, ask for fewer bars, or use "
                + "a coarser resolution. The read is refused rather than shortened to fit, because a series "
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
                + " minutes (one week). Above a week a timeframe is a calendar month or a quarter, whose "
                + "length in minutes is not fixed, so no minute count expresses one.")
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
    /// weekly bar — exactly at the ceiling, nothing out of range about it — 62,500 bars <i>span</i> about
    /// 1,200 years; the reach is <b>four bar spans per bar wanted</b>, so it is about <b>4,800</b> years and
    /// the window starts before year one. <b>The 4× is the whole point</b>: it is what carries a pair that
    /// is legal on both axes past a calendar neither axis knows about — refusal in fact begins around 26,400
    /// weekly bars, not 62,500. A bound on either axis alone is not the rule; the bound is on the product.
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
    /// <see cref="ValidateBucketSpan"/> before it is returned (gh#96).
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
}
