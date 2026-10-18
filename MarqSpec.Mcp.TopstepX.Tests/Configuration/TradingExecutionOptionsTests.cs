using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.Tests.Configuration;

/// <summary>
/// Binding and validating <see cref="TradingExecutionOptions"/> — master enable, allowlist, caps and denylist
/// (gh#708, ADR-0024 practice-only v1 configuration).
/// </summary>
public sealed class TradingExecutionOptionsTests
{
    /// <summary>Master enable is off when the key is absent.</summary>
    [Fact]
    public void Enabled_DefaultsToFalse_WhenTheKeyIsAbsent()
    {
        Bind([]).Enabled.Should().BeFalse();
    }

    /// <summary>The shipped caps are conservative defaults an operator can widen.</summary>
    [Fact]
    public void ContractCaps_DefaultToConservativeValues()
    {
        TradingExecutionOptions options = Bind([]);

        options.MaxContractsPerOrder.Should().Be(5);
        options.MaxContractsPerDay.Should().Be(50);
    }

    /// <summary>An allowlist parses as positive integers in configured order.</summary>
    [Fact]
    public void AllowedAccountIds_ParseCommaSeparatedPositiveIntegers()
    {
        TradingExecutionOptions options = Bind(new Dictionary<string, string?>
        {
            ["Trading:AllowedAccountIds"] = " 123, 456 ",
        });

        options.AllowedAccountIdList().Should().Equal(123, 456);
        options.IsAllowlistConfigured.Should().BeTrue();
    }

    /// <summary>A duplicate account id is refused at startup.</summary>
    [Fact]
    public void AllowedAccountIds_RefuseADuplicate()
    {
        Action bind = () => Bind(new Dictionary<string, string?>
        {
            ["Trading:AllowedAccountIds"] = "123,123",
        });

        bind.Should().Throw<OptionsValidationException>()
            .WithMessage("*Trading__AllowedAccountIds*")
            .WithMessage("*123*");
    }

    /// <summary>Denied symbols are normalised to upper-case.</summary>
    [Fact]
    public void DeniedSymbols_NormaliseToUpperCase()
    {
        TradingExecutionOptions options = Bind(new Dictionary<string, string?>
        {
            ["Trading:DeniedSymbols"] = " es, mes ",
        });

        options.DeniedSymbolSet().Should().BeEquivalentTo(["ES", "MES"]);
    }

    /// <summary>A blank symbol token is refused.</summary>
    [Fact]
    public void DeniedSymbols_RefuseABlankToken()
    {
        Action bind = () => Bind(new Dictionary<string, string?>
        {
            ["Trading:DeniedSymbols"] = "ES,,MES",
        });

        bind.Should().Throw<OptionsValidationException>()
            .WithMessage("*Trading__DeniedSymbols*");
    }

    /// <summary>Binds through the real composition root, so this measures what the deployed server does.</summary>
    /// <param name="configured">The <c>Trading:*</c> settings to add.</param>
    /// <returns>The bound and validated options.</returns>
    private static TradingExecutionOptions Bind(Dictionary<string, string?> configured)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();

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

        return provider.GetRequiredService<IOptions<TradingExecutionOptions>>().Value;
    }
}
