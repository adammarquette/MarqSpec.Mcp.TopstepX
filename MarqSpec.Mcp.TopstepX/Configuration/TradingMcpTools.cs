namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>Server-stamped MCP tool names written to the audit log (ADR-0024).</summary>
public static class TradingMcpTools
{
    /// <summary>Place-order tool name.</summary>
    public const string PlaceOrderOperation = "place_order";

    /// <summary>Modify-order tool name.</summary>
    public const string ModifyOrderOperation = "modify_order";

    /// <summary>Cancel-order tool name.</summary>
    public const string CancelOrderOperation = "cancel_order";

    /// <summary>Close-position tool name.</summary>
    public const string ClosePositionOperation = "close_position";

    /// <summary>Partial-close tool name.</summary>
    public const string PartialClosePositionOperation = "partial_close_position";
}
