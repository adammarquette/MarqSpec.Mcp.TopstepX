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
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

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

    [Theory]
    [InlineData(typeof(ReferenceTools))]
    [InlineData(typeof(MarketDataTools))]
    [InlineData(typeof(AccountTools))]
    [InlineData(typeof(SnapshotTools))]
    [InlineData(typeof(ObservationTools))]
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
    public void TheFootprintRebuildVerbCanBeResolved()
    {
        // FootprintProjector is reachable from NO tool yet (gh#222 is the surface). Leaving its
        // registration unchecked would ship a verb that dies the first time anyone asks the
        // container for it — the same hole IndicatorRebuilder had before this test existed.
        using ServiceProvider provider =
            Build(new Dictionary<string, string?>(), new McpOptions { Transport = McpTransport.Stdio });
        using IServiceScope scope = provider.CreateScope();

        Func<object> resolve = () => scope.ServiceProvider.GetRequiredService<FootprintProjector>();

        resolve.Should().NotThrow();
    }

    [Fact]
    public void TheVolumeProfileServiceCanBeResolved()
    {
        // VolumeProfileService is reachable from NO tool yet (gh#222 is the surface). Leaving
        // its registration unchecked would ship a reader that dies the first time anyone asks
        // the container for it — the same hole FootprintProjector had this test close.
        using ServiceProvider provider =
            Build(new Dictionary<string, string?>(), new McpOptions { Transport = McpTransport.Stdio });
        using IServiceScope scope = provider.CreateScope();

        Func<object> resolve = () => scope.ServiceProvider.GetRequiredService<VolumeProfileService>();

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

    [Theory]
    [InlineData("Unknown")]
    [InlineData("0")]
    public void AnUnsetConfiguredPivotSource_FailsValidation_RatherThanBootingOnIt(string configuredSource)
    {
        // Unknown = 0 is what a mistyped or absent value binds to, so honouring it picks a price series by
        // accident -- and `KeyLevels.PivotPrices` reads anything it does not recognise as Heikin-Ashi, so
        // the server would answer every level call from a source nobody chose, with nothing to see. The rule
        // is an IValidatableObject on the options type, so it travels with the value rather than living in a
        // lambda at this composition root that a second binder could miss.
        Dictionary<string, string?> configured = new() { ["KeyLevels:Source"] = configuredSource };

        using ServiceProvider provider = Build(configured, new McpOptions { Transport = McpTransport.Stdio });

        Func<KeyLevelDetectionOptions> read =
            () => provider.GetRequiredService<IOptions<KeyLevelDetectionOptions>>().Value;

        read.Should().Throw<OptionsValidationException>()
            .WithMessage("*HeikinAshiBody, Body, HighLow*");
    }
}
