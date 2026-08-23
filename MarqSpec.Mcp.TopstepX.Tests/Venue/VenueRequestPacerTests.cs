using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.Tests.Venue;

/// <summary>
/// Staying inside the gateway's documented request rate (gh#43).
/// </summary>
/// <remarks>
/// <para>
/// ProjectX documents <b>50 requests / 30 seconds</b> for <c>POST /api/History/retrieveBars</c> and
/// <b>200 requests / 60 seconds</b> for everything else, and reports a breach as an HTTP <c>429</c>.
/// The bar paging loop is the only place in this repository that issues requests in a burst, and it issued
/// them as fast as they completed — 50 in 30 seconds means a mean spacing of <b>600 ms</b>, which no
/// round-trip to a REST endpoint is going to guarantee on its own.
/// </para>
/// <para>
/// The window is modelled as <b>sliding</b> rather than fixed. The vendor does not say which it is, and a
/// schedule that never exceeds the cap in any <i>sliding</i> window cannot exceed it in a fixed one either —
/// so the stricter reading is the safe one to implement against.
/// </para>
/// </remarks>
public sealed class VenueRequestPacerTests
{
    private static readonly DateTimeOffset _start = new(2026, 8, 23, 14, 0, 0, TimeSpan.Zero);

    // ── The documented numbers, pinned in code ───────────────────────────────────────────────────────

    [Fact]
    public void TheHistoryPacerCarriesTheVendorsDocumentedLimit()
    {
        // If this fails, either the vendor changed the limit or somebody "tuned" it. Both need the wiki
        // page updated in the same change -- documentation/wiki/pages/projectx-gateway-api.md.
        VenueRequestPacer pacer = VenueRequestPacer.ForHistory(new FakeTimeProvider(_start));

        pacer.Capacity.Should().Be(50);
        pacer.Window.Should().Be(TimeSpan.FromSeconds(30));
    }

    // ── The shape of the limiter ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void RequestsUpToTheCapAreNotDelayedAtAll()
    {
        // Pacing must cost NOTHING on the ordinary path. A tool call that fetches one or two pages is the
        // overwhelming majority of traffic, and a fixed inter-request delay would tax every one of them for
        // a limit they were never near.
        FakeTimeProvider clock = new(_start);
        VenueRequestPacer pacer = new(3, TimeSpan.FromSeconds(30), clock);

        for (int i = 0; i < 3; i++)
        {
            pacer.ReserveSlot().Should().Be(_start);
        }
    }

    [Fact]
    public void TheRequestThatWouldBreachIsPushedToWhereTheWindowRolls()
    {
        FakeTimeProvider clock = new(_start);
        VenueRequestPacer pacer = new(3, TimeSpan.FromSeconds(30), clock);

        for (int i = 0; i < 3; i++)
        {
            pacer.ReserveSlot();
        }

        pacer.ReserveSlot().Should().Be(_start + TimeSpan.FromSeconds(30) + pacer.Margin);
    }

