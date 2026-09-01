using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace MarqSpec.Mcp.TopstepX.Tools;

/// <summary>
/// Bars, indicators and levels — the reason this server exists.
/// </summary>
/// <remarks>
/// <para>
/// <b>One MCP tool type, five files.</b> The seven tools here share nothing but the instrument resolver —
/// <c>Bars</c> (<c>get_bars</c>, <c>get_latest_bars</c>), <c>Indicators</c> (<c>get_indicators</c>,
/// <c>get_indicator_at</c>, plus the internal <c>get_market_snapshot</c> batch read),
/// <c>KeyLevels</c> (<c>get_key_levels</c>), <c>Tape</c> (<c>get_footprint</c>, <c>get_volume_profile</c>)
/// and <c>Roll</c> (<c>get_contract_roll</c>) each live in their own partial-class file (gh#391), so a reader
/// chasing one concern never has to skim the other four to find where it ends.
/// </para>
/// <para>
/// <b>Still one type, not five, and that is a deliberate trade rather than an oversight.</b> Splitting into
/// five separately-registered tool types would have meant five separate constructors, and this class is
/// constructed directly — bypassing DI — by every unit test that exercises one of its tools; a type-level
/// split would have forced a mechanical edit onto every one of them just to keep compiling, on top of the
/// edits the optional-parameter fix below already requires. A partial class gets the same file-size and
/// readability win — no file here is anywhere near the 1,217-line original — without multiplying the
/// constructor surface that <see cref="Tools.SnapshotTools"/> and roughly a dozen test fixtures already
/// depend on holding still. <see cref="Resolve"/> and <see cref="_guards"/> are shared by every concern
/// through ordinary partial-class visibility; <c>ReadAsync</c> and <c>CoverageAsync</c> turned out, once
/// split, to have exactly one remaining caller each — <c>Bars</c> and <c>Indicators</c> respectively — so
/// they moved into those files rather than being generalised into a base class or a collaborator that
/// nothing else would ever call.
/// </para>
/// </remarks>
[McpServerToolType]
public sealed partial class MarketDataTools(
    BarCacheService cache,
    TopstepXDbContext database,
    InstrumentRegistry registry,
    IndicatorCatalog catalog,
    IndicatorCacheService indicators,
    LevelMethodCatalog levelMethods,
    IMarketDataGateway gateway,
    ToolGuards guards,
    StoreAvailabilityHolder store,
    TimeProvider clock,
    IOptions<KeyLevelDetectionOptions> detection,
    VolumeProfileService volumeProfiles,
    TapeAvailabilityHolder tape,
    TapeVolumeFrontService volumeFront,
    FootprintCacheService footprints)
{
    private readonly BarCacheService _cache = cache;
    private readonly TopstepXDbContext _database = database;
    private readonly InstrumentRegistry _registry = registry;
    private readonly IndicatorCatalog _catalog = catalog;
    private readonly IndicatorCacheService _indicators = indicators;
    private readonly LevelMethodCatalog _levelMethods = levelMethods;
    private readonly IMarketDataGateway _gateway = gateway;
    private readonly ToolGuards _guards = guards;
    private readonly StoreAvailabilityHolder _store = store;
    private readonly TimeProvider _clock = clock;
    private readonly VolumeProfileService _volumeProfiles = volumeProfiles;
    private readonly TapeAvailabilityHolder _tape = tape;
    private readonly TapeVolumeFrontService _volumeFront = volumeFront;
    private readonly FootprintCacheService _footprints = footprints;

    // The detection defaults, not the detection options: three of the seven fields are overridden per call, and
    // the merge happens in ResolveDetection. The catalogue holds no copy of these -- ILevelMethod.Detect
    // takes them per call precisely because levels are computed on read and nothing stores them (ADR-0013),
    // so the tool boundary is where "the caller did not say" becomes "the operator's configured value".
    private readonly KeyLevelDetectionOptions _detection = detection.Value;

    /// <summary>How much history key-level detection covers when the caller does not say.</summary>
    /// <remarks>
    /// Enough for a level to have been touched more than once at most intraday resolutions. The description
    /// on the parameter advertised this number while the schema required the argument, so an agent following
    /// the description was rejected before its call reached any code (gh#70).
    /// </remarks>
    public const int DefaultLookbackBars = 500;

    /// <summary>The level method <c>get_key_levels</c> detects with when the caller does not name one.</summary>
    private const string DefaultLevelMethodName = "swing";

    private InstrumentId Resolve(string symbol)
    {
        // Every tool in this class reads the store, so the check sits on the path they all take. A per-tool
        // check is a check a new tool forgets.
        _store.Value.Require();

        return ExceptionTranslation.Try(
            () => _registry.Resolve(symbol),
            static ex => ex is KeyNotFoundException or ArgumentException);
    }
}
