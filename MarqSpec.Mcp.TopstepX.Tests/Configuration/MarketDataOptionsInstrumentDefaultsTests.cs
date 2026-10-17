using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.MarketData;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.Tests.Configuration;

/// <summary>
/// The shipped <c>MarketData__Instruments</c> default — the list a server serves when nobody sets the key
/// (gh#694).
/// </summary>
/// <remarks>
/// Out of the box this used to be ES and NQ only, so MES / MNQ / MGC / MCL returned "Unknown instrument"
/// until an operator copied a longer list by hand. The three surfaces that state the default
/// (<see cref="MarketDataOptions"/>, <c>.env.example</c>, compose) must stay the same string: a copied
/// <c>.env</c> that listed more than the catalogue default used to change the served universe from the
/// one the code ships.
/// </remarks>
public sealed class MarketDataOptionsInstrumentDefaultsTests
{
    /// <summary>
    /// Equity-index and metals/energy micros sit beside ES and NQ. The order is the configured order
    /// <see cref="InstrumentRegistry"/> serves.
    /// </summary>
    private const string ShippedDefault = "ES,NQ,MES,MNQ,MGC,MCL";

    /// <summary>The property initialiser is what an absent key binds to.</summary>
    [Fact]
    public void ThePropertyInitialiser_IsTheEquityIndexAndItsMicros()
    {
        new MarketDataOptions().Instruments.Should().Be(ShippedDefault);
        new MarketDataOptions().InstrumentList().Should().Equal("ES", "NQ", "MES", "MNQ", "MGC", "MCL");
    }

    /// <summary>
    /// An absent key through the composition root yields the same list — not an empty one, and not a
    /// leftover from a sourced <c>.env</c>.
    /// </summary>
    [Fact]
    public void AnUnsetInstrumentsKey_BindsTheShippedDefault()
    {
        MarketDataOptions options = Bind([]);

        options.Instruments.Should().Be(ShippedDefault);
        options.InstrumentList().Should().Equal("ES", "NQ", "MES", "MNQ", "MGC", "MCL");
    }

    /// <summary>
    /// Every default symbol is one <see cref="InstrumentRegistry"/> already has facts for. A default that
    /// named an unknown product would fail startup, which is the safe direction — but it would also mean
    /// the shipped list could not boot.
    /// </summary>
    [Fact]
    public void TheShippedDefault_IsAServableRegistry()
    {
        InstrumentRegistry registry = new(Options.Create(new MarketDataOptions()));

        registry.Instruments.Select(instrument => instrument.Symbol)
            .Should().Equal("ES", "NQ", "MES", "MNQ", "MGC", "MCL");
    }

    /// <summary>
    /// <c>.env.example</c> and compose's <c>${VAR:-}</c> fallback are the other two copies of this default.
    /// A copied <c>.env</c> that listed more than the catalogue used to change the served universe (the
    /// comment on the key records that). The three must stay one string.
    /// </summary>
    [Fact]
    public void EnvExampleAndCompose_StateTheSameShippedDefault()
    {
        string root = FindRepoRoot();
        string envExample = File.ReadAllText(Path.Combine(root, ".env.example"));
        string compose = File.ReadAllText(Path.Combine(root, "docker-compose.yml"));

        envExample.Should().Contain("MarketData__Instruments=" + ShippedDefault);
        compose.Should().Contain("${MarketData__Instruments:-" + ShippedDefault + "}");
    }

    /// <summary>Binds through the real composition root, so this measures what the deployed server does.</summary>
    /// <param name="configured">The <c>MarketData:*</c> settings to add.</param>
    /// <returns>The bound and validated options.</returns>
    private static MarketDataOptions Bind(Dictionary<string, string?> configured)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();

        // CreateBuilder() reads the process environment. A shell with MarketData__Instruments exported —
        // a copied `.env`, sourced — would otherwise reach the absent case and redden it for something
        // that is not a defect. Same strip as SessionOptionsBindingTests / StoreOptionsTests.
        IConfigurationBuilder sources = builder.Configuration;
        foreach (EnvironmentVariablesConfigurationSource ambient in sources.Sources
            .OfType<EnvironmentVariablesConfigurationSource>()
            .Where(source => string.IsNullOrEmpty(source.Prefix))
            .ToList())
        {
            sources.Sources.Remove(ambient);
        }

        builder.Configuration.AddInMemoryCollection(configured);

        Program.ConfigureServices(builder, new McpOptions { Transport = McpTransport.Stdio });

        using ServiceProvider provider = builder.Services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        return provider.GetRequiredService<IOptions<MarketDataOptions>>().Value;
    }

    /// <summary>
    /// Walks up from the test assembly to the solution root. The files under test are not copied to
    /// the output directory; they live next to the slnx.
    /// </summary>
    private static string FindRepoRoot()
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MarqSpec.Mcp.TopstepX.slnx")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not find MarqSpec.Mcp.TopstepX.slnx above " + AppContext.BaseDirectory);
    }
}
