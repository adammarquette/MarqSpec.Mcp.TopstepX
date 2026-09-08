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
/// <b>This file used to pin an enum-binding boundary that let a comma-separated list OR itself onto a real
/// source and boot</b> (gh#468, found reviewing PR #467): <c>Enum.Parse</c> read <c>HeikinAshiBody,Body</c>
/// as <c>1 | 2</c>, <see cref="PivotSource.HighLow"/>, without ever consulting <c>[Flags]</c>, and only
/// <c>IsServable</c> stood behind whatever it produced. gh#468 closed that by binding
/// <see cref="KeyLevelDetectionOptions.Source"/> as a string and resolving it in
/// <see cref="KeyLevelDetectionOptions.Validate"/> through the same
/// <see cref="PivotSources.Resolve(string)"/> a call's <c>pivotSource</c> already goes through. There is now
/// one door, not two: the binder never refuses a string, so every value — absent key excepted — is decided by
/// whether <c>Resolve</c> recognises it, trimmed and case-insensitive, as one of the three names.
/// </para>
/// <para>
/// That collapses the binder-versus-validator split this file used to pin. A numeral, a comma list and
/// <c>Unknown</c> are refused for the same reason now — <c>Resolve</c> never reads any of them as a name — so
/// these tests pin the boundary <c>Resolve</c> draws rather than enumerate the ways to reach it.
/// </para>
/// </remarks>
public sealed class KeyLevelSourceBindingTests
{
    /// <summary>An absent key leaves the initializer's default standing; nothing is refused.</summary>
    [Fact]
    public void Source_IsTheDefault_AndIsNotRefused_WhenTheKeyIsAbsent()
    {
        KeyLevelDetectionOptions options = BindWith(builder => { });

        options.Source.Should().Be(nameof(PivotSource.HeikinAshiBody));
    }

    /// <summary>
    /// A configured name <see cref="PivotSources.Resolve(string)"/> recognises is accepted, however it was
    /// cased or padded — the same tolerance a call's <c>pivotSource</c> already gets.
    /// </summary>
    /// <param name="configured">The configured value.</param>
    /// <param name="resolved">What it resolves to.</param>
    [Theory]
    [InlineData("HighLow", PivotSource.HighLow)]
    [InlineData("highlow", PivotSource.HighLow)]
    [InlineData(" HeikinAshiBody ", PivotSource.HeikinAshiBody)]
    [InlineData("Body", PivotSource.Body)]
    public void Source_IsAccepted_WhenPivotSourcesResolveRecognisesTheConfiguredName(
        string configured,
        PivotSource resolved)
    {
        KeyLevelDetectionOptions options = Bind(configured);

        PivotSources.Resolve(options.Source).Should().Be(resolved);
    }

    /// <summary>
    /// Everything <see cref="PivotSources.Resolve(string)"/> does not read as one of the three names is
    /// refused at startup with the friendly message — a name outside the vocabulary, an empty string,
    /// <c>Unknown</c> itself (defined, but not servable), a numeral (no longer a back door: <c>Resolve</c>
    /// matches names, not <c>Enum.Parse</c>'s numeric conversion), and — the case that was silently OR-ed
    /// into a real source before this card — a comma-separated list naming two of the three.
    /// </summary>
    /// <param name="configured">The configured value.</param>
    [Theory]
    [InlineData("Bogus")]
    [InlineData("")]
    [InlineData("Unknown")]
    [InlineData(" Unknown ")]
    [InlineData("0")]
    [InlineData("99")]
    [InlineData("-1")]
    [InlineData("HeikinAshiBody,Body")]
    [InlineData("Body, HighLow")]
    public void Validate_Refuses_WhenPivotSourcesResolveDoesNotRecogniseTheConfiguredName(string configured)
    {
        Action bind = () => Bind(configured);

        bind.Should().Throw<OptionsValidationException>()
            .WithMessage("*'" + configured + "'*")
            .WithMessage("*HeikinAshiBody, Body, HighLow*");
    }

    /// <summary>
    /// A key present with a JSON <c>null</c> is not an absent key, and it is not a name
    /// <see cref="PivotSources.Resolve(string)"/> recognises either — it is refused on the same terms as any
    /// other unresolved value. It is the one case with its own rendering: <see cref="Validate"/> writes the
    /// bare word <c>null</c> rather than quoting it as a value it read, so the rendering is asserted here.
    /// </summary>
    [Fact]
    public void Validate_RefusesAJsonNull_WhichIsNotAnAbsentKeyAndDoesNotResolve()
    {
        Action bind = () => BindWith(builder => builder.Configuration.AddJsonStream(
            new MemoryStream(Encoding.UTF8.GetBytes("""{ "KeyLevels": { "Source": null } }"""))));

        bind.Should().Throw<OptionsValidationException>()
            .WithMessage("*__Source is null,*")
            .WithMessage("*HeikinAshiBody, Body, HighLow*");
    }

    /// <summary>
    /// The refusal's explanatory half, which nothing else reads.
    /// </summary>
    /// <remarks>
    /// <b>This assertion is the reason the message stopped being wrong.</b> The sentence beside the value has
    /// been false four times — gh#459 filed for it, then twice more as that card was corrected, then a fifth
    /// shape (gh#468) that no wording had covered — and each time the value and the source list stayed
    /// perfectly true beside it. Asserting those two catches nothing: they are produced by
    /// <see cref="PivotSources.KnownNames"/> and by the input, and they hold no matter what the prose claims.
    /// So this reads the prose, and it reads it for the <b>mechanism</b> the message now names rather than for
    /// a list of the shapes it refuses — the enumeration is exactly what gh#468 stopped making, and a needle
    /// tied to one would have to be rewritten by the next author who correctly stopped enumerating.
    /// </remarks>
    [Fact]
    public void TheRefusal_ExplainsMatchingByName_AndBlamesNeitherAnUnsetNorAMistypedValue()
    {
        string message = CaptureRefusal("Bogus");

        message.Should().Contain("Matching is by name");
        message.Should().Contain("trimmed and case-insensitive");
        message.Should().Contain("Leaving the key out keeps the " + nameof(PivotSource.HeikinAshiBody));
        message.Should().NotContain("unset");
        message.Should().NotContain("mistyped");
    }

    /// <summary>
    /// The refusal <see cref="Validate"/> writes for <paramref name="configured"/>, or a failure naming it if
    /// it was not refused at all.
    /// </summary>
    /// <param name="configured">The value for <c>KeyLevels__Source</c>.</param>
    /// <returns>The message an operator reads at startup.</returns>
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
