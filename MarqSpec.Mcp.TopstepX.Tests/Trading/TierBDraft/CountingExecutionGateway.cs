// gh#720 Tier B (DRAFT, excluded from build until gh#711-#717 land; build with -p:IncludeTierB=true).
// Hand-written IExecutionGateway double against PR #734's interface. CI-safe: no venue, no credentials.
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Venue;

namespace MarqSpec.Mcp.TopstepX.Tests.Trading.TierBDraft;

public sealed class CountingExecutionGateway : IExecutionGateway
{
    public List<object> Writes { get; } = [];
    public Func<object, ExecutionWriteResult> Respond { get; set; } = _ =>
        new(ExecutionWriteLifecycle.Acknowledged, null, 0, null, 7_000_001);

    private Task<ExecutionWriteResult> Record(object command) { Writes.Add(command); return Task.FromResult(Respond(command)); }
    public Task<ExecutionWriteResult> PlaceOrder(ExecutionPlaceOrderCommand c, CancellationToken t) => Record(c);
    public Task<ExecutionWriteResult> ModifyOrder(ExecutionModifyOrderCommand c, CancellationToken t) => Record(c);
    public Task<ExecutionWriteResult> CancelOrder(ExecutionCancelOrderCommand c, CancellationToken t) => Record(c);
    public Task<ExecutionWriteResult> ClosePosition(ExecutionClosePositionCommand c, CancellationToken t) => Record(c);
    public Task<ExecutionWriteResult> PartialClosePosition(ExecutionPartialClosePositionCommand c, CancellationToken t) => Record(c);

    public static ExecutionWriteResult Reject(int code) => new(ExecutionWriteLifecycle.PendingOrRejected, null, code, "synthetic", null);
    public static ExecutionWriteResult Refused(TradingExecutionRefusal r) => new(ExecutionWriteLifecycle.NotTransmitted, r, null, null, null);
}
