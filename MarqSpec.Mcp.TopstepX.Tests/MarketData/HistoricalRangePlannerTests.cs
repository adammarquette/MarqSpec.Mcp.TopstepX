using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// How an outstanding range is cut into the band the venue's own pick answers for and the bands a
/// historical candidate set answers for (ADR-0020 §1–2, gh#505).
/// </summary>
/// <remarks>
/// <para>
/// The planner is <b>pure</b> — no store, no venue, no clock. That is what lets these run in the cheap tier:
/// the tenure start arrives as a value, and every candidate id arrives through a lookup delegate the caller
/// has already resolved against the venue. What is under test here is only the cutting.
/// </para>
/// <para>
/// The instants are literals rather than arithmetic over
/// <see cref="BarCacheService.PresentHorizon"/>, for the reason <c>BarCacheServiceTests.SettledNow</c>
/// states: a test derived from the constant follows the constant, and would stay green through the change it
/// exists to catch.
/// </para>
/// </remarks>
public sealed class HistoricalRangePlannerTests
{
    /// <summary>The contract the venue marks active — the present band's only candidate.</summary>
    private const string Front = "CON.F.US.MES.U26";

    /// <summary>A CME Globex session: 17:00 → 16:00 Central, no declared holidays.</summary>
    private static readonly BarSessionCalendar _calendar = BarSessionCalendar.Parse("16:00", []);

    /// <summary>March, June, September, December — the equity indices, MES among them.</summary>
    private static readonly ContractMonthCycle _quarterly = ContractMonthCycle.Parse("HMUZ");

    /// <summary>Every constructed expiry is listed, and its id is the ordinary MES one.</summary>
    private static string? Listed(ContractExpiry expiry) => "CON.F.US.MES." + expiry.Code;

