using System.ComponentModel;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MarqSpec.Mcp.TopstepX.Tools;

/// <summary>
/// <c>get_footprint</c> and <c>get_volume_profile</c> — reads over the tape-derived footprint cells.
/// </summary>
/// <remarks>
/// One of the five tool types gh#414 split <c>MarketDataTools</c> into: eight dependencies where the one type
/// had fifteen. The volume-front lookup this shares with <see cref="ContractRollTools"/> is
/// <see cref="VolumeFrontReader"/>, injected rather than inherited, so neither type can reach the other's
/// store or catalogue to get it.
/// </remarks>
/// <param name="resolver">Turns a caller's symbol into an instrument, refusing first if the store is absent.</param>
/// <param name="database">The store the cells are read from.</param>
/// <param name="gateway">Read ONCE for its venue id and not kept — every stored cell is keyed by it.</param>
/// <param name="guards">The request-shape checks the whole tool surface shares.</param>
/// <param name="tape">Whether the live tape is listening for this instrument at all.</param>
/// <param name="volumeProfiles">The cell reader, under the coverage ledger's window.</param>
/// <param name="front">The tape volume-front both tape answers publish.</param>
/// <param name="footprints">The on-read projection that brings the cells up to the stored prints.</param>
[McpServerToolType]
public sealed class TapeTools(
    InstrumentResolver resolver,
    TopstepXDbContext database,
    IMarketDataGateway gateway,
    ToolGuards guards,
    TapeAvailabilityHolder tape,
    VolumeProfileService volumeProfiles,
    VolumeFrontReader front,
    FootprintCacheService footprints)
{
    private readonly InstrumentResolver _resolver = resolver;
    private readonly TopstepXDbContext _database = database;
    // THE VENUE ID, NOT THE GATEWAY. Every row this tool reads is keyed by the venue, and that is the only
    // thing it wants from the client -- so the client is read once, here, and not kept. Keeping it would put
    // a live venue client in reach of a type that never calls one, and VenueFailureReportingTests is red on
    // exactly that: a tool that HOLDS a gateway and translates no VenueException reports a venue failure as
    // a bare "an error occurred". There is no _gateway field to call, so that cannot be added by accident
    // (gh#414).
    private readonly string _venue = gateway.VenueId;
    private readonly ToolGuards _guards = guards;
    private readonly TapeAvailabilityHolder _tape = tape;
    private readonly VolumeProfileService _volumeProfiles = volumeProfiles;
    private readonly VolumeFrontReader _front = front;
    private readonly FootprintCacheService _footprints = footprints;

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
        + "answer to the newest contiguous run and sets `covered.narrowed`. When this process is not "
        + "the recorder for that instrument, closed coverage is served and `live` is false — live "
        + "coverage is absent, and the answer is not a live tape. A window with no closed tape still "
        + "refuses with a sentence naming the fix; an empty answer and an absent tape must not look "
        + "the same. `live` is true only while this process is listening for that instrument. "
        + "When this process is the recorder but that instrument is reconnecting, not subscribed, or "
        + "stopped, the tool still refuses with a sentence naming the fix. "
        + "Top-level fields are always present. "
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
        (InstrumentId instrument, FootprintRead read, bool live) = await LoadFootprintCellsAsync(
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
            await _front.ReadAsync(instrument, cancellationToken).ConfigureAwait(false),
            live);
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
        + "the newest contiguous run and sets `covered.narrowed`. When this process is not the recorder, "
        + "closed coverage is served and `live` is false — live coverage is absent, and the answer is not "
        + "a live tape. A window with no closed tape still refuses with a sentence naming the fix; an "
        + "empty profile and an absent tape must not look the same. `live` is true only while this "
        + "process is listening. When this process is the recorder but that instrument is reconnecting, "
        + "not subscribed, or stopped, the tool still refuses with a sentence naming the fix. "
        + "Health is that instrument's tape, not another symbol's subscribe. Top-level "
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
        (InstrumentId instrument, FootprintRead cells, bool live) = await LoadFootprintCellsAsync(
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
            await _front.ReadAsync(instrument, cancellationToken).ConfigureAwait(false),
            live);
    }

    /// <summary>
    /// Resolves the instrument, validates the window, ensures footprint cells are projected, reads them,
    /// and enforces the row cap — the preamble <see cref="GetFootprint"/> and
    /// <see cref="GetVolumeProfile"/> share up to the point each diverges on what to do with the cells.
    /// <see cref="GetFootprint"/> refuses an empty read by name, <see cref="GetVolumeProfile"/> lets
    /// <see cref="VolumeProfileAggregator"/> refuse it instead. Running the cap check here, ahead of that
    /// divergence, does not change either tool's answer: a covered window with zero cells can never also
    /// be over cap, so the two checks never compete for which one fires.
    /// </summary>
    /// <remarks>
    /// This process not being the recorder is not a refusal. Closed <c>TapeCoverage</c> is served and
    /// <see cref="TapeAvailability.IsListening"/> comes back false, so the payload can say live coverage
    /// is absent (gh#728). A window with no closed tape still refuses, and that refusal names the fix.
    /// The still-open sentinel is dropped by <see cref="VolumeProfileService"/> while the instrument is
    /// not listening, so a live listen this process does not hold is not reported as coverage.
    /// </remarks>
    private async Task<(InstrumentId Instrument, FootprintRead Read, bool Live)> LoadFootprintCellsAsync(
        string symbol,
        int resolutionMinutes,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        InstrumentId instrument = _resolver.Resolve(symbol);
        BarRange window = _guards.ValidateWindow(fromUtc, toUtc, resolutionMinutes);
        TapeAvailability health = _tape.For(instrument.Symbol);

        // Not the recorder: stdio, the switch off, or another process holding the claim. Closed
        // coverage is an answer. A recorder whose own subscription is down still refuses — stored
        // cells must not look like that instrument's live tape (gh#218, gh#728).
        if (!IsNotTheRecorder(health))
        {
            health.Require();
        }

        await EnsureFootprintProjectedAsync(instrument, resolutionMinutes, cancellationToken)
            .ConfigureAwait(false);

        FootprintRead read = await ExceptionTranslation.TryAsync(
                () => ReadCellsOrNameTheFixAsync(
                    health, instrument, resolutionMinutes, window, cancellationToken),
                static ex => ex is InvalidOperationException)
            .ConfigureAwait(false);

        RefuseIfOverCellCap(read.Cells.Count, "footprint cells");

        return (instrument, read, health.IsListening);
    }

    /// <summary>
    /// Reads the cells. When this process is not recording and the store has no closed tape, the
    /// refusal names the fix instead of reading as a quiet market.
    /// </summary>
    private async Task<FootprintRead> ReadCellsOrNameTheFixAsync(
        TapeAvailability health,
        InstrumentId instrument,
        int resolutionMinutes,
        BarRange window,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _volumeProfiles.ReadCellsAsync(
                    _venue,
                    instrument,
                    resolutionMinutes,
                    window.Start,
                    window.End,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) when (IsNotTheRecorder(health) && IsNoClosedTape(ex))
        {
            throw new InvalidOperationException(health.Explanation);
        }
    }

    /// <summary>
    /// This process is not recording the instrument, as distinct from a recorder whose subscription
    /// is down. The first may read closed coverage another process wrote. The second must not.
    /// </summary>
    private static bool IsNotTheRecorder(TapeAvailability health) =>
        health.Reason is TapeUnavailableReason.NeverStarted
            or TapeUnavailableReason.HeldByAnotherRecorder;

    /// <summary>
    /// The store's "nothing overlaps" refusal, as distinct from a window that names the earliest
    /// covered time. The earliest-time sentence is the fact a caller needs (R-9.7); this one is
    /// replaced, when this process is not recording, by the sentence that names the fix.
    /// </summary>
    private static bool IsNoClosedTape(InvalidOperationException ex) =>
        ex.Message.Contains("no tape", StringComparison.Ordinal);

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
            .Where(c => c.Venue == _venue
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
    /// <b>No catch here, deliberately</b>, for the reason <c>BarTools.ReadAsync</c> states: a
    /// <c>StoreContentionException</c> is a fact about this server's database and is translated once
    /// for the whole tool surface by <see cref="StoreFaultGuard"/>. It cannot raise a
    /// <c>VenueException</c> at all — <see cref="FootprintCacheService"/> holds no gateway.
    /// </remarks>
    private Task EnsureFootprintProjectedAsync(
        InstrumentId instrument,
        int resolutionMinutes,
        CancellationToken cancellationToken) =>
        _footprints.EnsureProjectedAsync(
            _venue, instrument, resolutionMinutes, cancellationToken);
}
