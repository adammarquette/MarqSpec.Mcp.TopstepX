using System.Diagnostics;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.Mcp.TopstepX.Tests.Configuration;

/// <summary>The process mutex in front of the account writer lock (gh#772).</summary>
[Trait("Gate", "ADR-0024")]
public sealed class TradingAccountWriterLockWaitTests
{
    [Fact]
    public async Task AMutexWaitPastTheCommandTimeout_IsUnavailable()
    {
        using TradingAccountWriterLock writer = new(
            "Host=127.0.0.1;Port=1;Username=x;Password=x;Database=x",
            NullLogger<TradingAccountWriterLock>.Instance);
        await using IAsyncDisposable held = await writer.HoldProcessMutexForTestsAsync();
        using CancellationTokenSource budget = new(TimeSpan.FromSeconds(5));
        Stopwatch started = Stopwatch.StartNew();
        TradingWriterLockTake take = await writer.TryHoldAsync(100, budget.Token);
        started.Stop();

        take.Should().Be(TradingWriterLockTake.Unavailable);
        started.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(1.5));
        started.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(4));
        held.Should().NotBeNull();
    }
}
