using System.Text;
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
/// Where <c>KeyLevels__Source</c> is actually decided — the configuration binder, or
/// <see cref="KeyLevelDetectionOptions.Validate"/> — and what each one says when it refuses.
/// </summary>
/// <remarks>
/// <para>
/// <b>The file used to assert, three times, that an unset or mistyped value binds to
/// <see cref="PivotSource.Unknown"/> and is refused by <c>Validate</c>. Neither half was true</b> (gh#459),
/// and the sentences were believed: gh#444 was written from them and named "unset <c>KeyLevels__Source</c>
/// fails startup by design" as a trap an operator meets, which only running it disproved.
/// </para>
/// <para>
/// The binder splits on whether <c>Enum.Parse</c> can <b>read</b> the value, not on whether it means
/// anything. An <b>absent</b> key never reaches <c>Unknown</c> at all — the binder leaves a bound property
/// alone when its key is missing, so the property initializer's <see cref="PivotSource.HeikinAshiBody"/>
/// stands. A value it <b>cannot read</b> — a name outside the vocabulary, an empty string — never reaches
/// <c>Validate</c>: the binder throws first. A value it <b>can</b> read binds as whatever it makes of it, and
/// only <c>IsServable</c> stands behind that: <c>Unknown</c>, any numeral, defined or not, and a JSON
/// <c>null</c> arrive at <c>Validate</c> and are refused there — and a <b>comma-separated list</b> is OR-ed
/// together, so <c>HeikinAshiBody,Body</c> binds as <see cref="PivotSource.HighLow"/> and boots. Three
/// corrections of the original claim each enumerated the outcomes and each missed one; these tests pin the
/// mechanism instead.
/// </para>
/// <para>
/// These pin the boundary rather than the wording of any one sentence, because the defect was a claim about
/// <i>which component refuses</i>. The last test is the exception: it reads the message, because a message
/// that explains the mechanism wrongly is what made this a card instead of a comment fix.
/// </para>
/// </remarks>
public sealed class KeyLevelSourceBindingTests
{
    /// <summary>An absent key leaves the initializer's default standing; nothing is refused.</summary>
    [Fact]
    public void Source_IsTheDefault_AndIsNotRefused_WhenTheKeyIsAbsent()
    {
        KeyLevelDetectionOptions options = BindWith(builder => { });

        options.Source.Should().Be(PivotSource.HeikinAshiBody);
    }

    /// <summary>
    /// Whatever the binder reads as a known source is accepted, however it was spelled: a name in any case,
    /// a signed numeral — and a <b>comma-separated list</b>, which <c>Enum.Parse</c> ORs together without
    /// asking whether the enum is <c>[Flags]</c>, so two sources named together bind as a third. That last
    /// row is accepted and known, not endorsed: the server boots on it and serves from a source nobody
    /// named, and the sentence in the option's remarks that says so is pinned here (gh#468).
    /// </summary>
    /// <param name="configured">The configured value.</param>
    /// <param name="bound">What it binds to.</param>
    [Theory]
    [InlineData("HighLow", PivotSource.HighLow)]
    [InlineData("highlow", PivotSource.HighLow)]
    [InlineData("+2", PivotSource.Body)]
    [InlineData("HeikinAshiBody,Body", PivotSource.HighLow)]
    public void Source_IsAccepted_WhenWhatTheBinderReadsIsAKnownSource_ACommaListIncluded(
        string configured,
        PivotSource bound)
    {
        KeyLevelDetectionOptions options = Bind(configured);

        options.Source.Should().Be(bound);
    }

    /// <summary>
    /// A name outside the vocabulary fails in the binder, before <c>Validate</c> runs — which is why the
    /// friendly message cannot be what an operator sees for a typo. An empty string is a bad name, not an
    /// absent key.
    /// </summary>
    /// <param name="configured">The configured value.</param>
    [Theory]
    [InlineData("Bogus")]
    [InlineData("")]
    public void Binding_Fails_BeforeValidateRuns_WhenTheValueIsANameOutsideTheVocabulary(string configured)
    {
        Action bind = () => Bind(configured);

        bind.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*KeyLevels:Source*")
            .WithInnerException<FormatException>();
    }

