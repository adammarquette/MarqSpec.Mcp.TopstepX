using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.Embeddings;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Tools;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace MarqSpec.Mcp.TopstepX.Tests;

/// <summary>
/// The container has to actually build.
/// </summary>
/// <remarks>
/// <para>
/// This exists because it did not. <see cref="ProjectXMarketDataGateway"/> was registered as a singleton while
/// the vendor client registers <c>IProjectXApiClient</c> as <b>scoped</b> — a captive dependency, which the
/// container refuses outright. The process died at <c>builder.Build()</c>, before the transport existed, so an
/// MCP client saw a bare transport failure.
/// </para>
/// <para>
/// <b>Every other test in this suite constructs its subject by hand</b>, so none of them touched the
/// composition root and none of them could have caught it. Worse, the fault only appears when credentials
/// <i>are</i> configured — the unconfigured gateway has no dependencies and is safe at any lifetime — so every
/// local run without a <c>.env</c> was green.
/// </para>
/// <para>
/// <see cref="ServiceProviderOptions.ValidateOnBuild"/> and <see cref="ServiceProviderOptions.ValidateScopes"/>
/// are both on, which is what turns a startup crash into a fast unit test.
/// </para>
/// </remarks>
public sealed class CompositionRootTests
{
    private static readonly Dictionary<string, string?> _baseSettings = new()
    {
        ["ConnectionStrings:Default"] = "Host=localhost;Database=x;Username=u;Password=p",
        ["MarketData:Instruments"] = "ES,NQ",
        ["MarketData:SessionCloseCentral"] = "16:00",
        ["MarketData:MaxRows"] = "5000",
    };

    private static ServiceProvider Build(Dictionary<string, string?> extra, McpOptions mcp)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(_baseSettings.Concat(extra));

        Program.ConfigureServices(builder, mcp);

        // Exactly what the runtime does at startup, and what caught nothing until this test existed.
        return builder.Services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    [Fact]
    public void TheEmbeddingKeyIsRedactedFromHttpLogging()
    {
        // IHttpClientFactory's logging handlers write request headers at Trace, and raising
        // System.Net.Http.HttpClient to Trace is exactly what an operator does when embeddings are not
        // landing. THIS REPOSITORY IS PUBLIC, and the sibling ProjectX client has already leaked and rotated
        // a real credential once.
        //
        // This pins the REQUIREMENT (the token is redacted), not the mechanism. The framework default already
        // redacts every header, so the way this breaks is not by someone deleting a call -- it is by someone
        // ADDING RedactLoggedHeaders with a narrower list, which replaces the redact-everything default with
        // an allow-list. That is a change that reads like hardening and is not, and this test is what catches
        // it.
        using ServiceProvider provider = Build(
            new Dictionary<string, string?> { ["Embeddings:ApiKey"] = "a-key-that-must-not-be-logged" },
            new McpOptions { Transport = McpTransport.Stdio });

        HttpClientFactoryOptions options = provider
            .GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>()
            .Get(nameof(IEmbeddingProvider));

        options.ShouldRedactHeaderValue("Authorization").Should().BeTrue();
    }

    [Fact]
    public void TheContainerBuilds_WhenTheVenueIsNotConfigured()
    {
        using ServiceProvider provider = Build([], new McpOptions { Transport = McpTransport.Stdio });

        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMarketDataGateway>()
            .Should().BeOfType<UnconfiguredMarketDataGateway>();
    }

    [Fact]
    public void TheContainerBuilds_WhenTheVenueIsConfigured()
    {
        // THE regression. This is the path a run without a .env never reaches, and the one the compose stack
        // takes -- so it was the first configuration anyone actually deployed, and it died on startup.
        Dictionary<string, string?> configured = new()
        {
            ["ProjectX:ApiKey"] = "a-username",
            ["ProjectX:ApiSecret"] = "an-api-key",
            ["ProjectX:DataTier"] = "Simulated",
        };

        using ServiceProvider provider = Build(configured, new McpOptions { Transport = McpTransport.Stdio });

        using IServiceScope scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMarketDataGateway>()
            .Should().BeOfType<ProjectXMarketDataGateway>();
    }

    [Fact]
    public void TheHistoryPacerIsOneAllowanceSharedByEveryScope()
    {
        // The vendor counts History/retrieveBars against the CREDENTIAL, not against a request scope. The
        // gateway is deliberately scoped (see above), so a pacer registered alongside it at the same lifetime
        // would give every concurrent tool call its own full allowance -- N times the documented rate, with
        // nothing anywhere reporting it. Lifetime is the whole of this mechanism's correctness (gh#43).
        Dictionary<string, string?> configured = new()
        {
            ["ProjectX:ApiKey"] = "a-username",
            ["ProjectX:ApiSecret"] = "an-api-key",
            ["ProjectX:DataTier"] = "Simulated",
        };

        using ServiceProvider provider = Build(configured, new McpOptions { Transport = McpTransport.Stdio });

        using IServiceScope first = provider.CreateScope();
        using IServiceScope second = provider.CreateScope();

        VenueRequestPacer one = first.ServiceProvider.GetRequiredService<VenueRequestPacer>();
        VenueRequestPacer other = second.ServiceProvider.GetRequiredService<VenueRequestPacer>();

        one.Should().BeSameAs(other);
        one.Capacity.Should().Be(VenueRequestPacer.HistoryRequestsPerWindow);
        one.Window.Should().Be(VenueRequestPacer.HistoryWindow);
    }

    [Fact]
    public void TheReadTriggeredReplayCounterIsOneCountSharedByEveryScope()
    {
        // IndicatorCacheService is scoped so its memo cannot outlive a request. The replay count has to
        // outlive every scope or an operator — and startup warmup — cannot tell a one-off cold read from a
        // process that has been replaying on every call (gh#347).
        using ServiceProvider provider = Build([], new McpOptions { Transport = McpTransport.Stdio });

        using IServiceScope first = provider.CreateScope();
        using IServiceScope second = provider.CreateScope();

        IndicatorReadProjectionCounter one =
            first.ServiceProvider.GetRequiredService<IndicatorReadProjectionCounter>();
        IndicatorReadProjectionCounter other =
            second.ServiceProvider.GetRequiredService<IndicatorReadProjectionCounter>();

        one.Should().BeSameAs(other);
        one.Replays.Should().Be(0);
    }

