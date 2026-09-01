using System.ComponentModel;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;

namespace MarqSpec.Mcp.TopstepX.Tools;

/// <summary>
/// <c>get_contract_roll</c> — the tape-derived changeover plus both front-month answers, at a moment.
/// </summary>
public sealed partial class MarketDataTools
{
    /// <summary>Reports the most recent tape changeover a symbol's stored prints can prove.</summary>
    /// <param name="symbol">The instrument symbol.</param>
    /// <param name="asOfUtc">The instant to evaluate, or null for now.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <returns>
    /// The changeover, the tape front at <paramref name="asOfUtc"/>, and the bar-side seam
    /// around the flip. The gateway pick sits beside the tape only when the ask is now.
    /// </returns>
    [McpServerTool(ReadOnly = true, Idempotent = true, Title = "Get contract roll")]
    [Description(
        "Reports the most recent contract-roll changeover the stored tape can prove for a symbol, "
        + "and the tape front at asOfUtc. There is no historical tape before recording "
        + "began — a changeover from before that is ABSENT, not guessed. `front` is the same object "
        + "get_footprint returns: `used` is `tape-volume` or `none`, never a silent prefer of the "
        + "gateway. Keys inside `front` — including `changeover`, `gatewayContractId` and `agree` — "
        + "are omitted when that answer does not exist. The gateway pick is live only; a historical "
        + "asOfUtc omits `gatewayContractId` and `agree` rather than dating today's pick as if it "
        + "were as-of. `contracts` is the bar-side seam around the changeover (`span` / "
        + "segments) over stored bars in that window, every bar size together; it is "
        + "omitted when there is no changeover to place a window around. `SingleContract` "
        + "means that window has one contract — two contracts on different sizes is "
        + "SpansRoll even when no single series crosses. `span` Unknown means provenance "
        + "was never recorded, not that there was no roll. "
        + "asOfUtc is bounded like get_market_session's atUtc.")]
    public async Task<ToolPayloads.ContractRollInfo> GetContractRoll(
        [Description("The instrument symbol, e.g. ES.")] string symbol,
        [Description("The instant to evaluate, ISO-8601 UTC. Defaults to now.")] DateTimeOffset? asOfUtc = null,
        CancellationToken cancellationToken = default)
    {
        InstrumentId instrument = Resolve(symbol);
        DateTimeOffset now = _clock.GetUtcNow().ToUniversalTime();
        DateTimeOffset at =
            ToolGuards.ValidateInstant((asOfUtc ?? now).ToUniversalTime(), "asOfUtc");
        bool resolveGateway = asOfUtc is null || at == now;

        ToolPayloads.VolumeFrontInfo front =
            await FrontAsync(instrument, cancellationToken, at, resolveGateway).ConfigureAwait(false);

        ToolPayloads.ContractCoverage? contracts = front.Changeover is { } flip
            ? await BarSeamAroundAsync(instrument, flip, at, cancellationToken).ConfigureAwait(false)
            : null;

        return new ToolPayloads.ContractRollInfo(instrument.Symbol, at, front, contracts);
    }

    /// <summary>
    /// Bar provenance in a short window around a tape changeover — stored bars only, never a fetch.
    /// </summary>
    private async Task<ToolPayloads.ContractCoverage> BarSeamAroundAsync(
        InstrumentId instrument,
        ToolPayloads.VolumeFrontChangeoverInfo flip,
        DateTimeOffset asOfUtc,
        CancellationToken cancellationToken)
    {
        DateTimeOffset anchor = flip.FlippedAtUtc
            ?? MarketClock.FromMarket(flip.SessionDate, _levelMethods.Calendar.SessionClose)
                .ToUniversalTime();

        DateTimeOffset start = anchor - TimeSpan.FromDays(2);
        DateTimeOffset end = anchor + TimeSpan.FromDays(2);
        if (end > asOfUtc)
        {
            end = asOfUtc;
        }

        if (end <= start)
        {
            return new ToolPayloads.ContractCoverage(ToolPayloads.ContractSpan.Unknown, []);
        }

        // Per-resolution CoverageAsync cannot see two contracts that live on
        // different sizes — each series is SingleContract, and picking one reports
        // a safe window. Union the stored bars in the window and let the detector
        // answer once. Prices are structural zeros; Segment reads only time and id.
        List<Bar> shape = await _database.Bars
            .AsNoTracking()
            .Where(bar => bar.Venue == _gateway.VenueId
                && bar.Instrument == instrument.Symbol
                && bar.BucketStart >= start
                && bar.BucketStart < end)
            .OrderBy(bar => bar.BucketStart)
            .ThenBy(bar => bar.ResolutionMinutes)
            .Select(bar => new Bar(bar.BucketStart, 0m, 0m, 0m, 0m, 0L, bar.ContractId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (shape.Count == 0)
        {
            return new ToolPayloads.ContractCoverage(ToolPayloads.ContractSpan.Unknown, []);
        }

        return ToolPayloads.ToCoverage(shape);
    }
}
