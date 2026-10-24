using MarqSpec.Mcp.TopstepX.Domain.MarketData;

namespace MarqSpec.Mcp.TopstepX.MarketData;

/// <summary>One stored print, as the daily archive writes and reads it.</summary>
/// <param name="Venue">The venue.</param>
/// <param name="Instrument">The normalised symbol.</param>
/// <param name="ContractId">The venue contract.</param>
/// <param name="TradeTimeUtc">When the venue says the print occurred. UTC.</param>
/// <param name="Sequence">The ingest tiebreak.</param>
/// <param name="Price">The traded price.</param>
/// <param name="Size">Contracts traded.</param>
/// <param name="Direction">Who lifted. Zero stays unknown.</param>
/// <param name="RecordedAt">When this process received the print. UTC.</param>
public sealed record TapeExportTradeRow(
    string Venue,
    string Instrument,
    string ContractId,
    DateTimeOffset TradeTimeUtc,
    long Sequence,
    decimal Price,
    long Size,
    TradeDirection Direction,
    DateTimeOffset RecordedAt);

/// <summary>One closed listening range, as the daily archive writes and reads it.</summary>
/// <param name="Venue">The venue.</param>
/// <param name="Instrument">The normalised symbol.</param>
/// <param name="ContractId">The venue contract.</param>
/// <param name="RangeStart">Inclusive start. UTC.</param>
/// <param name="RangeEnd">Exclusive end. UTC. Never the still-listening sentinel.</param>
/// <param name="RecordedAt">When the range was written. UTC.</param>
public sealed record TapeExportCoverageRow(
    string Venue,
    string Instrument,
    string ContractId,
    DateTimeOffset RangeStart,
    DateTimeOffset RangeEnd,
    DateTimeOffset RecordedAt);

/// <summary>One footprint cell, as the daily archive writes and reads it.</summary>
/// <param name="Venue">The venue.</param>
/// <param name="Instrument">The normalised symbol.</param>
/// <param name="ResolutionMinutes">The bar size.</param>
/// <param name="BucketStart">When the bar opened. UTC.</param>
/// <param name="Price">The price level.</param>
/// <param name="BuyVolume">Volume that lifted.</param>
/// <param name="SellVolume">Volume that hit.</param>
/// <param name="RecordedAt">When the cell was computed. UTC.</param>
public sealed record TapeExportFootprintRow(
    string Venue,
    string Instrument,
    int ResolutionMinutes,
    DateTimeOffset BucketStart,
    decimal Price,
    long BuyVolume,
    long SellVolume,
    DateTimeOffset RecordedAt);

/// <summary>Everything one UTC day contributes to the archive.</summary>
/// <param name="Day">The UTC day.</param>
/// <param name="Trades">Prints whose trade time falls on that day.</param>
/// <param name="Coverage">Closed listening ranges whose start falls on that day.</param>
/// <param name="Footprint">Cells whose bucket opens on that day.</param>
public sealed record TapeExportDay(
    DateOnly Day,
    IReadOnlyList<TapeExportTradeRow> Trades,
    IReadOnlyList<TapeExportCoverageRow> Coverage,
    IReadOnlyList<TapeExportFootprintRow> Footprint);

/// <summary>One file the archive wrote. The path is relative to the volume root.</summary>
/// <param name="Kind">trades, tape-coverage, or footprint.</param>
/// <param name="Format"><c>csv.gz</c> or <c>parquet</c>.</param>
/// <param name="Venue">The venue. Empty is not used for a data file.</param>
/// <param name="Instrument">The normalised symbol.</param>
/// <param name="ContractId">The venue contract. Empty for a footprint file, which has no contract column.</param>
/// <param name="ResolutionMinutes">The bar size for a footprint file; zero for trades and coverage.</param>
/// <param name="RelativePath">Path under the volume, with <c>/</c> separators.</param>
/// <param name="Rows">Data rows. A header is not a row. The same count is written for both formats.</param>
/// <param name="Sha256">Lowercase hex of the file bytes.</param>
public sealed record TapeExportStoredFile(
    string Kind,
    string Format,
    string Venue,
    string Instrument,
    string ContractId,
    int ResolutionMinutes,
    string RelativePath,
    int Rows,
    string Sha256);

/// <summary>What a read of one day's files concluded.</summary>
/// <param name="Verified">Sha256 and row counts match the manifest, and no still-listening range is present.</param>
/// <param name="Reason">Empty when <paramref name="Verified"/> is true; otherwise the first check that failed.</param>
/// <param name="TradeRows">Trade rows across contracts, counted once. Zero when the files could not be read.</param>
/// <param name="CoverageRows">Closed coverage rows across contracts.</param>
/// <param name="FootprintRows">Footprint rows across instruments.</param>
/// <param name="Files">The files that verified, or empty when they did not.</param>
public sealed record TapeExportVerification(
    bool Verified,
    string Reason,
    int TradeRows,
    int CoverageRows,
    int FootprintRows,
    IReadOnlyList<TapeExportStoredFile>? Files = null);

/// <summary>Counts from one export or import pass. The pass does not delete local rows.</summary>
/// <param name="DaysExported">Days whose files verified.</param>
/// <param name="DaysRefused">Days that failed verification or were refused on import.</param>
/// <param name="RowsExported">Trade, coverage and footprint rows written.</param>
/// <param name="RowsImported">Rows inserted. A conflict inserts nothing.</param>
public sealed record TapeExportReport(
    int DaysExported,
    int DaysRefused,
    int RowsExported,
    int RowsImported);