    [Fact]
    public void TheContainerBuilds_ForTheHttpTransport()
    {
        McpOptions http = new() { Transport = McpTransport.Http, HttpBearerToken = "a-token" };
        Dictionary<string, string?> settings = new()
        {
            ["Mcp:Transport"] = "Http",
            ["Mcp:HttpBearerToken"] = "a-token",
        };

        using ServiceProvider provider = Build(settings, http);
        provider.Should().NotBeNull();
    }

    [Fact]
    public void TheContainerBuilds_WhenCredentialsAreConfiguredAndTheRecorderIsEnabled()
    {
        // THE captive-dependency case the recorder exists to survive. IProjectXApiClient is scoped;
        // a BackgroundService is a singleton. Consuming the client in the constructor refuses to
        // build — and only when credentials ARE configured, which is the path local dev never
        // reaches. ValidateOnBuild + ValidateScopes turn that startup crash into this test.
        Dictionary<string, string?> configured = new()
        {
            ["ProjectX:ApiKey"] = "a-username",
            ["ProjectX:ApiSecret"] = "an-api-key",
            ["ProjectX:DataTier"] = "Simulated",
            ["MarketData:RecordTape"] = "true",
            ["Mcp:HttpBearerToken"] = "a-token",
        };

        using ServiceProvider provider = Build(
            configured,
            new McpOptions { Transport = McpTransport.Http, HttpBearerToken = "a-token" });

        provider.GetServices<IHostedService>().Should().Contain(s => s is TradeTapeRecorder);
        provider.GetServices<IHostedService>().Should().Contain(
            s => s is IndicatorWarmup,
            "warmup is always registered; the switch decides whether ExecuteAsync runs");
    }

    [Fact]
    public void TheContainerBuilds_WhenCredentialsAreConfiguredAndWarmupIsEnabled()
    {
        Dictionary<string, string?> configured = new()
        {
            ["ProjectX:ApiKey"] = "a-username",
            ["ProjectX:ApiSecret"] = "an-api-key",
            ["ProjectX:DataTier"] = "Simulated",
            ["MarketData:WarmIndicators"] = "true",
            ["Mcp:HttpBearerToken"] = "a-token",
        };

        using ServiceProvider provider = Build(
            configured,
            new McpOptions { Transport = McpTransport.Http, HttpBearerToken = "a-token" });

        provider.GetServices<IHostedService>().Should().Contain(s => s is IndicatorWarmup);
    }

    [Theory]
    [InlineData(typeof(ReferenceTools))]
    [InlineData(typeof(BarTools))]
    [InlineData(typeof(IndicatorTools))]
    [InlineData(typeof(KeyLevelTools))]
    [InlineData(typeof(TapeTools))]
    [InlineData(typeof(ContractRollTools))]
    [InlineData(typeof(AccountTools))]
    [InlineData(typeof(SnapshotTools))]
    [InlineData(typeof(ObservationTools))]
    [InlineData(typeof(SessionBarTools))]
    [InlineData(typeof(SessionIndicatorTools))]
    public void EveryToolTypeCanBeResolvedFromARequestScope(Type toolType)
    {
        // The MCP SDK activates a tool type per call from the request scope, and it resolves constructor
        // parameters from DI rather than activating them recursively. A tool whose dependency is not
        // registered therefore fails at CALL time, per tool, with nothing failing at startup -- so a probe
        // that exercised some tools and not others would report the server healthy.
        Dictionary<string, string?> configured = new()
        {
            ["ProjectX:ApiKey"] = "a-username",
            ["ProjectX:ApiSecret"] = "an-api-key",
            ["ProjectX:DataTier"] = "Simulated",
        };

        using ServiceProvider provider = Build(configured, new McpOptions { Transport = McpTransport.Stdio });
        using IServiceScope scope = provider.CreateScope();

        Func<object> resolve = () => ActivatorUtilities.CreateInstance(scope.ServiceProvider, toolType);

        resolve.Should().NotThrow(
            toolType.Name + " cannot be built from a request scope, so every call to its tools would fail "
            + "while the server reported itself healthy.");
    }

    [Fact]
    public void TheMarketDataToolFamily_IsUnchangedByTheFileSplit()
    {
        // gh#391 splits MarketDataTools.cs (1,217 lines, 15 constructor dependencies) into one file per
        // concern -- Bars, Indicators, KeyLevels, Tape, Roll -- as a `partial class`. The card's own
        // acceptance criteria treat this as the load-bearing check: "No change to any tool's response
        // payload or [Description] text" and "tools/list returns the same tools from this family, with
        // the same names and schemas." Pinned here against the SDK's own tool registry rather than
        // against source text, so a future edit that changes what a client actually receives goes red
        // even if it leaves every XML doc comment alone.
        //
        // Eight names, not the seven the file's original 2023-era docstring counted: gh#349 added
        // get_contract_roll after that count was written, and gh#388's GetLatestIndicatorReadings is
        // internal -- composed by SnapshotTools, never registered as its own McpServerTool -- so it
        // does not add a ninth.
        string[] expectedNames =
        [
            "get_bars", "get_latest_bars", "get_indicators", "get_indicator_at",
            "get_key_levels", "get_footprint", "get_volume_profile", "get_contract_roll",
        ];

        using ServiceProvider provider = Build([], new McpOptions { Transport = McpTransport.Stdio });

        List<McpServerTool> family = [.. provider.GetServices<McpServerTool>()
            .Where(t => expectedNames.Contains(t.ProtocolTool.Name))];

        family.Select(t => t.ProtocolTool.Name).Should().BeEquivalentTo(expectedNames,
            "the split moves code between files; it must not add, drop or rename a tool in this family");

        // The whole wire-level Tool object a client actually receives -- name, title, description, the
        // JSON input schema (parameter names, types, requiredness, defaults) and the ReadOnly/Idempotent
        // annotations -- not a text-level Description diff, which a schema built from the wrong partial
        // file's parameter list could still pass. Baselines below are the exact `JsonSerializer.Serialize
        // (tool.ProtocolTool)` output captured against the pre-split file (origin/develop at 9de6563,
        // before gh#391) and diffed byte-for-byte against the post-split output -- see the PR body for
        // how that diff was run.
        foreach (McpServerTool tool in family)
        {
            _knownGoodToolJson.Should().ContainKey(tool.ProtocolTool.Name);

            JsonNode? actual = JsonSerializer.SerializeToNode(tool.ProtocolTool);
            JsonNode? expected = JsonNode.Parse(_knownGoodToolJson[tool.ProtocolTool.Name]);

            JsonNode.DeepEquals(actual, expected).Should().BeTrue(
                tool.ProtocolTool.Name + "'s wire-level Tool object must be byte-for-byte the same "
                + "before and after the split.\nActual:   " + actual
                + "\nExpected: " + expected);
        }
    }

