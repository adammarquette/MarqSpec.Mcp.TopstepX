using System.Runtime.CompilerServices;
using FluentAssertions;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// Awaits a <see cref="Microsoft.Extensions.Hosting.BackgroundService.ExecuteTask"/> deterministically
/// instead of polling <c>IsCompleted</c> on a wall-clock budget (gh#407).
/// </summary>
/// <remarks>
/// A hosted service's own completion is a real signal — reacting to it beats spinning a 10ms poll
/// against it. But an unbounded <c>await Task.WhenAny(executeTask)</c> is not a fix, it is a
/// different flake: if <c>ExecuteTask</c> never completes (exactly the regression a couple of these
/// fixtures pin — a "never record" path that must return promptly, not hang), the wait blocks
/// forever and the test host eventually kills the whole run with no diagnostic at all, which is
/// worse than the wall-clock flake this card exists to remove. <see cref="Task.Delay(TimeSpan)"/>
/// races it instead, and <see cref="AwaitCompletionAsync"/> asserts the real task won that race, so
/// a hang still fails — with a message, inside one test, on a real budget — rather than hanging CI.
/// One helper for every call site so the nine of them cannot drift apart on the bound.
/// </remarks>
internal static class BackgroundServiceTestSupport
{
    /// <summary>
    /// Default bound for <see cref="AwaitCompletionAsync"/> when a caller does not supply its own.
    /// </summary>
    /// <remarks>
    /// This races a real completion signal (<see cref="Task.WhenAny(Task[])"/> against
    /// <c>executeTask</c>), so it is not a poll interval and is a different quantity from
    /// <c>TradeTapeRecorderTests._multiSourcePollBudget</c>, which bounds spinning a predicate with
    /// no single completion to await. Both happen to be 15s today; that is coincidence, not a
    /// relationship, and each may change independently.
    /// </remarks>
    internal static readonly TimeSpan DefaultCompletionBudget = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Awaits <paramref name="executeTask"/> to reach any terminal state (completed, faulted, or
    /// canceled) without observing or rethrowing its exception — the same semantics as polling
    /// <c>ExecuteTask.IsCompleted</c>, just reacting to the real completion instead of a poll.
    /// </summary>
    public static async Task AwaitCompletionAsync(
        Task executeTask,
        TimeSpan? timeout = null,
        [CallerArgumentExpression(nameof(executeTask))] string? taskExpression = null)
    {
        TimeSpan budget = timeout ?? DefaultCompletionBudget;
        Task winner = await Task.WhenAny(executeTask, Task.Delay(budget));

        winner.Should().BeSameAs(
            executeTask,
            $"{taskExpression} must complete within {budget} — a hang here is the regression "
            + "several of these fixtures exist to catch, not a scheduling flake to poll around");
    }
}
