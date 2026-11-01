namespace MarqSpec.Mcp.TopstepX.Venue;

/// <summary>
/// Holds this server inside the gateway's documented request rate (gh#43).
/// </summary>
/// <remarks>
/// <para>
/// ProjectX publishes two limits: <b>50 requests / 30 seconds</b> for
/// <c>POST /api/History/retrieveBars</c>, and <b>200 requests / 60 seconds</b> for every other endpoint. A
/// breach comes back as an HTTP <c>429</c>. The history limit is both the tighter of the two and the one
/// covering the only call this repository ever issues in a burst — the bar paging loop.
/// </para>
/// <para>
/// <b>Why this is not left to the client's retry.</b> The vendor client retries a <c>429</c>, which recovers
/// from a breach after provoking one. 50 requests in 30 seconds is a mean spacing of <b>600 ms</b>, and a
/// paging loop that issues the next page the moment the last one lands is spaced by nothing but the vendor's
/// own latency. Relying on a REST round-trip to exceed 600 ms is relying on the vendor being slow.
/// </para>
/// <para>
/// <b>The window is treated as sliding.</b> The vendor's page does not say whether its window is fixed or
/// sliding. A schedule that never exceeds the cap in any <i>sliding</i> window cannot exceed it in a fixed
/// one either, so the stricter reading is the one that is safe to implement against. Modelling the real
/// shape rather than flattening it to a fixed delay also means pacing costs <b>nothing</b> until a burst is
/// actually near the cap, which is where all ordinary traffic sits.
/// </para>
/// <para>
/// <b>Lifetime matters.</b> The limit belongs to the credential, not to a request scope, so this must be a
/// <b>singleton</b> even though <see cref="ProjectXMarketDataGateway"/> is scoped — two concurrent tool calls
/// share one allowance. A per-gateway pacer would let N concurrent scopes each burst to the cap.
/// </para>
/// <para>
/// Recorded on the wiki page rather than only here:
/// <c>documentation/wiki/pages/projectx-gateway-api.md</c>.
/// </para>
/// </remarks>
public sealed class VenueRequestPacer
{
    /// <summary>Requests the vendor allows against <c>History/retrieveBars</c> per <see cref="HistoryWindow"/>.</summary>
    public const int HistoryRequestsPerWindow = 50;

    /// <summary>
    /// How many of those history requests a backfill may take in one <see cref="HistoryWindow"/>.
    /// The rest stay available for a live read (gh#899).
    /// </summary>
    public const int BackfillRequestsPerWindow = 20;

    /// <summary>The window <see cref="HistoryRequestsPerWindow"/> is counted over.</summary>
    public static readonly TimeSpan HistoryWindow = TimeSpan.FromSeconds(30);

    /// <summary>How far past the window boundary a released request is scheduled.</summary>
    /// <remarks>
    /// <para>
    /// Scheduling the released request at <i>exactly</i> <c>oldest + window</c> is legal only if the vendor's
    /// window is half-open. If it is closed at both ends, that instant holds one request too many and returns
    /// the 429 this type exists to avoid. The vendor does not say which it is — the same unknown as
    /// fixed-versus-sliding, and it gets the same answer: take the stricter reading, because being wrong
    /// costs a quarter of a second and being right the other way costs the breach.
    /// </para>
    /// <para>
    /// It also absorbs timer granularity, which fires "about then" rather than "at or after". It does
    /// <b>not</b> absorb clock skew against the vendor's own counter — that is unbounded and no fixed margin
    /// would cover it.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan BoundaryMargin = TimeSpan.FromMilliseconds(250);

    private readonly int _capacity;
    private readonly TimeSpan _window;
    private readonly TimeProvider _clock;
    private readonly Lock _sync = new();

    /// <summary>
    /// Reservations still inside the window, oldest first. Live and released backfill share it, so the
    /// two together cannot pass <see cref="Capacity"/>. A row older than the window is removed before
    /// every decision.
    /// </summary>
    private readonly Queue<HistoryReservation> _reserved = new();

    /// <summary>Live reads blocked in <see cref="WaitForSlotAsync"/>.</summary>
    private int _liveWaiters;