    /// <summary>
    /// Every family tool's serialised <c>Tool</c> object, captured against the pre-split
    /// <c>MarketDataTools.cs</c> (origin/develop at 9de6563, before gh#391) and diffed byte-for-byte
    /// against the post-split output -- see the PR body for how.
    /// </summary>
    /// <remarks>
    /// <b>The baseline moved once since, deliberately, for <c>period</c> (gh#495).</b>
    /// <c>get_indicators</c> and <c>get_indicator_at</c> gained an optional <c>period</c> that SELECTS among
    /// the periods an operator configured, so their entries below were recaptured from the actual output and
    /// the differences are exactly two: the new <c>period</c> property, appended, with
    /// <c>"type":["integer","null"]</c> and <c>"default":null</c> -- and the description text saying what it
    /// selects. <b><c>required</c> is unchanged on both</b>, which is the half that matters: an optional
    /// argument that landed in <c>required</c> would break every existing caller, and a moved baseline is
    /// the one way that goes green. Anything else appearing in a future diff here is a regression, not an
    /// update; recapture only what a card asked for, and say which card.
    /// <para>
    /// <b>And once more for <c>get_bars</c>'s description (gh#504).</b> Making the coverage ledger per
    /// contract means a memo-covered read resolves the instrument's contracts first, so
    /// <c>venueRequests == 0</c> stopped proving "no vendor round trip" and only ever proved "no bar fetch".
    /// The sentence saying otherwise was the one an MCP client reads, so it was corrected and this baseline
    /// recaptured from the actual output. <b>The diff is exactly one field</b> — <c>description</c> on
    /// <c>get_bars</c>. Name, title, input schema, <c>required</c> and the annotations are untouched.
    /// </para>
    /// <para>
    /// <b>And once more for both bar tools' descriptions (gh#592).</b> The response gained <c>history</c>,
    /// which says whether this call's historical half was decided over the contracts the product's cycle
    /// names or only over what the vendor happened to list. A field an MCP client is never told about is a
    /// field it cannot act on — and the whole point of this one is that the payload it sits on otherwise
    /// looks ordinary — so the descriptions say what each value means and, explicitly, that it is not one of
    /// <c>contracts.span</c>'s. <b>The diff is exactly two fields</b> — <c>description</c> on
    /// <c>get_bars</c> and on <c>get_latest_bars</c>. Input schemas, <c>required</c> and the annotations are
    /// untouched: nothing here is a new argument.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<string, string> _knownGoodToolJson = new(StringComparer.Ordinal)
    {
        ["get_bars"] =
            """{"name":"get_bars","title":"Get bars","description":"Reads OHLCV bars for an instrument over a time window. Served from a local cache; the vendor is called only for buckets genuinely missing, where 'genuinely' excludes weekends, the daily maintenance window and holidays. The response reports `venueRequests` and `fetchedBuckets`, and only the first is evidence of a bar fetch: `venueRequests` counts HISTORY requests alone, so `venueRequests == 0` is the exact test for no bar fetch \u2014 NOT for no vendor call at all, because a read whose gaps the empty-range memo covers still makes one contract search per request. It undercounts vendor traffic and never overcounts it. `fetchedBuckets` counts how much the answer changed the store and can read zero even after a genuine fetch. Never returns a truncated series: an over-cap window is refused with the real count. The response also carries `contracts`: bars are keyed by the symbol, so a window spanning a quarterly roll contains TWO contracts. `contracts.span` is SingleContract, SpansRoll, or Unknown \u2014 Unknown means the provenance was never recorded, NOT that there was no roll. Adjacent quarters do not trade at the same price; do not read a series across a roll as one. `history.selection` is a SEPARATE question from `contracts.span` and must not be read as one of its values: span asks whether these bars cross a roll, history asks whether the contracts they were CHOSEN FROM were the ones this product's contract-month cycle names. A window can be SingleContract and still have been decided among survivors. AsTheCycleNames means every expiry the cycle named was listed by the vendor and the volume decision ran over all of them. NarrowedByTheVenue means the vendor did not list some of them, so the decision ran over what was left \u2014 the bars are a real series from a real contract, and when the only survivor was the vendor's own active contract that stretch is the thin, complete-looking series this selection exists to prevent you acting on. FellBackToTheFront is worse still: NONE of the cycle's expiries was listed, so no volume decision ran for that stretch at all. AsTheFrontAlone is the whole-read form of that risk: there was no contract-month cycle to decide against \u2014 this server does not serve that instrument, or the vendor's active contract has an expiry that does not read against the cycle \u2014 so the entire window is fetched from the vendor's active contract. It is recorded where the plan is cut, not inferred from `venueRequests`. `history.unresolved` names the expiries that fell away, e.g. M26. NotDecidedHere means THIS call decided no history: every bucket was already stored, or the window sits in the present band. It is in no case a statement that the stored history is whole: bars fetched by an earlier degraded read read back exactly like any other, and nothing recorded about them says otherwise.","inputSchema":{"type":"object","properties":{"symbol":{"description":"The instrument symbol, e.g. ES.","type":"string"},"resolutionMinutes":{"description":"The bar size in minutes, e.g. 1, 5, 15, 60.","type":"integer"},"fromUtc":{"description":"Window start, ISO-8601 UTC, inclusive.","type":"string","format":"date-time"},"toUtc":{"description":"Window end, ISO-8601 UTC, exclusive.","type":"string","format":"date-time"}},"required":["symbol","resolutionMinutes","fromUtc","toUtc"]},"outputSchema":null,"annotations":{"title":"Get bars","destructiveHint":null,"idempotentHint":true,"openWorldHint":null,"readOnlyHint":true},"icons":null,"_meta":null}""",
        ["get_latest_bars"] =
            """{"name":"get_latest_bars","title":"Get latest bars","description":"Reads the most recent closed bars for an instrument. Anchored on the last CLOSED bucket, never a forming one. This is usually the tool to reach for over get_bars, which needs explicit dates. The response shape is get_bars', `contracts` and `history` included, and those two mean exactly what they mean there \u2014 read `history.selection` before treating a deep lookback as ordinary.","inputSchema":{"type":"object","properties":{"symbol":{"description":"The instrument symbol, e.g. ES.","type":"string"},"resolutionMinutes":{"description":"The bar size in minutes.","type":"integer"},"count":{"description":"How many bars to return.","type":"integer"}},"required":["symbol","resolutionMinutes","count"]},"outputSchema":null,"annotations":{"title":"Get latest bars","destructiveHint":null,"idempotentHint":true,"openWorldHint":null,"readOnlyHint":true},"icons":null,"_meta":null}""",
        ["get_indicators"] =
            """{"name":"get_indicators","title":"Get indicators","description":"Reads an indicator series from a local cache. The VENDOR IS NEVER CALLED: every value is computed from bars this server already holds. A series the cache has no values for — after an indicator is added or a period is changed — is computed and stored by the first read that asks for it. At the shipped catalogue that costs about eight seconds once for a year of 5-minute bars, and it grows with the number of series the operator configures: every additional period is one more series in the same replay. An HTTP process with MarketData__WarmIndicators on starts that replay at boot (stdio never does). A read that arrives before warmup finishes that series still pays that cost, or can contend with it; once that series is written, the first read is a probe. Known indicators: atr, rsi, sma, ema, macd, macd-signal, macd-histogram, vwap, vwap-rolling, bb-upper, bb-middle, bb-lower. An unknown name is an error listing these, because a typo that returned no data would read as 'no signal'. Each name is computed at its primary configured period and, where the operator configured them, at additional periods; pass period to select one. A long period needs that many bars inside ONE contract run before it measures, so a freshly configured ema at 200 reads as ABSENT until the current contract has 200 stored bars. Buckets where the indicator could not yet measure are ABSENT rather than zero. Values are never smoothed across a contract roll, so expect a run of absent values just after one; `contracts.span` says whether the window contains a roll — and Unknown there means the provenance was never recorded, not that there was none.","inputSchema":{"type":"object","properties":{"symbol":{"description":"The instrument symbol, e.g. ES.","type":"string"},"resolutionMinutes":{"description":"The bar size in minutes.","type":"integer"},"indicator":{"description":"The indicator name, e.g. rsi.","type":"string"},"fromUtc":{"description":"Window start, ISO-8601 UTC, inclusive.","type":"string","format":"date-time"},"toUtc":{"description":"Window end, ISO-8601 UTC, exclusive.","type":"string","format":"date-time"},"period":{"description":"Which configured period to read, e.g. 200. Omit for the indicator's primary configured period, which is also the one get_market_snapshot reports. Only periods the operator configured are readable: any other is an error listing the configured ones, never an empty series. Not accepted for vwap, which is anchored to the session. For macd, macd-signal and macd-histogram this is the SLOW length.","type":["integer","null"],"default":null}},"required":["symbol","resolutionMinutes","indicator","fromUtc","toUtc"]},"outputSchema":null,"annotations":{"title":"Get indicators","destructiveHint":null,"idempotentHint":true,"openWorldHint":null,"readOnlyHint":true},"icons":null,"_meta":null}""",
        ["get_indicator_at"] =
            """{"name":"get_indicator_at","title":"Get indicator as of","description":"Reads one indicator value as of a moment, from the same local cache get_indicators reads, and on the same terms: no vendor call, and a series with no stored values is computed by the first read that needs it — or at HTTP startup when MarketData__WarmIndicators is on, once warmup has finished that series. A read before then is still the first-read cost. period selects among the operator's configured periods on the same terms as get_indicators. Returns the value at or BEFORE that moment, never after — a later value is information the market did not have. Cannot-measure DROPS the `value` KEY instead of sending null, so the whole reading arrives as `{}`: test whether the key is THERE, never whether it equals null. An ABSENT value means CANNOT MEASURE, not zero and not neutral — refuse to conclude rather than substitute. `contractId` names the contract the value belongs to when it is known; two readings from different contracts are not comparable.","inputSchema":{"type":"object","properties":{"symbol":{"description":"The instrument symbol, e.g. ES.","type":"string"},"resolutionMinutes":{"description":"The bar size in minutes.","type":"integer"},"indicator":{"description":"The indicator name, e.g. atr.","type":"string"},"asOfUtc":{"description":"The moment, ISO-8601 UTC.","type":"string","format":"date-time"},"period":{"description":"Which configured period to read, e.g. 200. Omit for the indicator's primary configured period, which is also the one get_market_snapshot reports. Only periods the operator configured are readable: any other is an error listing the configured ones, never an empty series. Not accepted for vwap, which is anchored to the session. For macd, macd-signal and macd-histogram this is the SLOW length.","type":["integer","null"],"default":null}},"required":["symbol","resolutionMinutes","indicator","asOfUtc"]},"outputSchema":null,"annotations":{"title":"Get indicator as of","destructiveHint":null,"idempotentHint":true,"openWorldHint":null,"readOnlyHint":true},"icons":null,"_meta":null}""",
        ["get_key_levels"] =
            """{"name":"get_key_levels","title":"Get key levels","description":"Detects support and resistance as ZONES rather than lines, sized in ATR multiples so a zone is comparably wide across instruments. Significance is prominence in ATR multiples, so a 2.0 on ES and a 2.0 on NQ mean the same thing. A zone's support/resistance label is assigned relative to the CURRENT price, not to how it formed — a broken resistance is today's support. Detection is confined to the contract in front: if the lookback spans a quarterly roll, `detectedOverBars` is smaller than the lookback asked for, because a level from the expiring contract sits at a price the current one has never traded. The SAME truncation also happens when the window holds bars with no recorded contract — history cached before this server tracked provenance. Read `contracts.span` to tell the two apart: `SpansRoll` means the store has two DIFFERENT recorded contracts — a real roll — even when an unattributed run also sits in the window. `Unknown` means at least one run's contract was never recorded and the known ones never disagree — genuinely cannot tell whether a roll happened there, NOT a statement that it did not. Read `detectedOverBars` — fewer bars behind a level is less weight for it either way. Overlapping zones MERGE whichever side of price they formed on, so one reported zone can be a support and a resistance that ran into each other; `touchCount` is how many pivots went into it. `pivotSource`, `pivotLookback` and `pivotRightLookback` tune the detection for one call; OMIT them and this server's configured defaults apply. They carry no default of their own, because the default is an operator setting rather than a constant — omitting one asks for the configured value, it does not name a particular one. Zone width, the significance floor and the two caps are operator settings only, so every level this server reports is sized, filtered and capped alike and two of them can be compared. Each method returns at most `detection.maxLevels` levels, the most significant ones; `methods[i].levels.length == detection.maxLevels` is the per-method signal that that method was cut, and `capped` is true when any requested method stopped there. The top-level `levels` array is the union, ordered by price — its length is not a completeness signal. Levels below a method's cap are absent rather than folded into the ones you can see. The response reports the detection it actually ran under as `detection`, so an empty `levels` can be told from a market with no structure — read it with `detectedOverBars`. `methods` selects which detectors run — `swing`, `session`, `pivot-classic`, `pivot-fibonacci`, `pivot-camarilla`, `pivot-woodie`, `pivot-demark`, `volume-poc`, `volume-vah`, `volume-val`, `volume-traded` — comma-separated; Omit for swing. The response names each method's zones and a family-aware confluence score, with the tolerance it was computed against. Methods that share a family share one budget. A requested method that contributed nothing is named, with why.","inputSchema":{"type":"object","properties":{"symbol":{"description":"The instrument symbol, e.g. ES.","type":"string"},"resolutionMinutes":{"description":"The timeframe in minutes.","type":"integer"},"lookbackBars":{"description":"How many bars of history to detect over. Omit for 500.","type":"integer","default":500},"pivotSource":{"description":"Which price on a bar a pivot is measured from: HeikinAshiBody, Body or HighLow. Omit to use this server's configured source. HeikinAshiBody smooths single-bar noise into structure and is the shipped default. Body reads open and close only, HighLow reads the raw wicks. NOTE: on a continuous intraday series, where a bar opens at the previous close, a body high ties with its neighbour's on every bar and Body can find NO pivots at all — an empty level set there is a property of the source, not a market without structure. An unknown name is an error listing the three.","type":["string","null"],"default":null},"pivotLookback":{"description":"How many bars to its LEFT a pivot must dominate; larger means fewer, more structural levels. Omit to use this server's configured lookback. The window is asymmetric: detection needs this + `pivotRightLookback` + 1 bars to find even one pivot — and the window it runs over is whatever the store holds, cut back to the contract in front, which can be far less than `lookbackBars` asked for. When that happens the answer is an EMPTY level set, not an error: compare `detection.pivotLookback` against `detectedOverBars` to tell that from a market with no structure.","type":["integer","null"],"default":null},"pivotRightLookback":{"description":"How many bars to its RIGHT a pivot must dominate — the confirmation window. Omit to use this server's configured value. It is shorter than the left one by default because the two sides answer different questions: the left asks how much history the level stood clear of, the right only has to show the extreme held. It is also the lag: the last this-many bars of the series can never produce a pivot, so the newest structure is always missing from the answer. There is no zero — a pivot judged only by the bars before it repaints as soon as the next one arrives.","type":["integer","null"],"default":null},"methods":{"description":"Which level methods to run, comma-separated: swing, session, pivot-classic, pivot-fibonacci, pivot-camarilla, pivot-woodie, pivot-demark, volume-poc, volume-vah, volume-val, volume-traded. Omit for swing. An unknown name is an error listing the known ones — never an empty level set. Session and every pivot-* method refuse when a bucket of this resolutionMinutes overhangs a session close. Volume-* methods consume the tape-derived profile for the window; they never spread a bar's volume across its range.","type":["string","null"],"default":null}},"required":["symbol","resolutionMinutes"]},"outputSchema":null,"annotations":{"title":"Get key levels","destructiveHint":null,"idempotentHint":true,"openWorldHint":null,"readOnlyHint":true},"icons":null,"_meta":null}""",
        ["get_footprint"] =
            """{"name":"get_footprint","title":"Get footprint","description":"Reads buy/sell volume by price by bar from stored footprint cells. The tape only goes forward: there is no historical footprint for a period before recording began — not slow, not expensive, ABSENT. A window before recording began is refused and names the earliest covered time; an empty answer is not a quiet market. The response reports `covered` from TapeCoverage — not the window you asked for — and `contracts` with span SingleContract naming which contract was listened to. `contracts.segments` use bar-open times from the cells (`firstBucket` / `lastBucket`), not the exclusive coverage end — that range stays on `covered`. A roll or listening hole narrows the answer to the newest contiguous run and sets `covered.narrowed`. When the live tape is not listening for that instrument the tool refuses with a sentence naming the fix — an empty answer and an absent tape must not look the same. Top-level fields are always present. `front` names the tape volume-front beside the contract Bars would fetch — `used` is `tape-volume` or `none`, never a silent prefer of the gateway. `contracts` stays the newest listening run; it is not rewritten from `front`. Keys inside `front` are omitted when that answer does not exist. A covered window whose stored tape has prints the cells do not yet reflect is projected on this read (no vendor call). If the tape still produces no cell — a roll inside the bar, or prints that do not count — the tool refuses rather than returning empty `cells`. Never truncates: an over-cap window is refused.","inputSchema":{"type":"object","properties":{"symbol":{"description":"The instrument symbol, e.g. ES.","type":"string"},"resolutionMinutes":{"description":"The bar size in minutes the cells were projected at.","type":"integer"},"fromUtc":{"description":"Window start, ISO-8601 UTC, inclusive.","type":"string","format":"date-time"},"toUtc":{"description":"Window end, ISO-8601 UTC, exclusive.","type":"string","format":"date-time"}},"required":["symbol","resolutionMinutes","fromUtc","toUtc"]},"outputSchema":null,"annotations":{"title":"Get footprint","destructiveHint":null,"idempotentHint":true,"openWorldHint":null,"readOnlyHint":true},"icons":null,"_meta":null}""",
        ["get_volume_profile"] =
            """{"name":"get_volume_profile","title":"Get volume profile","description":"Aggregates stored footprint cells into volume by price, the point of control, and the 70% value area. The tape only goes forward: there is no historical footprint for a period before recording began — not slow, not expensive, ABSENT. A window before recording began is refused and names the earliest covered time; an empty profile is not a quiet market. The response reports `covered` from TapeCoverage — not the window you asked for — and `contracts` with span SingleContract naming which contract was listened to. `contracts.segments` use bar-open times from the cells, not the exclusive coverage end. A roll or listening hole narrows the answer to the newest contiguous run and sets `covered.narrowed`. When the live tape is not listening the tool refuses with a sentence naming the fix — an empty profile and an absent tape must not look the same. Health is that instrument's tape, not another symbol's subscribe. Top-level fields are always present. `front` names the tape volume-front beside the contract Bars would fetch — `used` is `tape-volume` or `none`, never a silent prefer of the gateway. `contracts` stays the newest listening run; it is not rewritten from `front`. Keys inside `front` are omitted when that answer does not exist. A covered window whose stored tape has prints the cells do not yet reflect is projected on this read (no vendor call). If the tape still produces no cell the tool refuses rather than returning an empty profile. Never truncates: an over-cap window is refused.","inputSchema":{"type":"object","properties":{"symbol":{"description":"The instrument symbol, e.g. ES.","type":"string"},"resolutionMinutes":{"description":"The bar size in minutes the cells were projected at.","type":"integer"},"fromUtc":{"description":"Window start, ISO-8601 UTC, inclusive.","type":"string","format":"date-time"},"toUtc":{"description":"Window end, ISO-8601 UTC, exclusive.","type":"string","format":"date-time"}},"required":["symbol","resolutionMinutes","fromUtc","toUtc"]},"outputSchema":null,"annotations":{"title":"Get volume profile","destructiveHint":null,"idempotentHint":true,"openWorldHint":null,"readOnlyHint":true},"icons":null,"_meta":null}""",
        ["get_contract_roll"] =
            """{"name":"get_contract_roll","title":"Get contract roll","description":"Reports the most recent contract-roll changeover the stored tape can prove for a symbol, and the tape front at asOfUtc. There is no historical tape before recording began — a changeover from before that is ABSENT, not guessed. `front` is the same object get_footprint returns: `used` is `tape-volume` or `none`, never a silent prefer of the gateway. Keys inside `front` — including `changeover`, `gatewayContractId` and `agree` — are omitted when that answer does not exist. The gateway pick is live only; a historical asOfUtc omits `gatewayContractId` and `agree` rather than dating today's pick as if it were as-of. `contracts` is the bar-side seam around the changeover (`span` / segments) over stored bars in that window, every bar size together; it is omitted when there is no changeover to place a window around. `SingleContract` means that window has one contract — two contracts on different sizes is SpansRoll even when no single series crosses. `span` Unknown means provenance was never recorded, not that there was no roll. asOfUtc is bounded like get_market_session's atUtc.","inputSchema":{"type":"object","properties":{"symbol":{"description":"The instrument symbol, e.g. ES.","type":"string"},"asOfUtc":{"description":"The instant to evaluate, ISO-8601 UTC. Defaults to now.","type":["string","null"],"format":"date-time","default":null}},"required":["symbol"]},"outputSchema":null,"annotations":{"title":"Get contract roll","destructiveHint":null,"idempotentHint":true,"openWorldHint":null,"readOnlyHint":true},"icons":null,"_meta":null}""",
    };

