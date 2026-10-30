namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>Closed audit outcome names (data dictionary §13).</summary>
public static class TradingAuditOutcomes
{
    /// <summary>Write-ahead row recorded before a venue transmit begins.</summary>
    public const string Attempted = "Attempted";

    /// <summary>Dry-run preview with no transmit.</summary>
    public const string Preview = "Preview";

    /// <summary>Gate refused before or during transmit.</summary>
    public const string Refused = "Refused";

    /// <summary>Broker acknowledged the write.</summary>
    public const string Live = "Live";

    /// <summary>Transmitted but not broker-acknowledged.</summary>
    public const string Failed = "Failed";

    /// <summary>Transmit threw or outcome could not be classified.</summary>
    public const string Unknown = "Unknown";
}
