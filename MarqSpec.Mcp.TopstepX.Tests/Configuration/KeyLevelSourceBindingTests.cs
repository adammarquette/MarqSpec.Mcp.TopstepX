using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
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
/// An <b>absent</b> key never reaches <c>Unknown</c> at all — the binder leaves a bound property alone when
/// its key is missing, so the property initializer's <see cref="PivotSource.HeikinAshiBody"/> stands. A
/// <b>mistyped</b> one never reaches <c>Validate</c> — the binder throws first. The refusal message is
/// reachable, but only by the one route the old text did not name: an explicit <c>Unknown</c>.
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
        KeyLevelDetectionOptions options = Bind(null);

        options.Source.Should().Be(PivotSource.HeikinAshiBody);
    }

    /// <summary>A named source is honoured, so the default is a default rather than a hard-wiring.</summary>
    [Fact]
    public void Source_IsHonoured_WhenTheValueNamesAKnownSource()
    {
        KeyLevelDetectionOptions options = Bind("HighLow");

        options.Source.Should().Be(PivotSource.HighLow);
    }

    /// <summary>
    /// A value outside the vocabulary fails in the binder, before <c>Validate</c> runs — which is why the
    /// friendly message cannot be what an operator sees for a typo.
    /// </summary>
    [Theory]
    [InlineData("Bogus")]
    [InlineData("")]
    public void Binding_Fails_BeforeValidateRuns_WhenTheValueIsNotAPivotSource(string configured)
    {
        Action bind = () => Bind(configured);

        bind.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*KeyLevels:Source*")
            .WithInnerException<FormatException>();
    }

    /// <summary>The one route that reaches the validator: <c>Unknown</c>, configured on purpose.</summary>
    [Fact]
    public void Validate_RefusesAnExplicitUnknown_NamingTheKnownSources()
    {
        Action bind = () => Bind("Unknown");

        bind.Should().Throw<OptionsValidationException>().WithMessage("*Known sources*");
    }

    /// <summary>
    /// The refusal must explain how <c>Unknown</c> actually arrives. It used to say an unset or mistyped
    /// value binds to it — the claim gh#459 was filed for, and the one a reader acts on.
    /// </summary>
    [Fact]
    public void TheRefusal_SaysHowUnknownArrives_AndNoLongerBlamesAnUnsetOrMistypedValue()
    {
        string message = CaptureRefusal("Unknown");

        message.Should().Contain("explicitly");
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

    /// <summary>
    /// Binds through the real composition root, so this measures what the deployed server does rather than a
    /// hand-rolled binder. <paramref name="configured"/> null means the key is absent entirely.
    /// </summary>
    /// <param name="configured">The value for <c>KeyLevels__Source</c>, or null to omit the key.</param>
    /// <returns>The bound and validated options.</returns>
    private static KeyLevelDetectionOptions Bind(string? configured)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();

        if (configured is not null)
        {
            builder.Configuration.AddInMemoryCollection(
                new Dictionary<string, string?> { ["KeyLevels:Source"] = configured });
        }

        Program.ConfigureServices(builder, new McpOptions { Transport = McpTransport.Stdio });

        return builder.Services
            .BuildServiceProvider()
            .GetRequiredService<IOptions<KeyLevelDetectionOptions>>()
            .Value;
    }
}