    [Theory]
    [InlineData(typeof(BarTools), typeof(BarCacheService))]
    [InlineData(typeof(IndicatorTools), typeof(IndicatorCacheService))]
    [InlineData(typeof(KeyLevelTools), typeof(VolumeProfileService))]
    [InlineData(typeof(TapeTools), typeof(FootprintCacheService))]
    [InlineData(typeof(ContractRollTools), typeof(VolumeFrontReader))]
    [InlineData(typeof(SnapshotTools), typeof(IndicatorCatalogNames))]
    [InlineData(typeof(SessionBarTools), typeof(SessionBarService))]
    [InlineData(typeof(SessionIndicatorTools), typeof(SessionCatalog))]
    public void AMarketDataToolTypeFailsTheContainerBuild_WhenOneOfItsOwnDependenciesIsUnregistered(
        Type toolType,
        Type dependency)
    {
        // THE STARTUP GUARANTEE, RE-ESTABLISHED ACROSS FIVE TYPES (gh#414), and it is the reason the theory
        // names a DIFFERENT dependency per type rather than dropping one service and watching everything
        // fall over. The regression gh#391 closed was that three MarketDataTools constructor parameters were
        // OPTIONAL -- ActivatorUtilities honours a parameter's default value instead of throwing when the
        // type behind it is unregistered, so a dropped registration booted clean and every activation built
        // a throwaway collaborator whose per-scope memo started empty on every call (the on-read reprobing
        // gh#347/gh#348 exist to count). Splitting one type into five multiplies the number of constructors
        // that hole could reopen in, so each of them is driven here.
        //
        // WHAT EACH PAIR HAS TO BE is a dependency that type genuinely holds, whose absence fails the build
        // naming BOTH it and the type -- which is what the two assertions below check, and it is why they
        // assert on the message rather than on "something threw": .NET validates every registered descriptor
        // on build, so a bare throw would be satisfied by any other type failing for its own reasons.
        //
        // MOST pairs are stronger than that: the dependency is one that type ALONE takes among the family --
        // BarTools is the only one holding a BarCacheService, TapeTools the only one holding a
        // FootprintCacheService, ContractRollTools' VolumeFrontReader reaches it through TapeTools too but the
        // message names both. SessionIndicatorTools has no such dependency and cannot be given one honestly
        // (gh#501): all eight of its collaborators are shared with IndicatorTools or SessionBarTools, so its
        // row names SessionCatalog, which SessionBarTools takes as well. Dropping it fails the build naming
        // both types, so the case still pins that this constructor is validated at build time and not at call
        // time -- it just does not, on its own, distinguish which of the two consumers was hurt.
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Configuration.AddInMemoryCollection(_baseSettings);

        Program.ConfigureServices(builder, new McpOptions { Transport = McpTransport.Stdio });

        ServiceDescriptor descriptor = builder.Services.Single(d => d.ServiceType == dependency);
        builder.Services.Remove(descriptor);

        // Caught by hand rather than through FluentAssertions' exception-assertion API: the provider
        // validates every registered descriptor on build, so more than one tool type fails validation here,
        // and an assertion built to expect exactly one thrown exception cannot tell which of them it was
        // handed.
        Exception? thrown = null;
        try
        {
            using ServiceProvider unused = builder.Services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
        }
        catch (Exception ex)
        {
            thrown = ex;
        }

        thrown.Should().NotBeNull(
            "a dropped " + dependency.Name + " registration must fail the CONTAINER BUILD, not silently "
            + "hand " + toolType.Name + " a fallback instance at call time");

        string message = thrown!.ToString();
        message.Should().Contain(dependency.Name, "the failure has to name the service that is missing");
        message.Should().Contain(
            toolType.Name,
            toolType.Name + " takes " + dependency.Name + " and must be named by the failure -- otherwise "
            + "this case is green on some OTHER type's validation error and pins nothing about this one");
    }