    private static DateTimeOffset Utc(int year, int month, int day) =>
        new(year, month, day, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ARangeEntirelyAfterTheTenureStart_IsOnePresentSliceOnTheFront()
    {
        // The whole present band is one slice however many trade dates it spans: the candidate set does not
        // vary across it -- it is the venue's pick, by definition -- so a split there would only produce two
        // adjacent slices asking the same contract the same question.
        BarRange range = new(Utc(2026, 8, 10), Utc(2026, 8, 12));

        IReadOnlyList<RangeSlice> slices = HistoricalRangePlanner.PlanSlices(
            [range], Utc(2026, 8, 1), Front, _calendar, _quarterly, 2, Listed);

        slices.Should().ContainSingle();
        slices[0].Range.Should().Be(range);
        slices[0].Present.Should().BeTrue();
        slices[0].Candidates.Should().Equal(Front);
    }

    [Fact]
    public void ARangeStraddlingTheTenureStart_SplitsAtIt()
    {
        // The cut is what stops a warm read re-asking a second contract about a stretch the venue's own pick
        // has already answered for, and what stops the present band swallowing history the front never
        // traded. One range, both sides of it.
        DateTimeOffset tenureStart = Utc(2026, 8, 10);
        BarRange range = new(Utc(2026, 8, 5), Utc(2026, 8, 15));

        IReadOnlyList<RangeSlice> slices = HistoricalRangePlanner.PlanSlices(
            [range], tenureStart, Front, _calendar, _quarterly, 2, Listed);

        slices.Should().HaveCount(2);

        slices[0].Range.Should().Be(new BarRange(range.Start, tenureStart));
        slices[0].Present.Should().BeFalse();

        // August trades the September contract and the December one behind it: HMUZ, at or after month 8.
        slices[0].Candidates.Should().Equal("CON.F.US.MES.U26", "CON.F.US.MES.Z26");

        slices[1].Range.Should().Be(new BarRange(tenureStart, range.End));
        slices[1].Present.Should().BeTrue();
        slices[1].Candidates.Should().Equal(Front);
    }

    [Fact]
    public void AHistoricalRange_SplitsWhereTheCandidateSetChanges()
    {
        // MES is quarterly, depth two. March's candidates are H26 and M26; April's are M26 and U26 -- so a
        // range spanning the turn cannot be fetched from one candidate set, and asking March's set for
        // April's bars is exactly the thin-series defect ADR-0020 exists to fix.
        //
        // The cut is at the instant the trade date first becomes April's, which is the evening session open
        // on 31 March -- NOT midnight, and not the calendar month boundary. A bar opening at 17:30 Central on
        // 31 March belongs to April's session, and the contract that traded it is April's.
        DateTimeOffset turn =
            MarketClock.FromMarket(new DateOnly(2026, 3, 31), new TimeOnly(17, 0)).ToUniversalTime();
        BarRange range = new(Utc(2026, 3, 20), Utc(2026, 4, 5));

        IReadOnlyList<RangeSlice> slices = HistoricalRangePlanner.PlanSlices(
            [range], Utc(2026, 6, 1), Front, _calendar, _quarterly, 2, Listed);

        slices.Should().HaveCount(2);
        slices.Should().OnlyContain(slice => !slice.Present);

        slices[0].Range.Should().Be(new BarRange(range.Start, turn));
        slices[0].Candidates.Should().Equal("CON.F.US.MES.H26", "CON.F.US.MES.M26");

        slices[1].Range.Should().Be(new BarRange(turn, range.End));
        slices[1].Candidates.Should().Equal("CON.F.US.MES.M26", "CON.F.US.MES.U26");
    }

    [Fact]
    public void AHistoricalSliceWithNoListedCandidate_FallsBackToTheFront()
    {
        // Degradation is today's behaviour, loudly -- never a slice with nothing to ask. A constructed expiry
        // the venue does not list is dropped, and when the drop empties the set the slice is fetched from the
        // venue's own pick exactly as it is today. The caller logs the warning and withholds the permanent
        // memo; the planner's part is only to leave a set that can be fetched.
        BarRange range = new(Utc(2026, 3, 20), Utc(2026, 3, 25));

        IReadOnlyList<RangeSlice> slices = HistoricalRangePlanner.PlanSlices(
            [range], Utc(2026, 6, 1), Front, _calendar, _quarterly, 2, static _ => null);

        slices.Should().ContainSingle();
        slices[0].Range.Should().Be(range);
        slices[0].Candidates.Should().Equal(Front);

        // Still historical. The fallback names what to ask, not which band the slice is in -- a present slice
        // is one the store's trailing run of the front already vouches for, and this one is not.
        slices[0].Present.Should().BeFalse();
    }

    [Fact]
    public void AHistoricalSliceWhoseOnlySurvivorIsTheFront_IsNotACycleFrontSlice()
    {
        // THE WRONG-LOOKS-RIGHT CASE (gh#570). An August trade date names U26 and Z26 on HMUZ at depth two.
        // A venue that lists only U26 -- one ContractDirectory negative on Z26, one hiccup, one hour -- leaves
        // a candidate set of exactly the front, which by the SET ALONE is indistinguishable from a cycle that
        // named the front and nothing else. Nothing throws, nothing comes back empty, and the caller folds
        // the slice into the present band and memoises it: the pre-ADR-0020 series for that stretch, served
        // as an ordinary answer.
        //
        // The two are different facts. One survived a check the other never had to take, so the planner has
        // to record WHICH expiries did not resolve rather than leaving the caller to infer it from a set
        // that cannot express it.
        BarRange range = new(Utc(2026, 8, 5), Utc(2026, 8, 8));

        IReadOnlyList<RangeSlice> narrowed = HistoricalRangePlanner.PlanSlices(
            [range],
            Utc(2026, 8, 10),
            Front,
            _calendar,
            _quarterly,
            2,
            expiry => string.Equals(expiry.Code, "U26", StringComparison.Ordinal) ? Front : null);

        narrowed.Should().ContainSingle();
        narrowed[0].Candidates.Should().Equal(Front);
        narrowed[0].FellBackToFront.Should().BeFalse(
            "U26 did survive the check -- this is a narrowed set, not an empty one, and conflating the two "
            + "would put every one-listed-candidate range back on the venue for ever");
        narrowed[0].Unresolved.Select(expiry => expiry.Code).Should().Equal(
            new[] { "Z26" },
            "the caller warns with the expiries that did not resolve, and cannot name them if the planner "
            + "drops them silently");
        narrowed[0].IsCycleFrontSlice(Front).Should().BeFalse(
            "the front is this slice's only candidate because the venue lists no Z26, so it must not be "
            + "merged into the present band");

        // THE CONTRAST, and the reason the distinction cannot be read off the candidate set. At depth one an
        // August trade date names U26 alone, everything the cycle named resolved, and the slice is an
        // ordinary one-candidate answer -- mergeable, memoisable, and rightly silent.
        IReadOnlyList<RangeSlice> named = HistoricalRangePlanner.PlanSlices(
            [range], Utc(2026, 8, 10), Front, _calendar, _quarterly, 1, Listed);

        named.Should().ContainSingle();
        named[0].Candidates.Should().Equal(Front);
        named[0].Unresolved.Should().BeEmpty();
        named[0].IsCycleFrontSlice(Front).Should().BeTrue();
    }

    [Fact]
    public void ASliceWithNothingLeftToAsk_NamesEveryExpiryThatDidNotResolve()
    {
        // The fallback and the narrowing are the same fact at two strengths, so they are recorded the same
        // way: FellBackToFront says the set emptied, Unresolved says WHAT it lost. A fallback that named
        // nothing would make the loud half of ADR-0020's degradation rule loud about a range and silent
        // about the contracts, which is the half an operator actually acts on.
        BarRange range = new(Utc(2026, 3, 20), Utc(2026, 3, 25));

        IReadOnlyList<RangeSlice> slices = HistoricalRangePlanner.PlanSlices(
            [range], Utc(2026, 6, 1), Front, _calendar, _quarterly, 2, static _ => null);

        slices.Should().ContainSingle();
        slices[0].FellBackToFront.Should().BeTrue();
        slices[0].Unresolved.Select(expiry => expiry.Code).Should().Equal("H26", "M26");
        slices[0].IsCycleFrontSlice(Front).Should().BeFalse();
    }

    [Fact]
    public void APresentSlice_NamesNothingUnresolved()
    {
        // The present band never constructs a candidate, so it can never have dropped one. Recorded here
        // because a non-empty Unresolved on a present slice would be read by the caller as a degradation
        // and would cost a warning on the hottest read this server serves.
        IReadOnlyList<RangeSlice> slices = HistoricalRangePlanner.PlanSlices(
            [new BarRange(Utc(2026, 8, 10), Utc(2026, 8, 12))],
            Utc(2026, 8, 1),
            Front,
            _calendar,
            _quarterly,
            2,
            static _ => null);

        slices.Should().ContainSingle();
        slices[0].Present.Should().BeTrue();
        slices[0].Unresolved.Should().BeEmpty();
        slices[0].IsCycleFrontSlice(Front).Should().BeTrue();
    }

    [Fact]
    public void TheSlicesCoverTheInputExactly_AndAreAscending()
    {
        // The planner sits between the gap detector and the fetch, so anything it loses is a bucket nobody
        // ever asks for again -- a permanent hole that reads back as a quiet market -- and anything it
        // duplicates is a bucket fetched twice and a venue request nobody accounted for. Cover exactly.
        BarRange[] ranges =
        [
            new(Utc(2026, 3, 20), Utc(2026, 4, 5)),   // crosses the March -> April candidate turn
            new(Utc(2026, 4, 20), Utc(2026, 5, 20)),  // crosses a MONTH boundary that is not a candidate turn
            new(Utc(2026, 8, 5), Utc(2026, 8, 15)),   // straddles the tenure start
        ];

        IReadOnlyList<RangeSlice> slices = HistoricalRangePlanner.PlanSlices(
            ranges, Utc(2026, 8, 10), Front, _calendar, _quarterly, 2, Listed);

        // April and May name the same two candidates on a quarterly cycle, so the second range is ONE slice.
        // Splitting on the calendar month rather than on the candidate set would make it two, and each half
        // would ask the same contracts the same question.
        slices.Should().HaveCount(5);
        slices.Should().OnlyContain(slice => !slice.Range.IsEmpty);

        for (int i = 1; i < slices.Count; i++)
        {
            slices[i].Range.Start.Should().BeOnOrAfter(slices[i - 1].Range.End);
        }

        // Contiguous slices reassemble into exactly the ranges handed in -- no bucket dropped, none doubled.
        List<BarRange> merged = [];
        foreach (RangeSlice slice in slices)
        {
            if (merged.Count > 0 && merged[^1].End == slice.Range.Start)
            {
                merged[^1] = new BarRange(merged[^1].Start, slice.Range.End);
            }
            else
            {
                merged.Add(slice.Range);
            }
        }

        merged.Should().Equal(ranges);
    }
}
