using FluentAssertions;
using MarqSpec.Mcp.TopstepX.MarketData;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>
/// The exact text <see cref="StoreAvailability.Unavailable"/> builds — the seam <see cref="StoreStartup"/> and
/// <c>Program.MigrateAsync</c> both go through to turn a store fault into an operator-facing sentence.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this file exists at all.</b> Two branches of this method have shipped without a test now: the
/// deadline clamp in <c>StoreStartup.ReachAsync</c> (gh#551) and this one — the empty-<c>detail</c> collapse
/// that drops its own sentence rather than leaving a double space. Both times a reviewer's own mutation, not a
/// test in the tree, was what proved the branch mattered: reverting <c>detailClause</c> to the unconditional
/// <c>detail + " "</c> reproduced the exact double-space text this method exists to avoid, and the full suite
/// stayed green throughout. These two equality assertions are what closes that gap for <c>Unavailable</c>
/// itself — an exact-text assertion, not a substring one, because a substring check would not have caught the
/// reviewer's mutation either: the double space sits inside a string a <c>Contain</c> assertion never looks at
/// closely enough to notice.
/// </para>
/// <para>
/// Confirmed locally the same way the clamp was: reverted <c>detailClause</c> to
/// <c>detail + " "</c>, reran this file, watched
/// <see cref="AnEmptyDetail_DropsItsOwnSentence_RatherThanLeavingADoubleSpace"/> fail on the double space, then
/// reverted the revert and confirmed green again.
/// </para>
/// </remarks>
public sealed class StoreAvailabilityTests
{
    /// <summary>
    /// A blank <c>detail</c> — <see cref="StoreStartup.ReachAsync"/>'s call, once gh#551 moved the connection
    /// target out of <see cref="StoreAvailability.Explanation"/> and onto the log line alone — leaves no
    /// orphaned sentence and no double space behind it.
    /// </summary>
    [Fact]
    public void AnEmptyDetail_DropsItsOwnSentence_RatherThanLeavingADoubleSpace()
    {
        StoreAvailability unavailable = StoreAvailability.Unavailable(string.Empty);

        unavailable.Explanation.Should().Be(
            "The database is not reachable, so cached market data and observations are unavailable. "
            + "Start it with `docker compose up -d postgres`, or point ConnectionStrings__Default at a "
            + "running Postgres, then restart this server. Tools that need no database — list_instruments, "
            + "get_market_session, search_contracts — work regardless.");
    }

    /// <summary>
    /// A non-empty <c>detail</c> — <c>Program.MigrateAsync</c>'s mid-migration branch — is folded in exactly
    /// where the fixed sentence either side of it was written to receive it, with the same spacing as before
    /// gh#551 touched this method for the empty-detail case.
    /// </summary>
    [Fact]
    public void ANonEmptyDetail_IsFoldedInBetweenTheFixedSentences_WithNoDoubleSpace()
    {
        StoreAvailability unavailable =
            StoreAvailability.Unavailable("The connection dropped while applying migrations.");

        unavailable.Explanation.Should().Be(
            "The database is not reachable, so cached market data and observations are unavailable. "
            + "The connection dropped while applying migrations. "
            + "Start it with `docker compose up -d postgres`, or point ConnectionStrings__Default at a "
            + "running Postgres, then restart this server. Tools that need no database — list_instruments, "
            + "get_market_session, search_contracts — work regardless.");
    }
}