    [Fact]
    public void TheRebuildVerbCanBeResolved()
    {
        // IndicatorRebuilder is reachable from NO tool, so the theory above -- which walks the tool types --
        // does not cover it, and `GetRequiredService<IndicatorRebuilder>()` in the rebuild-indicators branch
        // is verified by nothing else. That branch runs before the store is even migrated and exits the
        // process, so a missing registration surfaces as an operator running a repair command and getting a
        // container exception instead. This verb has already shipped once having never been executed
        // anywhere (gh#37); leaving its one resolution unchecked would repeat exactly that.
        using ServiceProvider provider =
            Build(new Dictionary<string, string?>(), new McpOptions { Transport = McpTransport.Stdio });
        using IServiceScope scope = provider.CreateScope();

        Func<object> resolve = () => scope.ServiceProvider.GetRequiredService<IndicatorRebuilder>();

        resolve.Should().NotThrow();
    }

    [Fact]
    public void TheReselectVerbCanBeResolved()
    {
        // BarReselector is reachable from NO tool, so the theory above -- which walks the tool types -- does
        // not cover it, and `GetRequiredService<BarReselector>()` in the reselect-bars branch is verified by
        // nothing else. That branch migrates the store and exits the process, so a missing registration
        // surfaces as an operator running a repair command over months of history and getting a container
        // exception instead. This repository has already shipped a verb that had never been executed
        // anywhere (gh#37); leaving its one resolution unchecked would repeat exactly that.
        using ServiceProvider provider =
            Build(new Dictionary<string, string?>(), new McpOptions { Transport = McpTransport.Stdio });
        using IServiceScope scope = provider.CreateScope();

        Func<object> resolve = () => scope.ServiceProvider.GetRequiredService<BarReselector>();

        resolve.Should().NotThrow();
    }

