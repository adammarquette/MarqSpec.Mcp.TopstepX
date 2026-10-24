namespace MarqSpec.Mcp.TopstepX.Data.Entities;

/// <summary>
/// One file a tape export wrote, and whether a cold-storage upload of that file finished (gh#755, <c>R-10.7</c>).
/// </summary>
/// <remarks>
/// <para>
/// This is a status ledger, not a copy of the tape and not a retention policy. A later decision to
/// drop raw tape would read it; this process does not. Timescale <c>policy_retention</c> is not
/// applied here. The row is replaced on the next pass of the same file
/// (<c>ON CONFLICT DO UPDATE</c>), so a retried upload does not insert a second status.
/// </para>
/// <para>
/// <see cref="ContractId"/> is empty for a footprint file. <see cref="FootprintCellRecord"/> has no
/// contract column, so the file is per instrument and resolution. Inventing a contract would be a
/// second copy of a fact the cell does not store.
/// </para>
/// </remarks>
public sealed class TapeExportReceipt
{
    /// <summary>The local file is written and the upload has not finished.</summary>
    public const string StatePending = "pending";

    /// <summary>Drive was not configured. The local file is the copy. Distinct from <see cref="StatePending"/>.</summary>
    public const string StateDriveDisabled = "drive_disabled";

    /// <summary>The Drive file id is the object for this identity.</summary>
    public const string StateUploaded = "uploaded";

    /// <summary>The upload failed. Local rows, the local file, and any previous Drive file id stay.</summary>
    public const string StateFailed = "failed";

    /// <summary>The UTC day the file covers.</summary>
    public required DateOnly Day { get; set; }

    /// <summary><c>trades</c>, <c>tape-coverage</c>, <c>footprint</c>, or <c>manifest</c>.</summary>
    public required string Kind { get; set; }

    /// <summary><c>csv.gz</c>, <c>parquet</c>, or <c>json</c>.</summary>
    public required string Format { get; set; }

    /// <summary>The venue. Empty for the day manifest.</summary>
    public required string Venue { get; set; }

    /// <summary>The normalised symbol. Empty for the day manifest.</summary>
    public required string Instrument { get; set; }

    /// <summary>The venue contract. Empty for footprint files and the manifest.</summary>
    public required string ContractId { get; set; }

    /// <summary>The bar size for a footprint file. Zero otherwise.</summary>
    public required int ResolutionMinutes { get; set; }

    /// <summary>Path under the volume, with <c>/</c> separators.</summary>
    public required string RelativePath { get; set; }

    /// <summary>Lowercase hex sha256 of the file bytes.</summary>
    public required string Sha256 { get; set; }

    /// <summary>Data rows. Zero for a manifest.</summary>
    public required int RowCount { get; set; }

    /// <summary>When this status was written. UTC.</summary>
    public required DateTimeOffset ExportedAtUtc { get; set; }

    /// <summary>The Drive file id after a successful upload. A later failure does not clear it.</summary>
    public string? DriveFileId { get; set; }

    /// <summary>
    /// <see cref="StatePending"/>, <see cref="StateDriveDisabled"/>, <see cref="StateUploaded"/>, or
    /// <see cref="StateFailed"/>.
    /// </summary>
    public required string UploadState { get; set; }

    /// <summary>The exception type name of a failed upload. Null otherwise. Never credential content.</summary>
    public string? FailureType { get; set; }

    /// <summary>The HTTP status of a failed upload, when the provider reported one.</summary>
    public int? FailureStatus { get; set; }
}
