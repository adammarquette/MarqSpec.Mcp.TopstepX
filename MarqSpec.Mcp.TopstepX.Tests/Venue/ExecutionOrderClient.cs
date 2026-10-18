using System.Reflection;
using MarqSpec.Client.ProjectX;
using MarqSpec.Client.ProjectX.Api.Models;

namespace MarqSpec.Mcp.TopstepX.Tests.Venue;

/// <summary>
/// Dispatch proxy that records venue order calls for execution gateway tests (gh#710).
/// </summary>
internal class ExecutionOrderClient : DispatchProxy
{
    /// <summary>How many place calls reached the client.</summary>
    public int PlaceOrderCount { get; private set; }

    /// <summary>How many modify calls reached the client.</summary>
    public int ModifyOrderCount { get; private set; }

    /// <summary>How many cancel calls reached the client.</summary>
    public int CancelOrderCount { get; private set; }

    /// <summary>How many close-position calls reached the client.</summary>
    public int ClosePositionCount { get; private set; }

    /// <summary>How many partial-close calls reached the client.</summary>
    public int PartialClosePositionCount { get; private set; }

    /// <summary>Scripted place response.</summary>
    public Func<PlaceOrderRequest, PlaceOrderResponse> PlaceOrderAnswer { get; set; } = static _ =>
        new PlaceOrderResponse { Success = true, OrderId = 42, ErrorCode = 0 };

    /// <summary>Scripted modify response.</summary>
    public Func<ModifyOrderRequest, ModifyOrderResponse> ModifyOrderAnswer { get; set; } = static _ =>
        new ModifyOrderResponse { Success = true, ErrorCode = 0 };

    /// <summary>Scripted cancel response.</summary>
    public Func<int, long, CancelOrderResponse> CancelOrderAnswer { get; set; } = static (_, _) =>
        new CancelOrderResponse { Success = true, ErrorCode = 0 };

    /// <summary>Scripted close response.</summary>
    public Func<int, string, ClosePositionResponse> ClosePositionAnswer { get; set; } = static (_, _) =>
        new ClosePositionResponse { Success = true, ErrorCode = 0 };

    /// <summary>Scripted partial close response.</summary>
    public Func<int, string, int, PartialClosePositionResponse> PartialClosePositionAnswer { get; set; } =
        static (_, _, _) => new PartialClosePositionResponse { Success = true, ErrorCode = 0 };

    /// <summary>Builds a proxy implementing the vendor client and this double as the same instance.</summary>
    public static (ExecutionOrderClient Fake, IProjectXApiClient Client) Create()
    {
        IProjectXApiClient client = Create<IProjectXApiClient, ExecutionOrderClient>();
        return ((ExecutionOrderClient)(object)client, client);
    }

    /// <inheritdoc />
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        ArgumentNullException.ThrowIfNull(targetMethod);
        ArgumentNullException.ThrowIfNull(args);

        return targetMethod.Name switch
        {
            nameof(IProjectXApiClient.PlaceOrderAsync) => InvokePlace(args),
            nameof(IProjectXApiClient.ModifyOrderAsync) => InvokeModify(args),
            nameof(IProjectXApiClient.CancelOrderAsync) => InvokeCancel(args),
            nameof(IProjectXApiClient.ClosePositionAsync) => InvokeClose(args),
            nameof(IProjectXApiClient.PartialClosePositionAsync) => InvokePartialClose(args),
            _ => throw new InvalidOperationException(
                "This double answers execution methods only; '" + targetMethod.Name + "' is not wired."),
        };
    }

    private object InvokePlace(object?[] args)
    {
        PlaceOrderCount++;
        PlaceOrderRequest request = (PlaceOrderRequest)args[0]!;
        CancellationToken cancellation = (CancellationToken)args[1]!;
        cancellation.ThrowIfCancellationRequested();
        return Task.FromResult(PlaceOrderAnswer(request));
    }

    private object InvokeModify(object?[] args)
    {
        ModifyOrderCount++;
        ModifyOrderRequest request = (ModifyOrderRequest)args[0]!;
        CancellationToken cancellation = (CancellationToken)args[1]!;
        cancellation.ThrowIfCancellationRequested();
        return Task.FromResult(ModifyOrderAnswer(request));
    }

    private object InvokeCancel(object?[] args)
    {
        CancelOrderCount++;
        int accountId = (int)args[0]!;
        long orderId = (long)args[1]!;
        CancellationToken cancellation = (CancellationToken)args[2]!;
        cancellation.ThrowIfCancellationRequested();
        return Task.FromResult(CancelOrderAnswer(accountId, orderId));
    }

    private object InvokeClose(object?[] args)
    {
        ClosePositionCount++;
        int accountId = (int)args[0]!;
        string contractId = (string)args[1]!;
        CancellationToken cancellation = (CancellationToken)args[2]!;
        cancellation.ThrowIfCancellationRequested();
        return Task.FromResult(ClosePositionAnswer(accountId, contractId));
    }

    private object InvokePartialClose(object?[] args)
    {
        PartialClosePositionCount++;
        int accountId = (int)args[0]!;
        string contractId = (string)args[1]!;
        int size = (int)args[2]!;
        CancellationToken cancellation = (CancellationToken)args[3]!;
        cancellation.ThrowIfCancellationRequested();
        return Task.FromResult(PartialClosePositionAnswer(accountId, contractId, size));
    }
}
