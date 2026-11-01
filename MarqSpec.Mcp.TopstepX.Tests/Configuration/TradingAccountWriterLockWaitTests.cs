using System.Threading;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace MarqSpec.Mcp.TopstepX.Tests.Configuration;

/// <summary>The process mutex in front of the account writer lock (gh#772).</summary>
[Trait("Gate", "ADR-0024")]
public sealed class TradingAccountWriterLockWaitTests
{
    private static readonly DateTimeOffset _start = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AMutexWaitPastTheCommandTimeout_IsUnavailable()
    {
        CountingClock clock = new(_start);
        using TradingAccountWriterLock writer = new(
            "Host=127.0.0.1;Port=1;Username=x;Password=x;Database=x",
            NullLogger<TradingAccountWriterLock>.Instance,
            clock);
        await using IAsyncDisposable held = await writer.HoldProcessMutexForTestsAsync();
        Task<TradingWriterLockTake> pending = writer.TryHoldAsync(100, CancellationToken.None);
        await WaitUntilArmed(clock);

        clock.Advance(TimeSpan.FromSeconds(TradingAccountWriterLock.CommandTimeoutSeconds)
            - TimeSpan.FromMilliseconds(1));
        pending.IsCompleted.Should().BeFalse();
        clock.Advance(TimeSpan.FromMilliseconds(1));

        TradingWriterLockTake take = await pending;
        take.Should().Be(TradingWriterLockTake.Unavailable);
        held.Should().NotBeNull();
    }

    [Fact]
    public async Task ACallerCancellation_ThrowsInsteadOfRefusingTheLock()
    {
        CountingClock clock = new(_start);
        using TradingAccountWriterLock writer = new(
            "Host=127.0.0.1;Port=1;Username=x;Password=x;Database=x",
            NullLogger<TradingAccountWriterLock>.Instance,
            clock);
        await using IAsyncDisposable held = await writer.HoldProcessMutexForTestsAsync();
        using CancellationTokenSource caller = new();
        Task<TradingWriterLockTake> pending = writer.TryHoldAsync(100, caller.Token);
        await WaitUntilArmed(clock);

        caller.Cancel();

        Func<Task> wait = async () => await pending;
        await wait.Should().ThrowAsync<OperationCanceledException>();
        pending.IsCompletedSuccessfully.Should().BeFalse();
        held.Should().NotBeNull();
    }

    private static async Task WaitUntilArmed(CountingClock clock)
    {
        using CancellationTokenSource budget = new(TimeSpan.FromSeconds(5));
        while (clock.Timers < 1)
        {
            await Task.Delay(1, budget.Token);
        }
    }

    private sealed class CountingClock : FakeTimeProvider
    {
        private int _timers;

        public CountingClock(DateTimeOffset start)
            : base(start)
        {
        }

        public int Timers => Volatile.Read(ref _timers);

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            ITimer timer = base.CreateTimer(callback, state, dueTime, period);
            Interlocked.Increment(ref _timers);
            return timer;
        }
    }
}