    /// <summary>
    /// Completed when the last waiting live read leaves. Backfill waits on this instead of polling.
    /// </summary>
    private TaskCompletionSource _liveReleased =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Creates a pacer.</summary>
    /// <param name="capacity">The most requests allowed in any <paramref name="window"/>.</param>
    /// <param name="window">The window the cap is counted over.</param>
    /// <param name="clock">The clock. Injected so a test can drive a burst without waiting for one.</param>
    /// <exception cref="ArgumentOutOfRangeException">The capacity or the window is not positive.</exception>
    public VenueRequestPacer(int capacity, TimeSpan window, TimeProvider clock)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(window, TimeSpan.Zero);
        ArgumentNullException.ThrowIfNull(clock);

        _capacity = capacity;
        _window = window;
        _clock = clock;
    }

    /// <summary>The most requests allowed in any <see cref="Window"/>.</summary>
    public int Capacity => _capacity;

    /// <summary>The window <see cref="Capacity"/> is counted over.</summary>
    public TimeSpan Window => _window;

    /// <summary>How far past the boundary a released request is scheduled. See <see cref="BoundaryMargin"/>.</summary>
    public TimeSpan Margin => BoundaryMargin;

    /// <summary>A pacer carrying the vendor's documented <c>History/retrieveBars</c> limit.</summary>
    /// <param name="clock">The clock.</param>
    /// <returns>A pacer of 50 requests per 30 seconds.</returns>
    public static VenueRequestPacer ForHistory(TimeProvider clock) =>
        new(HistoryRequestsPerWindow, HistoryWindow, clock);

    /// <summary>
    /// Takes the next slot, returning the instant at which the request may be issued.
    /// </summary>
    /// <returns>Now when there is room, otherwise the instant the oldest reservation leaves the window.</returns>
    /// <remarks>
    /// The decision is separated from the sleep on purpose: a test can then read the whole schedule of a
    /// hundred-page burst exactly, instead of inferring it from how long something took.
    /// <para>
    /// A slot is consumed even if the caller never issues the request. That errs toward <i>fewer</i> venue
    /// requests, which is the safe direction for this to be wrong in.
    /// </para>
    /// </remarks>
    public DateTimeOffset ReserveSlot()
    {
        lock (_sync)
        {
            return ReserveLiveUnlocked(armWaiter: false);
        }
    }

    /// <summary>
    /// Takes the next slot and waits for it.
    /// </summary>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>
    /// How long the caller was held. <see cref="TimeSpan.Zero"/> means the request went straight out — which
    /// is what the caller uses to decide whether there is anything worth telling an operator about.
    /// </returns>
    /// <remarks>
    /// Completes synchronously while there is room, so the ordinary one- or two-page read pays nothing at
    /// all for this. The delay is returned rather than logged here because this type has no logger and
    /// should not grow one: it is a clock and a queue, and the call site knows what the request was for.
    /// </remarks>
    public async ValueTask<TimeSpan> WaitForSlotAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset at;
        lock (_sync)
        {
            // The waiter flag is taken in the same lock as the reservation, so a backfill grant
            // cannot land between the booking and the wait.
            at = ReserveLiveUnlocked(armWaiter: true);
        }

        TimeSpan wait = at - _clock.GetUtcNow();
        if (wait <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        try
        {
            await Task.Delay(wait, _clock, cancellationToken).ConfigureAwait(false);
            return wait;
        }
        finally
        {
            ReleaseLiveWaiter();
        }
    }

    /// <summary>
    /// Takes a backfill slot. Nothing is booked while a live read is waiting.
    /// </summary>
    /// <returns>
    /// Now when fewer than <see cref="BackfillRequestsPerWindow"/> backfill rows are still in the
    /// window, the shared window is not full, and no live read is waiting. Otherwise the later release,
    /// and that future time is not inserted.
    /// </returns>
    public DateTimeOffset ReserveBackfillSlot()
    {
        lock (_sync)
        {
            return PlanBackfillUnlocked().At;
        }
    }

    /// <summary>
    /// Takes a backfill slot and waits for it. Time spent only yielding to a live read is not included
    /// in the returned delay, so the gateway's paced total stays a rate-window figure.
    /// </summary>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>How long the caller was held for the rate window.</returns>
    public async ValueTask<TimeSpan> WaitForBackfillSlotAsync(CancellationToken cancellationToken)
    {
        TimeSpan paced = TimeSpan.Zero;
        while (true)
        {
            BackfillPlan plan;
            Task liveReleased;
            lock (_sync)
            {
                plan = PlanBackfillUnlocked();
                liveReleased = _liveReleased.Task;
            }

            TimeSpan wait = plan.At - _clock.GetUtcNow();
            if (wait <= TimeSpan.Zero)
            {
                return paced;
            }

            if (plan.Yielded)
            {
                Task delay = Task.Delay(wait, _clock, cancellationToken);
                Task finished = await Task.WhenAny(delay, liveReleased).ConfigureAwait(false);
                if (finished == delay)
                {
                    await delay.ConfigureAwait(false);
                }
                else if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                continue;
            }

            await Task.Delay(wait, _clock, cancellationToken).ConfigureAwait(false);
            paced += wait;
        }
    }

    private DateTimeOffset ReserveLiveUnlocked(bool armWaiter)
    {
        DateTimeOffset now = _clock.GetUtcNow();
        Trim(now);
        DateTimeOffset at;
        if (_reserved.Count < _capacity)
        {
            at = now;
        }
        else
        {
            // Full: this request may go once the oldest reservation still in the window, live or
            // backfill, has aged out. Sliding, not a fresh allowance at a window boundary.
            //
            // Plus BoundaryMargin, so the released request lands just PAST the boundary rather than
            // on it. Exactly on it is legal only if the vendor's window is half-open.
            HistoryReservation oldest = _reserved.Dequeue();
            at = oldest.At + _window + BoundaryMargin;
            if (at < now)
            {
                at = now;
            }
        }

        _reserved.Enqueue(new HistoryReservation(false, at));
        if (armWaiter && at > now)
        {
            _liveWaiters++;
        }

        return at;
    }

    private BackfillPlan PlanBackfillUnlocked()
    {
        DateTimeOffset now = _clock.GetUtcNow();
        Trim(now);
        if (_liveWaiters > 0)
        {
            return new BackfillPlan(LiveRelease(now), Yielded: true);
        }

        DateTimeOffset backfillAt = BackfillReady(now);
        DateTimeOffset sharedAt = SharedReady(now);
        DateTimeOffset at = sharedAt > backfillAt ? sharedAt : backfillAt;
        if (at > now)
        {
            return new BackfillPlan(at, Yielded: false);
        }

        _reserved.Enqueue(new HistoryReservation(true, at));
        return new BackfillPlan(at, Yielded: false);
    }

    private DateTimeOffset BackfillReady(DateTimeOffset now)
    {
        int count = 0;
        DateTimeOffset oldest = now;
        bool any = false;
        foreach (HistoryReservation slot in _reserved)
        {
            if (!slot.Backfill)
            {
                continue;
            }

            count++;
            if (!any || slot.At < oldest)
            {
                oldest = slot.At;
                any = true;
            }
        }

        if (count < BackfillRequestsPerWindow)
        {
            return now;
        }

        DateTimeOffset ready = oldest + _window + BoundaryMargin;
        return ready > now ? ready : now;
    }

    private DateTimeOffset SharedReady(DateTimeOffset now)
    {
        if (_reserved.Count < _capacity)
        {
            return now;
        }

        DateTimeOffset ready = _reserved.Peek().At + _window + BoundaryMargin;
        return ready > now ? ready : now;
    }

    private DateTimeOffset LiveRelease(DateTimeOffset now)
    {
        DateTimeOffset wake = now + BoundaryMargin;
        foreach (HistoryReservation slot in _reserved)
        {
            if (!slot.Backfill && slot.At > wake)
            {
                wake = slot.At;
            }
        }

        return wake;
    }

    /// <summary>Drops rows whose window has already closed. The queue is oldest first.</summary>
    private void Trim(DateTimeOffset now)
    {
        while (_reserved.Count > 0
            && _reserved.Peek().At + _window + BoundaryMargin <= now)
        {
            _reserved.Dequeue();
        }
    }

    private void ReleaseLiveWaiter()
    {
        TaskCompletionSource released;
        lock (_sync)
        {
            if (_liveWaiters > 0)
            {
                _liveWaiters--;
            }

            if (_liveWaiters > 0)
            {
                return;
            }

            released = _liveReleased;
            _liveReleased = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        released.TrySetResult();
    }

    private readonly record struct BackfillPlan(DateTimeOffset At, bool Yielded);

    private sealed class HistoryReservation(bool backfill, DateTimeOffset at)
    {
        public bool Backfill { get; } = backfill;

        public DateTimeOffset At { get; } = at;
    }
}