    [Fact]
    public void TheReleasedRequestLandsPastTheBoundaryRatherThanOnIt()
    {
        // Exactly `oldest + window` is legal only if the vendor's window is half-open. Closed at both ends,
        // that instant holds one request too many. The vendor does not say which it is -- the same unknown
        // as fixed-versus-sliding, and it gets the same answer: take the stricter reading.
        FakeTimeProvider clock = new(_start);
        VenueRequestPacer pacer = new(1, TimeSpan.FromSeconds(30), clock);

        pacer.ReserveSlot().Should().Be(_start);

        pacer.Margin.Should().BeGreaterThan(TimeSpan.Zero);
        pacer.ReserveSlot().Should().BeAfter(_start + TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void TheWindowSlidesRatherThanResetting()
    {
        // A fixed-window limiter would hand out a fresh full allowance the moment the window ticked over,
        // which permits 2x the cap across the boundary. This one lets exactly one request through for each
        // one that ages out.
        FakeTimeProvider clock = new(_start);
        VenueRequestPacer pacer = new(3, TimeSpan.FromSeconds(30), clock);

        pacer.ReserveSlot().Should().Be(_start);
        clock.Advance(TimeSpan.FromSeconds(10));
        pacer.ReserveSlot().Should().Be(_start + TimeSpan.FromSeconds(10));
        pacer.ReserveSlot().Should().Be(_start + TimeSpan.FromSeconds(10));

        // Full. The next one waits for the FIRST reservation to leave the window, not for a new window.
        pacer.ReserveSlot().Should().Be(_start + TimeSpan.FromSeconds(30) + pacer.Margin);

        // And the one after that waits for the second, which was taken ten seconds later.
        pacer.ReserveSlot().Should().Be(_start + TimeSpan.FromSeconds(40) + pacer.Margin);
    }

    [Fact]
    public void ASlotAlreadyInThePastIsTakenImmediately()
    {
        FakeTimeProvider clock = new(_start);
        VenueRequestPacer pacer = new(2, TimeSpan.FromSeconds(30), clock);

        pacer.ReserveSlot();
        pacer.ReserveSlot();

        // Long enough that both earlier reservations have aged out. The cap must not carry a debt forward.
        clock.Advance(TimeSpan.FromMinutes(5));

        pacer.ReserveSlot().Should().Be(clock.GetUtcNow());
        pacer.ReserveSlot().Should().Be(clock.GetUtcNow());
    }

    // ── The scenario the issue names ─────────────────────────────────────────────────────────────────

    [Fact]
    public void AColdFiveMinuteYearNeverExceedsTheCapInAnyWindow()
    {
        // 365 days = 525,600 minutes. A page is 1000 bars x 5 minutes = 5,000 minutes, so a cold year is
        // 106 back-to-back POST /api/History/retrieveBars calls -- the burst this pacer exists for.
        //
        // The clock never advances here, which is the WORST case: it models pages returning instantly. Real
        // round-trips only spread the burst out further, so the schedule below is an upper bound on how
        // tightly the requests can land.
        const int pages = 106;
        FakeTimeProvider clock = new(_start);
        VenueRequestPacer pacer = VenueRequestPacer.ForHistory(clock);

        List<DateTimeOffset> schedule = [.. Enumerable.Range(0, pages).Select(_ => pacer.ReserveSlot())];

        foreach (DateTimeOffset slot in schedule)
        {
            schedule.Count(s => s >= slot && s < slot + pacer.Window)
                .Should().BeLessThanOrEqualTo(pacer.Capacity);
        }

        // Three bursts of fifty, so the whole year costs sixty seconds of pacing and no more -- plus two
        // boundary margins, which is the half-second the stricter reading of the window costs. Unpaced, the
        // 51st request would have gone out inside the first window and earned a 429.
        (schedule[^1] - schedule[0])
            .Should().Be(TimeSpan.FromSeconds(60) + (2 * pacer.Margin));
    }

    [Fact]
    public void TheAllowanceHoldsWhenThreadsRaceForIt()
    {
        // Concurrency is the ENTIRE justification for the singleton lifetime (CompositionRootTests pins that
        // two scopes share one object; this pins that the shared object is right when they both reach it).
        // ReserveSlot is fully synchronous inside the lock and WaitForSlotAsync awaits outside it, so there
        // is no lock held across an await -- but that is a property worth pinning rather than inferring.
        const int reservations = 500;
        FakeTimeProvider clock = new(_start);
        VenueRequestPacer pacer = VenueRequestPacer.ForHistory(clock);

        DateTimeOffset[] slots = new DateTimeOffset[reservations];
        Parallel.For(
            0,
            reservations,
            new ParallelOptions { MaxDegreeOfParallelism = 16 },
            i => slots[i] = pacer.ReserveSlot());

        foreach (DateTimeOffset slot in slots)
        {
            slots.Count(s => s >= slot && s < slot + pacer.Window)
                .Should().BeLessThanOrEqualTo(pacer.Capacity);
        }

        // 500 reservations is ten bursts of fifty, so nine boundaries are crossed and no more. A lost update
        // under the race would show up here as a shorter span -- two threads handed the same slot.
        (slots.Max() - slots.Min())
            .Should().Be((9 * pacer.Window) + (9 * pacer.Margin));
    }

    // ── Waiting ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AWaitInsideTheCapCompletesWithoutTouchingTheClock()
    {
        FakeTimeProvider clock = new(_start);
        VenueRequestPacer pacer = new(3, TimeSpan.FromSeconds(30), clock);

        for (int i = 0; i < 3; i++)
        {
            // Zero is the signal the gateway reads to decide whether there is anything to tell an operator.
            (await pacer.WaitForSlotAsync(CancellationToken.None)).Should().Be(TimeSpan.Zero);
        }

        clock.GetUtcNow().Should().Be(_start);
    }

    [Fact]
    public async Task AWaitBeyondTheCapDoesNotCompleteUntilTheWindowRolls()
    {
        FakeTimeProvider clock = new(_start);
        VenueRequestPacer pacer = new(3, TimeSpan.FromSeconds(30), clock);

        for (int i = 0; i < 3; i++)
        {
            await pacer.WaitForSlotAsync(CancellationToken.None);
        }

        Task<TimeSpan> fourth = pacer.WaitForSlotAsync(CancellationToken.None).AsTask();
        fourth.IsCompleted.Should().BeFalse();

        // Still held ON the boundary -- the margin is the point, so releasing at exactly 30s would be wrong.
        clock.Advance(TimeSpan.FromSeconds(30));
        fourth.IsCompleted.Should().BeFalse();

        clock.Advance(pacer.Margin);
        TimeSpan waited = await fourth.WaitAsync(TimeSpan.FromSeconds(30));

        waited.Should().Be(TimeSpan.FromSeconds(30) + pacer.Margin);
    }

    [Fact]
    public async Task AWaitingRequestIsCancellable()
    {
        // The pacer sits inside a tool call. A caller that gives up must not be held by a delay this server
        // chose to add.
        FakeTimeProvider clock = new(_start);
        VenueRequestPacer pacer = new(1, TimeSpan.FromMinutes(10), clock);

        await pacer.WaitForSlotAsync(CancellationToken.None);

        using CancellationTokenSource cts = new();
        Task<TimeSpan> blocked = pacer.WaitForSlotAsync(cts.Token).AsTask();
        await cts.CancelAsync();

        Func<Task> wait = () => blocked;
        await wait.Should().ThrowAsync<OperationCanceledException>();
    }

    // ── Guards ───────────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ACapacityThatIsNotPositiveIsRefused(int capacity)
    {
        Func<VenueRequestPacer> build =
            () => new VenueRequestPacer(capacity, TimeSpan.FromSeconds(30), new FakeTimeProvider(_start));

        build.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void AWindowThatIsNotPositiveIsRefused()
    {
        Func<VenueRequestPacer> build =
            () => new VenueRequestPacer(50, TimeSpan.Zero, new FakeTimeProvider(_start));

        build.Should().Throw<ArgumentOutOfRangeException>();
    }
}
