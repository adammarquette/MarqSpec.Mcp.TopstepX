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
    /// How long one session is in Central wall-clock time, in minutes — 24 hours less the venue's
    /// maintenance window.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The one hour <see cref="BarSessionCalendar.DefaultMaintenanceWindow"/> holds, written out as
    /// <c>60</c> because a <c>const int</c> is a compile-time constant and cannot read a
    /// <see cref="TimeSpan"/> field. It is the line above which a bucket stops being a bar at all, and the
    /// number <see cref="ShortestSessionMinutes"/> is taken from.
    /// </para>
    /// <para>
    /// <b>It is a WALL-CLOCK span.</b> A session runs from the reopen on the previous market date to the
    /// close on the trade date. At the shipped 16:00 close the reopen is 17:00, so every session is exactly
    /// 1,380 minutes. A close before 02:00 Central would put the spring-forward transition inside the
    /// session and shorten it to 1,320 minutes once a year — that close is refused at calendar construction
    /// (gh#613), so <see cref="ShortestSessionMinutes"/> equals this value for every configuration the server
    /// accepts.
    /// </para>
    /// </remarks>
    public const int SessionMinutes = (24 * 60) - 60;

    /// <summary>
    /// The shortest session this calendar can produce at any configured close, in minutes.
    /// </summary>
    /// <remarks>
    /// Equal to <see cref="SessionMinutes"/> since gh#613: a close whose reopen lands before 03:00 Central
    /// is refused at calendar construction, because spring-forward deletes the wall-clock hour [02:00, 03:00)
    /// and nothing else in the calendar's contract says a session may be an hour shorter once a year.
    /// <see cref="MaxResolutionMinutes"/> remains at 660 rather than the pigeonhole bound of 690 — raising
    /// the ceiling is a separate trade on its own evidence, not a side effect of this refusal.
    /// </remarks>
    public const int ShortestSessionMinutes = SessionMinutes;

    /// <summary>
    /// The coarsest bar this server serves, in minutes — half the shortest session.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A product bound, not an arithmetic one</b>, and a <i>derived</i> one rather than a chosen number.
    /// It is the widest bucket the UTC grid is guaranteed to fit inside <b>every</b> session, at every
    /// session close an operator can configure.
    /// </para>
    /// <para>
    /// <b>The derivation, so the next reader can re-run it.</b>
    /// <see cref="BarGapDetector.AlignUp"/> anchors buckets on a fixed grid struck from the .NET epoch — not
    /// on the session open — and <see cref="BarSessionCalendar.IsExpectedBucket"/> expects a bucket only when
    /// it opens inside the session and closes at or before that session's close. So a session of <c>S</c>
    /// minutes admits an <c>r</c>-minute bucket exactly when some multiple of <c>r</c> lands in
    /// <c>[open, close - r]</c> — a run of <c>S - r + 1</c> consecutive whole minutes. A run of <c>n</c>
    /// consecutive integers is <i>certain</i> to contain a multiple of <c>r</c> only while <c>n &gt;= r</c>,
    /// so the guarantee holds exactly while <c>S - r + 1 &gt;= r</c>, i.e. <c>r &lt;= (S + 1) / 2</c>. C#'s
    /// integer division truncates, which gives the largest admissible <i>integer</i> for an odd <c>S</c> and
    /// for an even one alike.
    /// </para>
    /// <para>
    /// <b><c>S</c> is <see cref="ShortestSessionMinutes"/>, which equals <see cref="SessionMinutes"/> since
    /// gh#613 refused the closes that would shorten a session.</b> The pigeonhole bound on 1,380 is 690; this
    /// ceiling stays at <b>660</b> until a separate card justifies raising it — the conservative bound still
    /// fits every admissible close, and 661 is measured to miss.
    /// </para>
    /// <para>
    /// <b>The two derivations gh#538 offered agree here by arithmetic accident, so only one is used.</b>
    /// 660 is both the pigeonhole bound on 1,320 and 1,320's largest proper divisor — but that pairing holds
    /// only because <c>S</c> is even. At <c>S = 1,379</c> the pigeonhole form gives 690 and the
    /// largest-proper-divisor form gives 197. The general form is the one implemented.
    /// </para>
    /// <para>
    /// <b>Above it the answer depends on the day and on the configured close, which is the failure this
    /// bound exists to remove (gh#538).</b> A 1,379-minute bucket is expected only when the grid lands within
    /// a minute of the session open, so <c>get_bars</c> at 1,379 answered an <b>empty series with
    /// <c>venueRequests: 0</c></b> on all but a handful of scattered trade dates — the exact shape gh#498
    /// abolished one minute higher, and an answer indistinguishable from an instrument that produced no data.
    /// </para>
    /// <para>
    /// <b>It over-rejects, deliberately, and the refusal says so rather than claiming the band never
    /// works.</b> Measured over sixteen years: at the shipped 16:00 close every width from 661 to 690 fits on
    /// every trade date, and so do 692, 696, 700 and 720 — thirty-four in all. Widen the sweep to the closes
    /// whose session can lose an hour and <b>two</b> survive; widen the window at one such close from one
    /// year to sixteen and the count falls 31 → 28 → 19. A survivor list is what a sweep did not disprove,
    /// which is not what a bound is. They are refused because <see cref="ValidateResolution"/> is
    /// deliberately <c>static</c> and reads no configuration, so serving them would make the servable set
    /// depend invisibly on <c>SessionCloseCentral</c>; a bound is a guarantee, and a list of widths that
    /// happen to survive one sweep is a table of coincidences.
    /// <c>ResolutionGuardTests.AboveTheCeiling_TheGuaranteeFails_AndTheCoincidencesAreNamed</c> measures both
    /// halves, and <c>TheGridRefusal_ConcedesTheBandSometimesFits</c> pins the message against overclaiming
    /// them away.
    /// </para>
    /// <para>
    /// <b>A bucket of a session's length or longer is a different thing again — not coarse, but a session
    /// bar.</b> A bucket <i>longer</i> than a session can never close inside one; one <i>exactly</i> a
    /// session's length can, but only when the grid lands exactly on the session open — 4.34% of trade dates
    /// at the shipped close — so the claim that carries the refusal is the definitional one rather than the
    /// arithmetic one: a bar covering a whole session is defined on the CME trade date rather than on the
    /// bucket grid, and the session-bars epic gh#496 serves it through <c>get_session_bars</c> and
    /// <c>get_latest_session_bars</c> on <see cref="SessionBarTools"/>. <see cref="ValidateResolution"/>
    /// keeps that as a <i>separate</i> refusal with its own message, because "ask for a narrower bar" is the
    /// wrong advice for a caller who wanted a daily bar.
    /// </para>
    /// <para>
    /// The overflow that prompted the ceiling's first version is a consequence, not the reason. See
    /// <see cref="LookbackWindow"/>: the ceiling on its own does not make that arithmetic safe.
    /// </para>
    /// </remarks>
    public const int MaxResolutionMinutes = 660;

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
    /// the end of the calendar; at the 660-minute bar <see cref="MaxResolutionMinutes"/> allows, two spans
    /// are 1,320 minutes, so nearly four days before the end of the calendar.
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
    /// <b>There are two refusals above the ceiling, not one, because there are two different mistakes
    /// (gh#538).</b> At or above <see cref="SessionMinutes"/> the caller asked for something that is not a bar
    /// resolution at all — a bucket covering a whole session is a session bar by definition, whether or not
    /// the grid ever lets it close inside one — and the remedy is another tool. Between <see cref="MaxResolutionMinutes"/> and that
    /// line the caller asked for a real bar the grid cannot be relied on to fit inside a session, and the
    /// remedy is a narrower one — so that message states the rule, its arithmetic, and the fact that a few
    /// widths in the band do fit every day and are refused anyway. A refusal that claimed they never produce
    /// a bar would be easier to write and would not be true, and a message that overstates what it measured
    /// is how a guard loses the reader who could have corrected it.
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

        if (resolutionMinutes >= SessionMinutes)
        {
            throw new McpException(
                "resolutionMinutes "
                + resolutionMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " covers a whole session or more — a session is "
                + SessionMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " minutes of Central wall clock, 24 hours less the venue's one-hour maintenance window. A "
                + "bucket longer than that can never close inside a single session, and one exactly that "
                + "long can only when the grid lands exactly on the session open — 4% of trade dates at the "
                + "shipped close. Either way it is a session bar rather than a bar resolution: a bar "
                + "covering a whole session is defined on the CME trade date, not on the bucket grid. The "
                + "day and the week are not unavailable and they are not out of range; ask get_session_bars "
                + "or get_latest_session_bars (gh#496) for them. The largest bar resolution this server "
                + "serves is "
                + MaxResolutionMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " minutes.");
        }

        return resolutionMinutes > MaxResolutionMinutes
            ? throw new McpException(
                "resolutionMinutes "
                + resolutionMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " is coarser than the largest bar this server serves, "
                + MaxResolutionMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " minutes — half of the "
                + ShortestSessionMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + "-minute shortest session. Buckets are anchored on a fixed UTC grid rather than on the "
                + "session open, so a bucket wider than half a session is not guaranteed to open and close "
                + "inside one: whether it fits depends on where that grid falls on the day, and on the trade "
                + "dates where it does not fit the series comes back empty with nothing said. "
                + MaxResolutionMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " is the widest bucket that fits on every trade date at every session close an operator "
                + "can configure, because a run of S - r + 1 consecutive minutes holds a multiple of r only "
                + "while S - r + 1 >= r, and S is "
                + ShortestSessionMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " minutes for every session close this server accepts. It is tight: "
                + "661 already misses. Widths above it are not all useless — every one from 661 to 690 fits "
                + "every trade date at the shipped 16:00 close, as do 692, 696, 700 and 720 — but which "
                + "widths those are depends on the configured close, which this check deliberately does not "
                + "read, so they are refused with the rest rather than served by coincidence. Ask for "
                + MaxResolutionMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " minutes or less; for the day and the week ask get_session_bars or "
                + "get_latest_session_bars (gh#496).")
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
    /// 660-minute bar — exactly at the ceiling, nothing out of range about it — 500,000 bars <i>span</i>
    /// about 627 years; the reach is <b>four bar spans per bar wanted</b>, so it is about <b>2,510</b> years
    /// and the window starts before year one. <b>The 4× is the whole point</b>: it is what carries a pair that
    /// is legal on both axes past a calendar neither axis knows about — refusal in fact begins around 403,000
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
    /// <see cref="BarGapDetector.MaxBucketsPerPass"/>, names zero whole sessions, or names more trade dates
    /// than <see cref="MaxRows"/>.
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
    /// <para>
    /// <b>Zero trade dates refuses too, and for the same reason an empty window does.</b> A non-empty window
    /// inside which no session both opens and closes — nine hours of a Monday over an `rth` session that runs
    /// longer — passes every check above and would otherwise answer <c>bars: []</c> and <c>absent: []</c>,
    /// which reads as "this instrument did not trade" rather than "the window is narrower than any session".
    /// The refusal names the nearest whole session so the caller can widen to it, or, when a market closure
    /// outruns the scan that looks for one, says only to widen it (gh#568). The refusal is a narrowing of
    /// the "wholly contained and in neither list ⇒ did not trade" signal: a window
    /// holding <i>only</i> non-trading days now refuses instead of reporting them. The signal survives
    /// wherever the window also holds one whole session, which is the only shape it was readable in anyway.
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

        // ZERO is not "nothing traded" -- it is no session both OPENING and CLOSING inside the window, since
        // TradeDatesIn admits a trade date only when its WHOLE session sits inside the window. Left
        // unrefused, a non-empty window naming no whole session answers exactly like the empty
        // window ValidateWindow already refuses to avoid: bars: [] and absent: [] both, read cold as "this
        // instrument did not trade" rather than "the window asked for something narrower than any session"
        // (gh#568). Checked before the row cap, which a count of zero can never be over anyway.
        if (tradeDates.Count == 0)
        {
            throw new McpException(NoWholeSessionMessage(window, definition, calendar));
        }

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
    /// The refusal for a window that names zero whole sessions — no session opens and closes inside it.
    /// </summary>
    /// <param name="window">The validated window.</param>
    /// <param name="definition">The session being asked for.</param>
    /// <param name="calendar">The calendar the nearest whole session is looked up on.</param>
    /// <returns>
    /// The message, naming the window, the session, and — when one can be named at all — the nearest whole
    /// session's bounds.
    /// </returns>
    /// <remarks>
    /// <para>
    /// <b>The opening clause says only what is certain: no session both opens and closes inside the
    /// window.</b> "Every session it touches is clipped" would be the cause in the common case and merely
    /// vacuous in the others — a window over a weekend, a declared holiday, the maintenance break or the gap
    /// between two sessions touches no session at all, so there is nothing it clipped, and a caller asking
    /// "did this instrument trade?" would be pointed at the wrong remedy.
    /// </para>
    /// <para>
    /// <b>"Nearest" is measured, not assumed, and it is measured over every trade date the window could
    /// reach.</b> The one named is whichever whole session needs the <b>smaller total widening</b> to fit:
    /// how far the window's start would move back, plus how far its end would move out. Naming the start's
    /// trade date unconditionally advised a widening many times larger than the one that answers, and handed
    /// back the wrong day's data to a caller who wanted the end of the window (gh#568).
    /// </para>
    /// <para>
    /// <b>The scan starts on the window's own market dates and widens outward until nothing further out
    /// could win.</b> Scoring only the trade dates of the two <i>endpoints</i> was the earlier defect and a
    /// subtler one: <see cref="BarSessionCalendar.TradeDateFor"/> answers <see langword="null"/> in the
    /// maintenance hour, at a weekend and on a declared holiday, so an endpoint landing in a gap dropped the
    /// session on the far side of that gap out of the running — usually the nearest one. Over a 30-minute
    /// grid of `rth` windows that named the wrong session in 6.6% of refusals and withheld bounds entirely
    /// from 30% of them, with a whole session sitting close by in every case. A fixed ±1-day widening does
    /// not fix it either: it clears `rth` and leaves `asia` at 5.2%, because a window deep inside a Saturday
    /// reaches Monday's session sooner than Friday's and Monday is two market dates away.
    /// </para>
    /// <para>
    /// <b>Each direction closes as soon as its own lower bound catches the best held.</b> A session on a
    /// trade date before the window ends no later than that trade date's close, so it cannot need less than
    /// <c>window.Start - close</c> of widening; one after starts no earlier than the <i>preceding</i> trade
    /// date's close, so it cannot need less than <c>previousClose - window.End</c>. The forward floor is
    /// anchored on that close rather than on the trade date's own open, which sits one maintenance window
    /// later — a weaker bound on purpose, since it can only make the walk stop late and never early. Both
    /// grow with distance, so the first date that fails ends the direction rather than being stepped over —
    /// the mistake the endpoint-only version made in miniature. The hard cap is
    /// <see cref="SessionWindows.LastClosedWalkSpanDays"/> of one, the span the closed-session walk gives
    /// itself to find a single closed session.
    /// </para>
    /// <para>
    /// <b>Ties keep the earlier date, stated rather than inherited.</b> The walk runs outward from the
    /// middle, so it has no ascending order to lean on the way the endpoint pair did. Trade dates are the
    /// full 24-hour kind, which correctly folds an evening leg into the date it belongs to rather than the
    /// plain calendar date. The <b>bounds-less arm survives</b>, shrunk to what it always should have been:
    /// a window with no whole session within that cap — a closure longer than the walk, or a session
    /// definition this calendar disowns — where the message names no session and says only to widen.
    /// </para>
    /// <para>
    /// <b>The nearest whole session's bounds are reported in UTC</b>, via <c>ToUniversalTime</c>, the same
    /// normalisation <see cref="SessionWindowPlan"/> applies to the window itself:
    /// <see cref="SessionWindows.WindowFor"/> hands back the market's own offset, and a message that quoted
    /// the window in UTC beside a session in Central would read as two different clocks rather than one
    /// instant seen twice.
    /// </para>
    /// </remarks>
    private static string NoWholeSessionMessage(
        BarRange window, SessionDefinition definition, BarSessionCalendar calendar)
    {
        string message =
            "That window " + window.Start.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
            + " .. " + window.End.ToString("O", System.Globalization.CultureInfo.InvariantCulture)
            + " names no whole " + definition.Name + " session — no " + definition.Name
            + " session both opens and closes inside it — and a session bar built from part of one is a "
            + "wrong number wearing an ordinary face.";

        // Scoring the two trade dates the window's ENDPOINTS resolve to was the earlier defect, and a quiet
        // one: TradeDateFor answers null in the maintenance hour, at a weekend and on a holiday, so an
        // endpoint landing in a gap dropped the session on the FAR SIDE of that gap out of the running --
        // usually the nearest one there was (gh#568).
        //
        // So the walk starts on the window's own market dates and widens a day at a time, and each direction
        // closes as soon as nothing further out could beat what is already held. A session on a trade date
        // BEFORE the window ends no later than that date's close, so it cannot need less than
        // (window.Start - close) of widening; one AFTER starts no earlier than the PRECEDING date's close --
        // one maintenance window before its own open, so a weaker floor than the tightest available, which
        // can only stop the walk late and never early -- so it cannot need less than
        // (previousClose - window.End). Both bounds grow with distance, which is what makes the first
        // failure final rather than a gap to step over. The cap is the span the closed-session
        // walk gives itself to find ONE closed session: a venue shut for longer than that has no session
        // worth calling nearest, and the bounds-less arm answers instead.
        DateOnly earliest = MarketClock.MarketDate(window.Start);
        DateOnly latest = MarketClock.MarketDate(window.End);

        (DateOnly Date, BarRange Session)? nearest = null;
        TimeSpan leastWidening = TimeSpan.MaxValue;

        void Consider(DateOnly candidate)
        {
            if (SessionWindows.WindowFor(calendar, definition, candidate) is not { } session)
            {
                return;
            }

            TimeSpan widening =
                (session.Start < window.Start ? window.Start - session.Start : TimeSpan.Zero)
                + (session.End > window.End ? session.End - window.End : TimeSpan.Zero);

            // Ties keep the EARLIER date, said outright rather than left to the order candidates arrive in:
            // this walk runs outward from the middle, so ascending order is the one thing it does not have.
            if (widening < leastWidening
                || (widening == leastWidening && nearest is { } held && candidate < held.Date))
            {
                nearest = (candidate, session);
                leastWidening = widening;
            }
        }

        bool Settled(TimeSpan floor) => nearest is not null && floor >= leastWidening;

        for (DateOnly candidate = earliest; candidate <= latest; candidate = candidate.AddDays(1))
        {
            Consider(candidate);
        }

        bool scanBack = true;
        bool scanForward = true;
        int cap = SessionWindows.LastClosedWalkSpanDays(1);
        for (int step = 1; step <= cap && (scanBack || scanForward); step++)
        {
            scanBack = scanBack && earliest.DayNumber - step >= DateOnly.MinValue.DayNumber;
            if (scanBack)
            {
                DateOnly back = earliest.AddDays(-step);
                scanBack = !Settled(window.Start - MarketClock.FromMarket(back, calendar.SessionClose));
                if (scanBack)
                {
                    Consider(back);
                }
            }

            scanForward = scanForward && latest.DayNumber + step <= DateOnly.MaxValue.DayNumber;
            if (scanForward)
            {
                DateOnly forward = latest.AddDays(step);
                scanForward = !Settled(
                    MarketClock.FromMarket(forward.AddDays(-1), calendar.SessionClose) - window.End);
                if (scanForward)
                {
                    Consider(forward);
                }
            }
        }

        return nearest is { } whole
            ? message + " The nearest whole " + definition.Name + " session is "
                + whole.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
                + "'s, " + whole.Session.Start.ToUniversalTime()
                    .ToString("O", System.Globalization.CultureInfo.InvariantCulture)
                + " to " + whole.Session.End.ToUniversalTime()
                    .ToString("O", System.Globalization.CultureInfo.InvariantCulture)
                + ". Widen the window to include it."
            : message + " Widen the window to include a whole session.";
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
    /// <see cref="CalendarHorizon"/>, the calendar carries fewer closed sessions than the count inside the
    /// bounded walk, or the sessions it names span more base buckets than
    /// <see cref="BarGapDetector.MaxBucketsPerPass"/>.
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
    /// <para>
    /// <b>The row cap is not the only cap a count is under, and reading it as though it were is the hole the
    /// last step closes.</b> <c>SessionBarService</c> answers a count with ONE covering base read — the first
    /// session's open to the last one's close — so a count <see cref="MaxRows"/> admits can still span more
    /// base buckets than a single gap-detection pass enumerates. Around 3,720 <c>rth</c> sessions is where
    /// that starts at a 30-minute base, well inside the default 5,000, and the fault landed inside
    /// <see cref="BarGapDetector.ExpectedBuckets"/> <i>after</i> the store had been opened. Measured here so
    /// every refusal on this surface still fires before the read.
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

        IReadOnlyList<DateOnly> tradeDates;
        try
        {
            tradeDates = SessionWindows.LastClosedTradeDates(calendar, definition, now, wanted);
        }
        catch (ArgumentOutOfRangeException ex) when (ex.ParamName == "count")
        {
            // The message names the CALENDAR as the cause, not the walk. The server does walk every one of
            // those days; what runs out is the sessions inside them, and a refusal reading "this server only
            // walks back N days" sends a reader to widen a bound that is not the one that bit. How many it
            // did find is the other half of the story and is NOT stated: the Domain reports it only inside
            // the exception message, which is free text this surface does not repeat (ADR-0008).
            //
            // The span is READ BACK from the Domain rather than restated, so the number in this sentence is
            // the number the walk actually used.
            throw new McpException(
                "count " + wanted.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " asks for more closed " + definition.Name
                + " sessions than the calendar holds in the "
                + SessionWindows.LastClosedWalkSpanDays(wanted)
                    .ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " calendar days this server walks back over. Ask for fewer.");
        }

        // THE BASE-BUCKET CAP, which the count form had no way of reaching before. It is measured over the
        // window the service will actually read -- the first session's open to the last one's close, the
        // overnight between them included -- because that is one covering read and one gap-detection pass.
        // The remedy is the only lever this caller has: there is no window to narrow and no resolution
        // argument, the base resolution belonging to the session definition.
        ValidateBucketSpan(
            CoveringWindow(calendar, definition, tradeDates),
            definition.BaseResolutionMinutes,
            "count " + wanted.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " " + definition.Name + " sessions",
            "Ask for fewer sessions.");

        return tradeDates;
    }

    /// <summary>The single window that covers a run of trade dates — first open to last close.</summary>
    /// <param name="calendar">The calendar the sessions are stated against.</param>
    /// <param name="definition">The session.</param>
    /// <param name="tradeDates">The trade dates, ascending and non-empty.</param>
    /// <returns>The covering window, in UTC.</returns>
    /// <exception cref="InvalidOperationException">
    /// The calendar carries no session on a date it has just handed back.
    /// </exception>
    private static BarRange CoveringWindow(
        BarSessionCalendar calendar, SessionDefinition definition, IReadOnlyList<DateOnly> tradeDates)
    {
        BarRange first = WindowOrFault(calendar, definition, tradeDates[0]);
        BarRange last = WindowOrFault(calendar, definition, tradeDates[^1]);

        return new BarRange(first.Start.ToUniversalTime(), last.End.ToUniversalTime());
    }

    /// <summary>One trade date's session window, or a fault naming the date the calendar disowned.</summary>
    /// <param name="calendar">The calendar.</param>
    /// <param name="definition">The session.</param>
    /// <param name="tradeDate">The trade date.</param>
    /// <returns>The window.</returns>
    /// <exception cref="InvalidOperationException">The calendar carries no such session.</exception>
    /// <remarks>
    /// Unreachable by construction — the dates come from a walk over this same calendar, which only yields a
    /// date it found a window for. It FAULTS rather than refusing, because an inconsistency inside this
    /// server is not a mistake a caller can act on, and an <see cref="McpException"/> would tell them it was.
    /// </remarks>
    private static BarRange WindowOrFault(
        BarSessionCalendar calendar, SessionDefinition definition, DateOnly tradeDate) =>
        SessionWindows.WindowFor(calendar, definition, tradeDate)
        ?? throw new InvalidOperationException(
            "The calendar carries no '" + definition.Name + "' session on "
            + tradeDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
            + ", yet the closed-session walk over the same calendar has just named it.");
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