    [Fact]
    public void TheFootprintRebuildVerbCanBeResolved()
    {
        // FootprintProjector is reached from get_footprint / get_volume_profile via
        // FootprintCacheService (gh#366). Leaving its registration unchecked would ship a
        // read that dies the first time anyone asks the container for it — the same hole
        // IndicatorRebuilder had before this test existed.
        using ServiceProvider provider =
            Build(new Dictionary<string, string?>(), new McpOptions { Transport = McpTransport.Stdio });
        using IServiceScope scope = provider.CreateScope();

        Func<object> resolve = () => scope.ServiceProvider.GetRequiredService<FootprintProjector>();

        resolve.Should().NotThrow();
    }

    [Fact]
    public void TheFootprintCacheServiceCanBeResolved()
    {
        // The on-read path (gh#366) is what actually calls FootprintProjector. Leaving this
        // registration unchecked would ship a tool that dies the first time a covered tape
        // has no cells.
        using ServiceProvider provider =
            Build(new Dictionary<string, string?>(), new McpOptions { Transport = McpTransport.Stdio });
        using IServiceScope scope = provider.CreateScope();

        Func<object> resolve = () => scope.ServiceProvider.GetRequiredService<FootprintCacheService>();

        resolve.Should().NotThrow();
    }

    [Fact]
    public void TheVolumeProfileServiceCanBeResolved()
    {
        // VolumeProfileService is reached from get_footprint / get_volume_profile (gh#222).
        // Leaving its registration unchecked would ship a reader that dies the first time
        // anyone asks the container for it — the same hole FootprintProjector had this test close.
        using ServiceProvider provider =
            Build(new Dictionary<string, string?>(), new McpOptions { Transport = McpTransport.Stdio });
        using IServiceScope scope = provider.CreateScope();

        Func<object> resolve = () => scope.ServiceProvider.GetRequiredService<VolumeProfileService>();

        resolve.Should().NotThrow();
    }

