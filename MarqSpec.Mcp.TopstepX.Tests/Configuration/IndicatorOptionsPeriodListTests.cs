using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.Tests.Configuration;

/// <summary>
/// Binding and validating <see cref="IndicatorOptions"/>'s additional-period lists — the operator-configured
/// EMA 10/13/24/48/200-style extras a caller later selects among with <c>period</c>, on top of each
/// indicator's unchanged singular primary.
/// </summary>
public sealed class IndicatorOptionsPeriodListTests
{
    /// <summary>An absent list key leaves the accessor holding only the primary period.</summary>
    [Fact]
    public void AdditionalPeriods_AreEmpty_WhenTheKeyIsAbsent()
    {
        IndicatorOptions options = Bind([]);

        options.EmaPeriods().Should().Equal(20);
    }

    /// <summary>
    /// Tokens are trimmed and kept in the order configured, appended after the primary — never re-sorted,
    /// since the operator's order is the order a caller will see in the "configured periods" error listing.
    /// </summary>
    [Fact]
    public void AdditionalPeriods_ParseACommaSeparatedList_TrimmedAndInConfiguredOrder()
    {
        IndicatorOptions options = Bind(new Dictionary<string, string?>
        {
            ["Indicators:AdditionalEmaPeriods"] = " 10, 13 ,200",
        });

        options.EmaPeriods().Should().Equal(20, 10, 13, 200);
    }

    /// <summary>A token that does not parse as an integer is refused, naming the offending key.</summary>
    [Fact]
    public void AdditionalPeriods_RefuseANonInteger_NamingTheKey()
    {
        Action bind = () => Bind(new Dictionary<string, string?>
        {
            ["Indicators:AdditionalEmaPeriods"] = "10,thirteen",
        });

        bind.Should().Throw<OptionsValidationException>()
            .WithMessage("*Indicators__AdditionalEmaPeriods*")
            .WithMessage("*thirteen*");
    }

    /// <summary>
    /// MACD's slow length must exceed the fixed fast length of 12 — 12 itself is refused, 13 is the floor.
    /// </summary>
    [Fact]
    public void AdditionalMacdSlowPeriods_RefuseTwelve_AndAcceptThirteen()
    {
        Action bindTwelve = () => Bind(new Dictionary<string, string?>
        {
            ["Indicators:AdditionalMacdSlowPeriods"] = "12",
        });

        bindTwelve.Should().Throw<OptionsValidationException>()
            .WithMessage("*Indicators__AdditionalMacdSlowPeriods*");

        IndicatorOptions accepted = Bind(new Dictionary<string, string?>
        {
            ["Indicators:AdditionalMacdSlowPeriods"] = "13",
        });

        accepted.MacdSlowPeriods().Should().Equal(26, 13);
    }

    /// <summary>The Bollinger window floor is 2 — 1 is refused.</summary>
    [Fact]
    public void AdditionalBollingerPeriods_RefuseOne()
    {
        Action bind = () => Bind(new Dictionary<string, string?>
        {
            ["Indicators:AdditionalBollingerPeriods"] = "1",
        });

        bind.Should().Throw<OptionsValidationException>()
            .WithMessage("*Indicators__AdditionalBollingerPeriods*");
    }

    /// <summary>A value above the shared 1,000 ceiling is refused.</summary>
    [Fact]
    public void AdditionalPeriods_RefuseAValueAboveOneThousand()
    {
        Action bind = () => Bind(new Dictionary<string, string?>
        {
            ["Indicators:AdditionalEmaPeriods"] = "1001",
        });

        bind.Should().Throw<OptionsValidationException>()
            .WithMessage("*Indicators__AdditionalEmaPeriods*");
    }

    /// <summary>
    /// A value repeated within one list is refused — the projector cannot write one key twice in a single
    /// <c>ON CONFLICT</c> statement.
    /// </summary>
    [Fact]
    public void AdditionalPeriods_RefuseARepeatedValue()
    {
        Action bind = () => Bind(new Dictionary<string, string?>
        {
            ["Indicators:AdditionalEmaPeriods"] = "10,10",
        });

        bind.Should().Throw<OptionsValidationException>()
            .WithMessage("*Indicators__AdditionalEmaPeriods*");
    }

    /// <summary>
    /// A value equal to the indicator's primary is refused rather than silently dropped, which would mislead
    /// the operator about what is actually configured.
    /// </summary>
    [Fact]
    public void AdditionalPeriods_RefuseAValueEqualToThePrimary()
    {
        Action bind = () => Bind(new Dictionary<string, string?>
        {
            ["Indicators:AdditionalEmaPeriods"] = "20",
        });

        bind.Should().Throw<OptionsValidationException>()
            .WithMessage("*Indicators__AdditionalEmaPeriods*");
    }

    /// <summary>
    /// <see cref="IndicatorOptions.RollingVwapPeriod"/> is the primary period of the rolling-VWAP indicator a
    /// later task adds, and defaults to 20 like the rest of this class's windows.
    /// </summary>
    [Fact]
    public void RollingVwapPeriod_DefaultsToTwenty()
    {
        IndicatorOptions options = Bind([]);

        options.RollingVwapPeriod.Should().Be(20);
        options.RollingVwapPeriods().Should().Equal(20);
    }

    /// <summary>Binds through the real composition root, so this measures what the deployed server does.</summary>
    /// <param name="configured">The <c>Indicators:*</c> settings to add.</param>
    /// <returns>The bound and validated options.</returns>
    private static IndicatorOptions Bind(Dictionary<string, string?> configured)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();

        // Same reasoning as KeyLevelSourceBindingTests.BindWith: a shell exporting an Indicators__* variable
        // would otherwise leak into the "absent key" cases below.
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

        return provider.GetRequiredService<IOptions<IndicatorOptions>>().Value;
    }
}
