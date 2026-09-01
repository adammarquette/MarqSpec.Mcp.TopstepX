using System.ComponentModel;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MarqSpec.Mcp.TopstepX.Tools;

/// <summary>
/// <c>get_footprint</c> and <c>get_volume_profile</c> — reads over the tape-derived footprint cells, plus
/// the volume-front lookup <c>get_contract_roll</c> also shares through <see cref="FrontAsync"/>.
/// </summary>
public sealed partial class MarketDataTools
{
    /// <summary>Reads stored footprint cells for a covered tape window.</summary>
    /// <param name="symbol">The instrument symbol.</param>
    /// <param name="resolutionMinutes">The bar size the cells were projected at.</param>
    /// <param name="fromUtc">The window start, inclusive.</param>
    /// <param name="toUtc">The window end, exclusive.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The cells under the ledger window that was actually covered.</returns>
    [McpServerTool(ReadOnly = true, Idempotent = true, Title = "Get footprint")]
    [Description(
        "Reads buy/sell volume by price by bar from stored footprint cells. The tape only goes forward: "
        + "there is no historical footprint for a period before recording began — not slow, not expensive, "
        + "ABSENT. A window before recording began is refused and names the earliest covered time; an empty "
        + "answer is not a quiet market. The response reports `covered` from TapeCoverage — not the window "
        + "you asked for — and `contracts` with span SingleContract naming which contract was listened to. "
        + "`contracts.segments` use bar-open times from the cells (`firstBucket` / `lastBucket`), not the "
        + "exclusive coverage end — that range stays on `covered`. A roll or listening hole narrows the "
        + "answer to the newest contiguous run and sets `covered.narrowed`. When the live tape is not "
        + "listening for that instrument the tool refuses with a sentence naming the fix — an empty "
        + "answer and an absent tape must not look the same. Top-level fields are always present. "
        + "`front` names the tape volume-front beside the contract Bars would fetch — `used` is "
        + "`tape-volume` or `none`, never a silent prefer of the gateway. `contracts` stays the "
        + "newest listening run; it is not rewritten from `front`. Keys inside `front` are omitted "
        + "when that answer does not exist. "
        + "A covered window whose stored tape has prints the cells do not yet reflect is projected on this "
        + "read (no vendor call). If the tape still produces no cell — a roll inside the bar, or prints "
        + "that do not count — the tool refuses rather than returning empty `cells`. Never truncates: an "
        + "over-cap window is refused.")]
    public async Task<ToolPayloads.FootprintSeries> GetFootprint(
        [Description("The instrument symbol, e.g. ES.")] string symbol,
        [Description("The bar size in minutes the cells were projected at.")] int resolutionMinutes,
        [Description("Window start, ISO-8601 UTC, inclusive.")] DateTimeOffset fromUtc,
        [Description("Window end, ISO-8601 UTC, exclusive.")] DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        (InstrumentId instrument, FootprintRead read) = await LoadFootprintCellsAsync(
                symbol, resolutionMinutes, fromUtc, toUtc, cancellationToken)
            .ConfigureAwait(false);

        if (read.Cells.Count == 0)
        {
            throw new McpException(await EmptyFootprintRefusalAsync(
                    instrument, resolutionMinutes, read.Window, cancellationToken)
                .ConfigureAwait(false));
        }

        List<ToolPayloads.FootprintCellPoint> cells =
        [
            .. read.Cells
                .OrderBy(c => c.BucketStart)
                .ThenBy(c => c.Price)
                .Select(c => new ToolPayloads.FootprintCellPoint(
                    c.BucketStart, c.Price, c.BuyVolume, c.SellVolume)),
        ];

        return new ToolPayloads.FootprintSeries(
            instrument.Symbol,
            resolutionMinutes,
            cells,
            new ToolPayloads.CoveredWindow(read.Window.Start, read.Window.End, read.Window.Narrowed),
            ToolPayloads.ToTapeCoverage(read.Window, read.Cells),
            await FrontAsync(instrument, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>Aggregates stored footprint cells into a volume profile for a covered tape window.</summary>
    /// <param name="symbol">The instrument symbol.</param>
    /// <param name="resolutionMinutes">The bar size the cells were projected at.</param>
    /// <param name="fromUtc">The window start, inclusive.</param>
    /// <param name="toUtc">The window end, exclusive.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The profile under the ledger window that was actually covered.</returns>
    [McpServerTool(ReadOnly = true, Idempotent = true, Title = "Get volume profile")]
    [Description(
        "Aggregates stored footprint cells into volume by price, the point of control, and the 70% value "
        + "area. The tape only goes forward: there is no historical footprint for a period before recording "
        + "began — not slow, not expensive, ABSENT. A window before recording began is refused and names "
        + "the earliest covered time; an empty profile is not a quiet market. The response reports "
        + "`covered` from TapeCoverage — not the window you asked for — and `contracts` with span "
        + "SingleContract naming which contract was listened to. `contracts.segments` use bar-open times "
        + "from the cells, not the exclusive coverage end. A roll or listening hole narrows the answer to "
        + "the newest contiguous run and sets `covered.narrowed`. When the live tape is not listening the "
        + "tool refuses with a sentence naming the fix — an empty profile and an absent tape must not look "
        + "the same. Health is that instrument's tape, not another symbol's subscribe. Top-level "
        + "fields are always present. `front` names the tape volume-front beside the contract Bars "
        + "would fetch — `used` is `tape-volume` or `none`, never a silent prefer of the gateway. "
        + "`contracts` stays the newest listening run; it is not rewritten from `front`. Keys inside "
        + "`front` are omitted when that answer does not exist. "
        + "A covered window whose stored tape has prints the cells do not yet reflect is projected on this "
        + "read (no vendor call). If the tape still produces no cell the tool refuses rather than "
        + "returning an empty profile. Never truncates: an over-cap window is refused.")]
    public async Task<ToolPayloads.VolumeProfileSeries> GetVolumeProfile(
        [Description("The instrument symbol, e.g. ES.")] string symbol,
        [Description("The bar size in minutes the cells were projected at.")] int resolutionMinutes,
        [Description("Window start, ISO-8601 UTC, inclusive.")] DateTimeOffset fromUtc,
        [Description("Window end, ISO-8601 UTC, exclusive.")] DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        (InstrumentId instrument, FootprintRead cells) = await LoadFootprintCellsAsync(
                symbol, resolutionMinutes, fromUtc, toUtc, cancellationToken)
            .ConfigureAwait(false);

        VolumeProfile profile = ExceptionTranslation.Try(
            () => VolumeProfileAggregator.From(cells.Cells),
            static ex => ex is ArgumentException);

        return new ToolPayloads.VolumeProfileSeries(
            instrument.Symbol,
            resolutionMinutes,
            [.. profile.ByPrice.Select(level => new ToolPayloads.VolumeAtPricePoint(level.Price, level.Volume))],
            profile.PointOfControl,
            profile.ValueAreaLow,
            profile.ValueAreaHigh,
            profile.ValueAreaVolume,
            profile.TotalVolume,
            new ToolPayloads.CoveredWindow(cells.Window.Start, cells.Window.End, cells.Window.Narrowed),
            ToolPayloads.ToTapeCoverage(cells.Window, cells.Cells),
            await FrontAsync(instrument, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Resolves the instrument, validates the window, requires the live tape, ensures footprint cells are
    /// projected, reads them, and enforces the row cap — the preamble <see cref="GetFootprint"/> and
    /// <see cref="GetVolumeProfile"/> shared byte for byte up to the point each diverges on what to do with
    /// the cells: <see cref="GetFootprint"/> refuses an empty read by name, <see cref="GetVolumeProfile"/>
    /// lets <see cref="VolumeProfileAggregator"/> refuse it instead. Running the cap check here, ahead of
    /// that divergence, does not change either tool's answer: a covered window with zero cells can never
    /// also be over cap, so the two checks never compete for which one fires.
    /// </summary>
    private async Task<(InstrumentId Instrument, FootprintRead Read)> LoadFootprintCellsAsync(
        string symbol,
        int resolutionMinutes,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        InstrumentId instrument = Resolve(symbol);
        BarRange window = _guards.ValidateWindow(fromUtc, toUtc, resolutionMinutes);
        _tape.For(instrument.Symbol).Require();

        await EnsureFootprintProjectedAsync(instrument, resolutionMinutes, cancellationToken)
            .ConfigureAwait(false);

        FootprintRead read = await ExceptionTranslation.TryAsync(
                () => _volumeProfiles.ReadCellsAsync(
                    _gateway.VenueId,
                    instrument,
                    resolutionMinutes,
                    window.Start,
                    window.End,
                    cancellationToken),
                static ex => ex is InvalidOperationException)
            .ConfigureAwait(false);

        RefuseIfOverCellCap(read.Cells.Count, "footprint cells");

        return (instrument, read);
    }

    /// <summary>
    /// Refuses a covered window that still has no cells after the on-read projection — a roll inside
    /// the bar, or prints that do not count. An empty list would look like a quiet market.
    /// </summary>
    private async Task<string> EmptyFootprintRefusalAsync(
        InstrumentId instrument,
        int resolutionMinutes,
        CoveredTapeWindow covered,
        CancellationToken cancellationToken)
    {
        // Broaden slightly so a cell whose bucket grazes the covered window is still visible — same
        // loadFrom margin ReadCellsAsync uses. Distinct resolutions other than the ask name the bug.
        DateTimeOffset loadFrom = covered.Start.AddMinutes(-Math.Max(resolutionMinutes, 1));

        List<int> otherResolutions = await _database.FootprintCells
            .AsNoTracking()
            .Where(c => c.Venue == _gateway.VenueId
                && c.Instrument == instrument.Symbol
                && c.ResolutionMinutes != resolutionMinutes
                && c.BucketStart < covered.End
                && c.BucketStart > loadFrom)
            .Select(c => c.ResolutionMinutes)
            .Distinct()
            .OrderBy(r => r)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        string resolution = resolutionMinutes.ToString(
            System.Globalization.CultureInfo.InvariantCulture);

        if (otherResolutions.Count > 0)
        {
            string known = string.Join(
                ", ",
                otherResolutions.Select(static r =>
                    r.ToString(System.Globalization.CultureInfo.InvariantCulture) + "m"));

            return "No footprint cells at " + resolution
                + "-minute resolution for the covered window. TapeCoverage is not per-resolution — "
                + "listening succeeded, and cells exist at other bar sizes (" + known
                + "). Ask for a resolution that has been projected. An empty cell list would look like a "
                + "quiet market.";
        }

        return "No footprint cells at " + resolution
            + "-minute resolution for the covered window. An empty cell list would look like a quiet "
            + "market.";
    }

    /// <summary>
    /// Refuses a tape-derived answer that would exceed the row cap rather than truncating it.
    /// </summary>
    private void RefuseIfOverCellCap(int count, string what)
    {
        if (count > _guards.MaxRows)
        {
            throw new McpException(
                "That covered window holds "
                + count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " " + what + ", over this server's cap of "
                + _guards.MaxRows.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + ". Narrow the window or ask for a coarser resolution. The read is refused rather than "
                + "truncated, because a shortened answer is indistinguishable from a complete one.");
        }
    }

    /// <summary>
    /// Projects stored prints into footprint cells for this resolution before reading them.
    /// </summary>
    /// <remarks>
    /// <b>No catch here, deliberately</b>, for the reason bar reads' own <c>ReadAsync</c> states: a
    /// <c>StoreContentionException</c> is a fact about this server's database and is translated once
    /// for the whole tool surface by <see cref="StoreFaultGuard"/>. It cannot raise a
    /// <c>VenueException</c> at all — <see cref="FootprintCacheService"/> holds no gateway.
    /// </remarks>
    private Task EnsureFootprintProjectedAsync(
        InstrumentId instrument,
        int resolutionMinutes,
        CancellationToken cancellationToken) =>
        _footprints.EnsureProjectedAsync(
            _gateway.VenueId, instrument, resolutionMinutes, cancellationToken);

    /// <summary>
    /// Reads both answers for the front month. Called only after the tape-derived answer is
    /// already going to be returned — a no-tape refusal is not rescued by this object.
    /// </summary>
    private async Task<ToolPayloads.VolumeFrontInfo> FrontAsync(
        InstrumentId instrument,
        CancellationToken cancellationToken,
        DateTimeOffset? asOfUtc = null,
        bool resolveGateway = true)
    {
        TapeVolumeFrontRead read;
        try
        {
            read = await _volumeFront
                .ReadAsync(instrument, cancellationToken, asOfUtc, resolveGateway)
                .ConfigureAwait(false);
        }
        catch (VenueException ex)
        {
            throw new McpException("The venue could not answer: " + ex.Message);
        }

        VolumeFrontChangeover? flip = read.Tape.Changeover;
        return new ToolPayloads.VolumeFrontInfo(
            read.Used,
            resolveGateway ? read.Agree : null,
            read.Tape.ActiveContractId,
            read.Tape.ActiveSessionDate,
            resolveGateway ? read.GatewaySelectedContractId : null,
            flip is null
                ? null
                : new ToolPayloads.VolumeFrontChangeoverInfo(
                    flip.SessionDate,
                    flip.FlippedAtUtc,
                    flip.FromContractId,
                    flip.ToContractId));
    }
}