    [Fact]
    public void TheTapeVolumeFrontServiceCanBeResolved()
    {
        // gh#219 stops at the service so the footprint tools' health block (gh#218) is not
        // a merge collision. Leaving the registration unchecked would ship a reader that
        // dies the first time anyone asks the container for it.
        using ServiceProvider provider =
            Build(new Dictionary<string, string?>(), new McpOptions { Transport = McpTransport.Stdio });
        using IServiceScope scope = provider.CreateScope();

        Func<object> resolve = () => scope.ServiceProvider.GetRequiredService<TapeVolumeFrontService>();

        resolve.Should().NotThrow();
    }

    [Fact]
    public void TheSessionBarServiceCanBeResolved()
    {
        // SessionBarTools reaches SessionBarService now (gh#500), so the theories above DO cover it: the
        // request-scope walk activates the tool type, and the unregistered-dependency theory drops this very
        // service and demands the container build fail. This stays as the DIRECT check. Both of those reach
        // the reader through a tool constructor, so a slice that moved the service behind a different seam
        // would take them with it and leave nothing asking the container for it at all -- which is exactly
        // the hole IndicatorRebuilder shipped through, and it costs one Fact to keep shut.
        using ServiceProvider provider =
            Build(new Dictionary<string, string?>(), new McpOptions { Transport = McpTransport.Stdio });
        using IServiceScope scope = provider.CreateScope();

        Func<object> resolve = () => scope.ServiceProvider.GetRequiredService<SessionBarService>();

        resolve.Should().NotThrow();
    }

