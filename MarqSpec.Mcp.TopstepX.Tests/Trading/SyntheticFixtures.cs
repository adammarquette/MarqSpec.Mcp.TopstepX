// Synthetic fixtures only. No real account ids, names, credentials or network. Every venue call is a recorded fake.
using System.Reflection;
using MarqSpec.Client.ProjectX;
using MarqSpec.Client.ProjectX.Api.Models;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.Tests.Trading;

/// <summary>Account fixtures for every stage (gh#720). Stage comes from the REAL parser over a synthetic name.</summary>
public static class SyntheticAccounts
{
    public const int PracticeId = 900001;        // PRAC-* family
    public const int LegacyPracticeId = 900002;  // PRACTICE* legacy family
    public const int EvaluationId = 900003;      // combine family
    public const int FundedId = 900004;          // EXPRESS-*
    public const int UnknownId = 900005;         // near miss
    public const int MissingId = 909999;         // not returned by the venue

    public static readonly IReadOnlyDictionary<int, string> Names = new Dictionary<int, string>
    {
        [PracticeId] = "PRAC-V2-00000-00000001",
        [LegacyPracticeId] = "PRACTICEJUL3",
        [EvaluationId] = "50KTC-V2-00000-00000003",
        [FundedId] = "EXPRESS-V2-00000-00000004",
        [UnknownId] = "PRACX-V2-00000-00000005",
    };

    public static IReadOnlyList<VenueAccount> All() =>
        [.. Names.Select(kv => new VenueAccount(kv.Key, ProjectXMapping.ResolveStage(kv.Value), true, true, 50_000m))];
}

public sealed class FixedKillSwitch(bool engaged) : ITradingKillSwitch
{
    public bool IsEngaged { get; set; } = engaged;
}

public sealed class RecordingUnknownSignal : ITradingUnknownStageRefusalSignal
{
    public List<int> Refused { get; } = [];
    public void UnknownStageWriteRefused(int accountId) => Refused.Add(accountId);
}

/// <summary>Read seam double: only GetAccountsAsync and VenueId are legal; anything else fails the test.</summary>
public class FakeMarketData : DispatchProxy
{
    public IReadOnlyList<VenueAccount> Accounts { get; set; } = SyntheticAccounts.All();
    public int AccountFetches { get; private set; }

    public static (IMarketDataGateway Gateway, FakeMarketData State) Create()
    {
        IMarketDataGateway proxy = Create<IMarketDataGateway, FakeMarketData>();
        return (proxy, (FakeMarketData)(object)proxy);
    }

    protected override object? Invoke(MethodInfo? m, object?[]? args) => m!.Name switch
    {
        "get_VenueId" => "projectx",
        "GetAccountsAsync" => Fetch(),
        _ => throw new InvalidOperationException($"Unexpected read-seam call {m.Name} during a trading write."),
    };

    private Task<IReadOnlyList<VenueAccount>> Fetch() { AccountFetches++; return Task.FromResult(Accounts); }
}

/// <summary>
/// Venue client double. Counts every WRITE verb; any other member throws so an unexpected venue call is loud.
/// Configure Next* to simulate acks, rejects (HTTP 200 + success:false) and transport faults.
/// </summary>
public class FakeProjectXClient : DispatchProxy
{
    private static readonly HashSet<string> _writes =
        ["PlaceOrderAsync", "ModifyOrderAsync", "CancelOrderAsync", "ClosePositionAsync", "PartialClosePositionAsync"];

    public List<(string Verb, object?[] Args)> WriteCalls { get; } = [];
    public bool Success { get; set; } = true;
    public int ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public long? OrderId { get; set; } = 7_000_001;
    public Exception? Throw { get; set; }

    public static (IProjectXApiClient Client, FakeProjectXClient State) Create()
    {
        IProjectXApiClient proxy = Create<IProjectXApiClient, FakeProjectXClient>();
        return (proxy, (FakeProjectXClient)(object)proxy);
    }

    protected override object? Invoke(MethodInfo? m, object?[]? args)
    {
        if (!_writes.Contains(m!.Name))
        {
            throw new InvalidOperationException($"Unexpected venue call {m.Name}.");
        }

        WriteCalls.Add((m.Name, args ?? []));
        if (Throw is not null)
        {
            return typeof(Task).GetMethod(nameof(Task.FromException), 1, [typeof(Exception)])!
                .MakeGenericMethod(m.ReturnType.GetGenericArguments()[0]).Invoke(null, [Throw]);
        }

        object response = m.Name switch
        {
            "PlaceOrderAsync" => new PlaceOrderResponse { Success = Success, ErrorCode = ErrorCode, ErrorMessage = ErrorMessage, OrderId = Success ? OrderId : null },
            "ModifyOrderAsync" => new ModifyOrderResponse { Success = Success, ErrorCode = ErrorCode, ErrorMessage = ErrorMessage },
            "CancelOrderAsync" => new CancelOrderResponse { Success = Success, ErrorCode = ErrorCode, ErrorMessage = ErrorMessage },
            "ClosePositionAsync" => new ClosePositionResponse { Success = Success, ErrorCode = ErrorCode, ErrorMessage = ErrorMessage },
            _ => new PartialClosePositionResponse { Success = Success, ErrorCode = ErrorCode, ErrorMessage = ErrorMessage },
        };
        return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(m.ReturnType.GetGenericArguments()[0])
            .Invoke(null, [response]);
    }
}

/// <summary>Wires the real gated adapter (PR #734) around the fakes.</summary>
public sealed class GatewayRig
{
    public GatewayRig(TradingExecutionOptions? options = null, bool killSwitch = false)
    {
        (MarketData, Accounts) = FakeMarketData.Create();
        (Client, Venue) = FakeProjectXClient.Create();
        KillSwitch = new FixedKillSwitch(killSwitch);
        Gate = new TradingExecutionGate(Options.Create(options ?? new TradingExecutionOptions { Enabled = true }), KillSwitch);
        Gateway = new ProjectXExecutionGateway(Gate, MarketData, Unknown, Client, new MarqSpec.Mcp.TopstepX.Telemetry.HostTelemetry());
    }

    public IMarketDataGateway MarketData { get; }
    public FakeMarketData Accounts { get; }
    public IProjectXApiClient Client { get; }
    public FakeProjectXClient Venue { get; }
    public FixedKillSwitch KillSwitch { get; }
    public RecordingUnknownSignal Unknown { get; } = new();
    public TradingExecutionGate Gate { get; }
    public IExecutionGateway Gateway { get; }

    public static ExecutionPlaceOrderCommand Limit(int accountId, int size = 1) =>
        new(accountId, "CON.F.US.MES.Z26", ExecutionOrderType.Limit, VenueSide.Buy, size, 6800.25m, null, null);

    /// <summary>Every verb against one account, so stage rules are proven per verb, not just for place.</summary>
    public IEnumerable<(string Verb, Func<Task<ExecutionWriteResult>> Call)> AllVerbs(int accountId) =>
    [
        ("place", () => Gateway.PlaceOrder(Limit(accountId), default)),
        ("modify", () => Gateway.ModifyOrder(new ExecutionModifyOrderCommand(accountId, 42, 2, 6801m, null), default)),
        ("cancel", () => Gateway.CancelOrder(new ExecutionCancelOrderCommand(accountId, 42), default)),
        ("close", () => Gateway.ClosePosition(new ExecutionClosePositionCommand(accountId, "CON.F.US.MES.Z26"), default)),
        ("partial", () => Gateway.PartialClosePosition(new ExecutionPartialClosePositionCommand(accountId, "CON.F.US.MES.Z26", 1), default)),
    ];
}
