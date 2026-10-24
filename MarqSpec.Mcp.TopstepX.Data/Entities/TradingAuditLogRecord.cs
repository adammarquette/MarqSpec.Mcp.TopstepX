namespace MarqSpec.Mcp.TopstepX.Data.Entities;

/// <summary>
/// One row in the append-only trading audit log (data dictionary §13, ADR-0024 §5).
/// </summary>
/// <remarks>
/// <para>
/// Written for every trading attempt — preview, confirm, or live — before the outcome is returned. The table
/// accepts inserts only; updates and deletes are refused by the database.
/// </para>
/// <para>
/// No secrets belong here: credentials, confirmation tokens and raw request bodies stay out of the store.
/// <see cref="RequestHash"/> is a one-way fingerprint of the normalised request the attempt carried.
/// </para>
/// </remarks>
public sealed class TradingAuditLogRecord
{
    /// <summary>The row's identity.</summary>
    public long Id { get; set; }

    /// <summary>When the attempt was recorded, UTC.</summary>
    public required DateTimeOffset AttemptedAt { get; set; }

    /// <summary>The venue account id the attempt targeted.</summary>
    public required int AccountId { get; set; }

    /// <summary>
    /// The funding stage parsed from a fresh account fetch at attempt time — not from an earlier read.
    /// Closed vocabulary: <c>Practice</c>, <c>Evaluation</c>, <c>Funded</c>, <c>Unknown</c>.
    /// </summary>
    public required string ParsedStage { get; set; }

    /// <summary>The MCP tool name, e.g. <c>place_order</c>.</summary>
    public required string Tool { get; set; }

    /// <summary>The caller-supplied idempotency key for this attempt.</summary>
    public required string IdempotencyKey { get; set; }

    /// <summary>SHA-256 hex digest of the normalised request payload — no raw body is stored.</summary>
    public required string RequestHash { get; set; }

    /// <summary>Whether the attempt was a dry-run preview rather than a transmit.</summary>
    public required bool DryRun { get; set; }

    /// <summary>Contracts requested or transmitted — used for daily cap rollups on place attempts.</summary>
    public int ContractCount { get; set; }

    /// <summary>
    /// A closed outcome name for the attempt, e.g. <c>Preview</c>, <c>Refused</c>, <c>Live</c>,
    /// <c>Failed</c>.
    /// </summary>
    public required string Outcome { get; set; }

    /// <summary>
    /// The venue's business error code when a gateway write was attempted, or <see langword="null"/> when
    /// no write ran.
    /// </summary>
    public string? VenueErrorCode { get; set; }

    /// <summary>
    /// Broker order id on a <c>Live</c> row, so a replay can return it. Null on the pre-send claim and on
    /// every row the venue did not assign an id to.
    /// </summary>
    public long? OrderId { get; set; }
}