    [Fact]
    public void TheSessionCatalogCanBeResolved()
    {
        // A singleton, so it resolves from the root rather than a scope -- and resolving it is the whole
        // point: SessionCatalog validates the configured definitions in its CONSTRUCTOR, and a singleton
        // resolves lazily, so a catalogue nothing ever asks for is a refusal that never happens.
        using ServiceProvider provider =
            Build(new Dictionary<string, string?>(), new McpOptions { Transport = McpTransport.Stdio });

        Func<object> resolve = () => provider.GetRequiredService<SessionCatalog>();

        resolve.Should().NotThrow();
    }

    [Fact]
    public void TheKeyLevelDetectionSection_Binds_IncludingItsSource()
    {
        // Bound from configuration rather than constructed, which is the whole of gh#244 on this side of the
        // seam: the tool used to build `new KeyLevelOptions()` and never read a setting at all.
        Dictionary<string, string?> configured = new()
        {
            ["KeyLevels:Source"] = "HighLow",
            ["KeyLevels:PivotLookback"] = "9",
            ["KeyLevels:ZoneAtrMultiple"] = "1.25",
            ["KeyLevels:MinSignificance"] = "0.75",
            ["KeyLevels:PivotRightLookback"] = "4",
            ["KeyLevels:MaxZoneWidthPercent"] = "1.75",
            ["KeyLevels:MaxLevels"] = "6",
        };

        using ServiceProvider provider = Build(configured, new McpOptions { Transport = McpTransport.Stdio });

        KeyLevelDetectionOptions options =
            provider.GetRequiredService<IOptions<KeyLevelDetectionOptions>>().Value;

        // Every one of the seven is a DIFFERENT value from its shipped default, so a field the projection
        // forgot to carry reads back as the default and this goes red naming it. Four of them agreeing with
        // the defaults would have hidden a dropped field in the three that did not.
        options.Defaults().Should().Be(
            new KeyLevelOptions(9, PivotSource.HighLow, 1.25m, 0.75m, 4, 1.75m, 6));
    }

    [Fact]
    public void TheShippedDetectionDefaults_AreTheOnesTheDocumentedSurfacePromises()
    {
        // An absent section binds the property initialisers, and those are the numbers `.env.example`,
        // compose and the tool catalogue all state. Heikin-Ashi stays the default: it smooths single-bar
        // noise into structure, which is the reason it has carried since the pipeline existed. The window is
        // 20 left and 15 right, and the caps 2.5% and 12 -- Bjorgum's calibration, adopted whole by gh#245,
        // which moved the lookback off 5 and is a BREAKING change to what an omitted argument produces.
        using ServiceProvider provider =
            Build(new Dictionary<string, string?>(), new McpOptions { Transport = McpTransport.Stdio });

        provider.GetRequiredService<IOptions<KeyLevelDetectionOptions>>().Value.Defaults()
            .Should().Be(new KeyLevelOptions(20, PivotSource.HeikinAshiBody, 0.5m, 0.5m, 15, 2.5m, 12));
    }

    [Fact]
    public void TheHost_RefusesToStart_WhenAnAdditionalMacdSlowPeriodIsNotAboveTheFastLength()
    {
        // MACD's fast length is fixed at 12 (IndicatorOptions' remarks), so a configured additional slow
        // period of 12 or less would throw the first time IndicatorCatalog tried to build a MacdLineIndicator
        // from it -- a boot-time refusal that names the setting is the alternative to a first-call crash.
        Dictionary<string, string?> configured = new()
        {
            ["Indicators:AdditionalMacdSlowPeriods"] = "12",
        };

        Action start = () =>
        {
            using ServiceProvider provider = Build(configured, new McpOptions { Transport = McpTransport.Stdio });
            _ = provider.GetRequiredService<IOptions<IndicatorOptions>>().Value;
        };

        start.Should().Throw<OptionsValidationException>()
            .WithMessage("*Indicators__AdditionalMacdSlowPeriods*");
    }
}
