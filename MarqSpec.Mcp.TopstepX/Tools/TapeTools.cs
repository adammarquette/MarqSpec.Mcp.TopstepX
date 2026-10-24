using System.ComponentModel;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MarqSpec.Mcp.TopstepX.Tools;

/// <summary>
/// <c>get_footprint</c> and <c>get_volume_profile</c> — reads over the tape-derived footprint cells.
/// </summary>
/// <remarks>
/// One of the five tool types gh#414 split <c>MarketDataTools</c> into. The volume-front lookup this shares with <see cref="ContractRollTools"/> is
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
/// <param name="telemetry">Counts a read served with <c>live</c> false, and why.</param>
[McpServerToolType]
public sealed class TapeTools(
    InstrumentResolver resolver,
    TopstepXDbContext database,
    IMarketDataGateway gateway,
    ToolGuards guards,
    TapeAvailabilityHolder tape,
    VolumeProfileService volumeProfiles,
    VolumeFrontReader front,
    FootprintCacheService footprints,
    HostTelemetry telemetry)
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
    private readonly HostTelemetry _telemetry = telemetry;

    /// <summary>Reads stored footprint cells for a covered tape window.</summary>
    /// <param name="symbol">The instrument symbol.</param>
    /// <param name="resolutionMinutes">The bar size the cells were projected at.</param>
    /// <param name="fromUtc">The window start, inclusive.</param>
    /// <param name="toUtc">The window end, exclusive.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>The cells under the ledger window that was actually covered.</returns>
    [McpServerTool(ReadOnly = true, Idempotent = true, Title = "Get footprint")]
    [Description(
        "Buy and sell volume by price by bar. The tape only goes forward: there is no historical "
        + "footprint before recording began, and a window before that refuses and names the earliest "
        + "covered time. `covered` is the TapeCoverage window, not the ask. An ask that starts "
        + "inside a bar is floored to that bar's open when the tape covers it, and "
        + "`covered.start` is that open. A roll, a listening hole, "
        + "a gap at the start, or a cut at the end sets `covered.narrowed`. That is the only "
        + "short-window flag, and `covered.end` is when the data ends. When this process is not the "
        + "recorder, the closed window is aggregated from stored prints in memory, nothing is written, "
        + "and `live` is false. `reason` is a closed code. `live` true means this process is listening, "
        + "not that the cells are current. No closed tape refuses and names the fix: under stdio, run a "
        + "separate HTTP recorder on the same database. Reconnecting, not subscribed, stopped, claim "
        + "taken over, and claim lapsed still refuse. `front.used` is `tape-volume` or `none`. Empty "
        + "cells and an over-cap window refuse. Top-level fields are always present.")]
    public async Task<ToolPayloads.FootprintSeries> GetFootprint(
        [Description("The instrument symbol, e.g. ES.")] string symbol,
        [Description("The bar size in minutes the cells were projected at.")] int resolutionMinutes,
        [Description("Window start, ISO-8601 UTC, inclusive.")] DateTimeOffset fromUtc,
        [Description("Window end, ISO-8601 UTC, exclusive.")] DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        LoadedTape loaded = await LoadFootprintCellsAsync(
                symbol, resolutionMinutes, fromUtc, toUtc, cancellationToken)
            .ConfigureAwait(false);
        FootprintRead read = loaded.Read;

        if (read.Cells.Count == 0)
        {
            throw new McpException(await EmptyFootprintRefusalAsync(
                    loaded.Instrument,
                    resolutionMinutes,
                    read.Window,
                    loaded.AggregatedFromTape,
                    cancellationToken)
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

        ToolPayloads.VolumeFrontInfo frontInfo = await _front
            .ReadAsync(loaded.Instrument, cancellationToken)
            .ConfigureAwait(false);
        CountClosedRead(loaded);

        return new ToolPayloads.FootprintSeries(
            loaded.Instrument.Symbol,
            resolutionMinutes,
            cells,
            new ToolPayloads.CoveredWindow(read.Window.Start, read.Window.End, read.Window.Narrowed),
            ToolPayloads.ToTapeCoverage(read.Window, read.Cells),
            frontInfo,
            loaded.Health.IsListening,
            WireReason(loaded.Health));
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
        "Volume by price, the point of control, and the 70% value area, over the same cells as "
        + "get_footprint. The tape only goes forward: there is no historical footprint before recording "
        + "began, and a window before that refuses and names the earliest covered time. `covered` is "
        + "the TapeCoverage window, not the ask. An ask that starts inside a bar is floored to "
        + "that bar's open when the tape covers it, and `covered.start` is that open. "
        + "A roll, a listening hole, a gap at the start, or a "
        + "cut at the end sets `covered.narrowed`. That is the only short-window flag, and "
        + "`covered.end` is when the data ends. When this process is not the recorder, the closed "
        + "window is aggregated from stored prints in memory, nothing is written, and `live` is false. "
        + "`reason` is a closed code. `live` true means this process is listening, not that the cells "
        + "are current. No closed tape refuses and names the fix: under stdio, run a separate HTTP "
        + "recorder on the same database. Reconnecting, not subscribed, stopped, claim taken over, and "
        + "claim lapsed still refuse. `front.used` is `tape-volume` or `none`. An empty profile and an "
        + "over-cap window refuse. Top-level fields are always present.")]
    public async Task<ToolPayloads.VolumeProfileSeries> GetVolumeProfile(
        [Description("The instrument symbol, e.g. ES.")] string symbol,
        [Description("The bar size in minutes the cells were projected at.")] int resolutionMinutes,
        [Description("Window start, ISO-8601 UTC, inclusive.")] DateTimeOffset fromUtc,
        [Description("Window end, ISO-8601 UTC, exclusive.")] DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        LoadedTape loaded = await LoadFootprintCellsAsync(
                symbol, resolutionMinutes, fromUtc, toUtc, cancellationToken)
            .ConfigureAwait(false);
        FootprintRead cells = loaded.Read;

        VolumeProfile profile = ExceptionTranslation.Try(
            () => VolumeProfileAggregator.From(cells.Cells),
            static ex => ex is ArgumentException);

        ToolPayloads.VolumeFrontInfo frontInfo = await _front
            .ReadAsync(loaded.Instrument, cancellationToken)
            .ConfigureAwait(false);
        CountClosedRead(loaded);

        return new ToolPayloads.VolumeProfileSeries(
            loaded.Instrument.Symbol,
            resolutionMinutes,
            [.. profile.ByPrice.Select(level => new ToolPayloads.VolumeAtPricePoint(level.Price, level.Volume))],
            profile.PointOfControl,
            profile.ValueAreaLow,
            profile.ValueAreaHigh,
            profile.ValueAreaVolume,
            profile.TotalVolume,
            new ToolPayloads.CoveredWindow(cells.Window.Start, cells.Window.End, cells.Window.Narrowed),
            ToolPayloads.ToTapeCoverage(cells.Window, cells.Cells),
            frontInfo,
            loaded.Health.IsListening,
            WireReason(loaded.Health));
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
    /// This process not being the recorder is not a refusal. Closed <c>TapeCoverage</c> is
    /// aggregated from <c>Trades</c> in memory and <see cref="TapeAvailability.IsListening"/>
    /// comes back false (gh#728). The holder is read once. A window with no closed tape still
    /// refuses, and that refusal is a <see cref="NoClosedTapeException"/>, not a sentence match.
    /// The still-open sentinel is dropped while the instrument is not listening. Nothing is
    /// saved on that path (ADR-0016). The closed-read counter is not incremented here: a later
    /// empty-cell, aggregator, or front refusal must not count as a serve.
    /// </remarks>
    private async Task<LoadedTape> LoadFootprintCellsAsync(
        string symbol,
        int resolutionMinutes,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        CancellationToken cancellationToken)
    {
        InstrumentId instrument = _resolver.Resolve(symbol);
        BarRange window = _guards.ValidateWindow(fromUtc, toUtc, resolutionMinutes);

        // Once per call. A second read can already be a different answer.
        TapeAvailability health = _tape.For(instrument.Symbol);
        bool aggregate = IsNotTheRecorder(health);

        FootprintRead read;
        if (aggregate)
        {
            read = await ExceptionTranslation.TryAsync(
                    () => AggregateOrNameTheFixAsync(
                        health, instrument, resolutionMinutes, window, cancellationToken),
                    static ex => ex is InvalidOperationException)
                .ConfigureAwait(false);
        }
        else
        {
            // A recorder whose subscription is down, or that lost the claim it held, still
            // refuses (gh#218, gh#728). Projecting is the only write, and it stays on this branch.
            health.Require();
            await EnsureFootprintProjectedAsync(instrument, resolutionMinutes, cancellationToken)
                .ConfigureAwait(false);
            read = await ExceptionTranslation.TryAsync(
                    () => _volumeProfiles.ReadCellsAsync(
                        _venue,
                        instrument,
                        resolutionMinutes,
                        window.Start,
                        window.End,
                        cancellationToken,
                        health),
                    static ex => ex is InvalidOperationException)
                .ConfigureAwait(false);
        }

        RefuseIfOverCellCap(read.Cells.Count, "footprint cells");

        return new LoadedTape(instrument, read, health, aggregate);
    }

    /// <summary>
    /// Aggregates the closed window from prints. A typed <see cref="NoClosedTapeException"/>
    /// is the no-closed-tape case. A window before recording began stays the earliest-time
    /// <see cref="InvalidOperationException"/>.
    /// </summary>
    private async Task<FootprintRead> AggregateOrNameTheFixAsync(
        TapeAvailability health,
        InstrumentId instrument,
        int resolutionMinutes,
        BarRange window,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _volumeProfiles.AggregateClosedCoverageAsync(
                    _venue,
                    instrument,
                    resolutionMinutes,
                    window.Start,
                    window.End,
                    cancellationToken,
                    health)
                .ConfigureAwait(false);
        }
        catch (NoClosedTapeException)
        {
            throw new NoClosedTapeException(health.ExplainNoClosedTape());
        }
    }

    /// <summary>
    /// This process is not recording the instrument, as distinct from a recorder whose subscription
    /// is down or that lost a claim it held. The first may read closed coverage another process
    /// wrote. The second must not.
    /// </summary>
    private static bool IsNotTheRecorder(TapeAvailability health) =>
        health.Reason is TapeUnavailableReason.NeverStarted
            or TapeUnavailableReason.HeldByAnotherRecorder;

    /// <summary>
    /// The closed code beside <c>live</c>. Every named <see cref="TapeUnavailableReason"/> has
    /// an arm. The discard throws, so a new named value still compiles and
    /// <c>WireReason_NamesEveryTapeUnavailableReason</c> is what fails; an unnamed value hits
    /// the same throw. Listening is a code, not a promise the cells are current. The refusing
    /// states still refuse; their codes exist so the mapping is total.
    /// </summary>
    internal static string WireReason(TapeUnavailableReason reason) => reason switch
    {
        TapeUnavailableReason.None => TapeWireReason.Listening,
        TapeUnavailableReason.NeverStarted => TapeClosedReadReason.NeverStarted,
        TapeUnavailableReason.Reconnecting => TapeWireReason.Reconnecting,
        TapeUnavailableReason.ConnectedButNotSubscribed => TapeWireReason.ConnectedButNotSubscribed,
        TapeUnavailableReason.Stopped => TapeWireReason.Stopped,
        TapeUnavailableReason.HeldByAnotherRecorder => TapeClosedReadReason.HeldByAnotherRecorder,
        TapeUnavailableReason.ClaimTakenOver => TapeWireReason.ClaimTakenOver,
        TapeUnavailableReason.ClaimLapsed => TapeWireReason.ClaimLapsed,
        TapeUnavailableReason.StoppedForSessionConflict => TapeWireReason.StoppedForSessionConflict,
        _ => throw new InvalidOperationException(
            "TapeUnavailableReason." + reason + " has no wire code."),
    };

    private static string WireReason(TapeAvailability health) => WireReason(health.Reason);

    /// <summary>Counts a closed read after the answer has survived every refusal.</summary>
    private void CountClosedRead(LoadedTape loaded)
    {
        if (ClosedReadReason(loaded.Health) is { } reason)
        {
            _telemetry.ClosedTapeRead(loaded.Instrument.Symbol, reason);
        }
    }

    /// <summary>Why a served read has <c>live</c> false, or null when the read is live.</summary>
    private static string? ClosedReadReason(TapeAvailability health) => health.Reason switch
    {
        TapeUnavailableReason.NeverStarted => TapeClosedReadReason.NeverStarted,
        TapeUnavailableReason.HeldByAnotherRecorder => TapeClosedReadReason.HeldByAnotherRecorder,
        _ => null,
    };

    /// <summary>One call's cells and the status that decided them.</summary>
    /// <param name="Instrument">The resolved instrument.</param>
    /// <param name="Read">The cells and the ledger window.</param>
    /// <param name="Health">The recorder status read once for this call.</param>
    /// <param name="AggregatedFromTape">True when the cells came from prints, not the stored projection.</param>
    private sealed record LoadedTape(
        InstrumentId Instrument,
        FootprintRead Read,
        TapeAvailability Health,
        bool AggregatedFromTape);

    /// <summary>
    /// Refuses a covered window that still has no cells after the on-read projection — a roll inside
    /// the bar, or prints that do not count. An empty list would look like a quiet market.
    /// </summary>
    private async Task<string> EmptyFootprintRefusalAsync(
        InstrumentId instrument,
        int resolutionMinutes,
        CoveredTapeWindow covered,
        bool aggregatedFromTape,
        CancellationToken cancellationToken)
    {
        string resolution = resolutionMinutes.ToString(
            System.Globalization.CultureInfo.InvariantCulture);

        // A non-recorder already aggregated the prints. Stored cells at another bar size are not
        // a resolution the caller can ask to have projected.
        if (aggregatedFromTape)
        {
            return "No footprint cells at " + resolution
                + "-minute resolution for the covered window. The closed tape produced none. An "
                + "empty cell list would look like a quiet market.";
        }

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

/// <summary>
/// Wire spellings for <see cref="TapeUnavailableReason"/> values that are not a closed-read
/// counter tag. <see cref="TapeClosedReadReason"/> keeps the two that are counted, so the
/// counter and the payload cannot spell those two differently. This type has no
/// <c>All</c> list: a public vocabulary in the telemetry namespace is driven as a tag set.
/// </summary>
internal static class TapeWireReason
{
    /// <summary>This process is listening. Not a promise the cells are current.</summary>
    internal const string Listening = "listening";

    /// <summary>The recorder is reconnecting and the read is refused.</summary>
    internal const string Reconnecting = "reconnecting";

    /// <summary>The hub is up and this instrument is not subscribed. The read is refused.</summary>
    internal const string ConnectedButNotSubscribed = "connected_but_not_subscribed";

    /// <summary>The recorder stopped. The read is refused.</summary>
    internal const string Stopped = "stopped";

    /// <summary>This process held the claim and another recorder took it. The read is refused.</summary>
    internal const string ClaimTakenOver = "claim_taken_over";

    /// <summary>This process held the claim and it lapsed. The read is refused.</summary>
    internal const string ClaimLapsed = "claim_lapsed";

    /// <summary>
    /// The recorder stopped because another TopstepX session took this login. The read is refused.
    /// </summary>
    internal const string StoppedForSessionConflict = "stopped_for_session_conflict";
}
