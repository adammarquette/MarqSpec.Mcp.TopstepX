namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>
/// Marks the current async flow as a bar backfill, so history paging takes the backfill sub-budget
/// instead of a live slot (gh#899).
/// </summary>
internal static class BarBackfillLane
{
    private static readonly AsyncLocal<int> _depth = new();

    /// <summary>Whether the current flow is inside a backfill run.</summary>
    public static bool IsActive => _depth.Value > 0;

    /// <summary>Enters the lane. Pair with <see cref="Exit"/>.</summary>
    public static void Enter() => _depth.Value++;

    /// <summary>Leaves the lane.</summary>
    public static void Exit()
    {
        if (_depth.Value > 0)
        {
            _depth.Value--;
        }
    }
}
