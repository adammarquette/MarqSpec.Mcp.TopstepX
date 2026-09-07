using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.Tests.Configuration;

/// <summary>
/// What <c>MarketData__Sessions__&lt;name&gt;__*</c> binds to, and what startup says when it does not describe a
/// servable session.
/// </summary>
/// <remarks>
/// <para>
/// The dictionary-by-name shape is <c>KeyLevels__Weights__&lt;name&gt;</c>'s, and the refusal is on the same
/// terms: <see cref="MarketDataOptions.Validate"/> is an <c>IValidatableObject</c> on the type, so
/// <c>ValidateDataAnnotations().ValidateOnStart()</c> — already wired at the composition root — turns a bad
/// window into a boot failure that names the key and the rule.
/// </para>
/// <para>
/// A boundary that is not the one the operator typed is the failure this whole slice exists to refuse: a
/// session bar built from the wrong window is a wrong number, not a rough one (ADR-0022). So the window is
/// parsed EXACTLY — <c>8:30-15:00</c> and <c>08:30:00-15:00:00</c> are refused rather than read charitably.
/// </para>
/// </remarks>
public sealed class SessionOptionsBindingTests
{
    /// <summary>
    /// A configured name that matches a shipped one REPLACES it rather than being added beside it — two
    /// definitions of <c>rth</c> would be two storage keys the store cannot tell apart.
    /// </summary>
    [Fact]
    public void Sessions_BindFromDoubleUnderscoreKeys()
    {
        MarketDataOptions options = BindWith(builder => builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["MarketData:Sessions:rth:Window"] = "09:00-15:00",
                ["MarketData:Sessions:rth:BaseResolutionMinutes"] = "15",
            }));

        IReadOnlyList<SessionDefinition> sessions = options.SessionList();

        sessions.Should().HaveCount(4);
        sessions.Should().ContainSingle(session => session.Name == "rth")
            .Which.Should().Be(new SessionDefinition("rth", new TimeOnly(9, 0), new TimeOnly(15, 0), 15));
    }

    /// <summary>
    /// Leaving all eight keys out ships the four shipped sessions, unreordered and whole.
    /// </summary>
    /// <remarks>
    /// Each of the four fields is asserted rather than the list compared by reference, so a silently reordered
    /// or half-copied overlay reddens here instead of being discovered as a session bar written under the
    /// wrong definition.
    /// </remarks>
    [Fact]
    public void AnUnsetSection_YieldsTheFourDefaults()
    {
        MarketDataOptions options = BindWith(builder => { });

        IReadOnlyList<SessionDefinition> sessions = options.SessionList();

        sessions.Should().HaveCount(SessionDefinition.Defaults.Count);
        for (int i = 0; i < SessionDefinition.Defaults.Count; i++)
        {
            SessionDefinition expected = SessionDefinition.Defaults[i];

            sessions[i].Name.Should().Be(expected.Name);
            sessions[i].StartCentral.Should().Be(expected.StartCentral);
            sessions[i].EndCentral.Should().Be(expected.EndCentral);
            sessions[i].BaseResolutionMinutes.Should().Be(expected.BaseResolutionMinutes);
        }
    }

    /// <summary>
    /// A configured session that breaks a rule fails startup naming BOTH the key an operator would edit and
    /// the rule it broke.
    /// </summary>
    /// <param name="window">The configured window.</param>
    /// <param name="baseMinutes">The configured base resolution.</param>
    /// <param name="key">The key the refusal must name.</param>
    /// <param name="rule">A phrase from the rule the refusal must state.</param>
    [Theory]
    [InlineData("08:45-15:00", "30", "MarketData__Sessions__rth__Window", "land on the stored UTC bucket grid")]
    [InlineData("0830-1500", "30", "MarketData__Sessions__rth__Window", "HH:mm-HH:mm")]
    [InlineData("8:30-15:00", "30", "MarketData__Sessions__rth__Window", "HH:mm-HH:mm")]
    [InlineData("08:30:00-15:00:00", "30", "MarketData__Sessions__rth__Window", "HH:mm-HH:mm")]
    [InlineData("08:30-15:00", "45", "MarketData__Sessions__rth__BaseResolutionMinutes", "divide 60")]
    public void AConfiguredWindowOffTheGrid_FailsStartup(
        string window,
        string baseMinutes,
        string key,
        string rule)
    {
        Action start = () => BindWith(builder => builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["MarketData:Sessions:rth:Window"] = window,
                ["MarketData:Sessions:rth:BaseResolutionMinutes"] = baseMinutes,
            }));

        start.Should().Throw<OptionsValidationException>()
            .WithMessage("*" + key + "*")
            .WithMessage("*" + rule + "*");
    }

    /// <summary>
    /// A session name is a storage key, so the dictionary key itself is a rule and startup states it.
    /// </summary>
    /// <remarks>
    /// The refusal names the entry rather than being asserted down to a property suffix: an uppercase name is
    /// a fault of the key, not of either value under it, and the message
    /// <see cref="SessionWindows.Validate"/> supplies says which rule it broke.
    /// </remarks>
    [Fact]
    public void AConfiguredSessionNameThatIsNotAStorageKey_FailsStartup()
    {
        Action start = () => BindWith(builder => builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["MarketData:Sessions:RTH:Window"] = "08:30-15:00",
                ["MarketData:Sessions:RTH:BaseResolutionMinutes"] = "30",
            }));

        start.Should().Throw<OptionsValidationException>()
            .WithMessage("*MarketData__Sessions__RTH*")
            .WithMessage("*storage key*");
    }

    /// <summary>
    /// A malformed <c>SessionCloseCentral</c> stays what it is today — a failure at the calendar singleton, not
    /// a session-validation failure that would rename the setting an operator has to fix.
    /// </summary>
    [Fact]
    public void AMalformedSessionClose_DoesNotBecomeASessionRefusal()
    {
        Action start = () => BindWith(builder => builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["MarketData:SessionCloseCentral"] = "not-a-time" }));

        start.Should().NotThrow();
    }

    /// <summary>
    /// Binds through the real composition root, so this measures what the deployed server does rather than a
    /// hand-rolled binder. <paramref name="configure"/> adds whatever configuration the case needs; adding
    /// nothing is the absent case.
    /// </summary>
    /// <param name="configure">Adds the case's configuration to the builder.</param>
    /// <returns>The bound and validated options.</returns>
    private static MarketDataOptions BindWith(Action<WebApplicationBuilder> configure)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();

        // CreateBuilder() reads the process environment, and a shell with MarketData__Sessions__* exported --
        // a copied `.env`, sourced -- would otherwise reach the absent case and redden it for something that
        // is not a defect. The unprefixed environment provider is removed rather than overridden, leaving the
        // prefixed host-settings ones alone. Copied from KeyLevelSourceBindingTests, where it was measured.
        IConfigurationBuilder sources = builder.Configuration;
        foreach (EnvironmentVariablesConfigurationSource ambient in sources.Sources
            .OfType<EnvironmentVariablesConfigurationSource>()
            .Where(source => string.IsNullOrEmpty(source.Prefix))
            .ToList())
        {
            sources.Sources.Remove(ambient);
        }

        configure(builder);

        Program.ConfigureServices(builder, new McpOptions { Transport = McpTransport.Stdio });

        using ServiceProvider provider = builder.Services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        return provider.GetRequiredService<IOptions<MarketDataOptions>>().Value;
    }
}