    /// <summary>
    /// What the binder lets through and <c>Validate</c> refuses: <c>Unknown</c> by name, padded or not, and
    /// any numeral outside the enum — <c>0</c> binds as <c>Unknown</c>, <c>99</c> and <c>-1</c> as values
    /// the enum does not define. The rendered value pins that the numeral arrived as itself rather than
    /// being coerced to something in range.
    /// </summary>
    /// <param name="configured">The configured value.</param>
    /// <param name="rendered">How the refusal names it, which is how it bound.</param>
    [Theory]
    [InlineData("Unknown", "Unknown")]
    [InlineData(" Unknown ", "Unknown")]
    [InlineData("0", "Unknown")]
    [InlineData("99", "99")]
    [InlineData("-1", "-1")]
    public void Validate_RefusesWhatTheBinderLetsThrough_NamingTheKnownSources(string configured, string rendered)
    {
        // Honouring any of these picks a price series by accident: `KeyLevels.PivotPrices` reads anything it
        // does not recognise as Heikin-Ashi, so a server that booted on one would answer every level call
        // from a source nobody chose, with nothing to see. The rule is an IValidatableObject on the options
        // type, so it travels with the value rather than living in a lambda at the composition root that a
        // second binder could miss.
        Action bind = () => Bind(configured);

        bind.Should().Throw<OptionsValidationException>()
            .WithMessage("*'" + rendered + "'*")
            .WithMessage("*HeikinAshiBody, Body, HighLow*");
    }

    /// <summary>
    /// A key present with a JSON <c>null</c> is not an absent key. The binder writes the enum's zero over
    /// the initializer, so it lands in <c>Validate</c> as <c>Unknown</c> — found while isolating the absent
    /// case above, where shadowing an exported variable with a null did not read as "not set".
    /// </summary>
    [Fact]
    public void Validate_RefusesAJsonNull_WhichBindsAsUnknownRatherThanAsAbsent()
    {
        Action bind = () => BindWith(builder => builder.Configuration.AddJsonStream(
            new MemoryStream(Encoding.UTF8.GetBytes("""{ "KeyLevels": { "Source": null } }"""))));

        bind.Should().Throw<OptionsValidationException>()
            .WithMessage("*'Unknown'*")
            .WithMessage("*HeikinAshiBody, Body, HighLow*");
    }

    /// <summary>
    /// The refusal must explain how its input actually arrives. It used to say an unset or mistyped value
    /// binds to <c>Unknown</c> — the claim gh#459 was filed for — then that only an explicit <c>Unknown</c>
    /// could reach it, then that four shapes could and nothing else; each was a completeness claim and each
    /// was wrong. It now names the mechanism — what the binder can read binds, a comma list included — and
    /// blames neither an unset nor a mistyped value.
    /// </summary>
    /// <param name="configured">The configured value.</param>
    [Theory]
    [InlineData("Unknown")]
    [InlineData("0")]
    [InlineData("99")]
    public void TheRefusal_SaysHowItsInputArrives_AndNoLongerBlamesAnUnsetOrMistypedValue(string configured)
    {
        string message = CaptureRefusal(configured);

        message.Should().Contain("numeral");
        message.Should().Contain("comma-separated");
        message.Should().Contain("JSON null");
        message.Should().NotContain("unset");
        message.Should().NotContain("mistyped");
    }

    private static string CaptureRefusal(string configured)
    {
        try
        {
            Bind(configured);
        }
        catch (OptionsValidationException exception)
        {
            return exception.Message;
        }

        throw new InvalidOperationException(
            "'" + configured + "' was expected to be refused by Validate and was not.");
    }

    /// <summary>Binds with <c>KeyLevels:Source</c> present and set to <paramref name="configured"/>.</summary>
    /// <param name="configured">The value for <c>KeyLevels__Source</c>.</param>
    /// <returns>The bound and validated options.</returns>
    private static KeyLevelDetectionOptions Bind(string configured) =>
        BindWith(builder => builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["KeyLevels:Source"] = configured }));

    /// <summary>
    /// Binds through the real composition root, so this measures what the deployed server does rather than a
    /// hand-rolled binder. <paramref name="configure"/> adds whatever configuration the case needs; adding
    /// nothing is the absent case.
    /// </summary>
    /// <param name="configure">Adds the case's configuration to the builder.</param>
    /// <returns>The bound and validated options.</returns>
    private static KeyLevelDetectionOptions BindWith(Action<WebApplicationBuilder> configure)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();

        // CreateBuilder() reads the process environment, and a shell with KeyLevels__Source exported --
        // exactly how this behaviour was measured -- would otherwise reach the absent case and redden it for
        // something that is not a defect. Shadowing the variable with a null does not work: a null-valued
        // key binds as Unknown (pinned above), which is not "absent" either. So the unprefixed environment
        // provider is removed rather than overridden, leaving the prefixed host-settings ones alone.
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

        // The same two switches CompositionRootTests.Build sets. They are what turned the captive dependency
        // that file was written for into a test, and a binder measured through a container the server never
        // builds is measured through the wrong container.
        using ServiceProvider provider = builder.Services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        return provider.GetRequiredService<IOptions<KeyLevelDetectionOptions>>().Value;
    }
}
