using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.Tools;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace MarqSpec.Mcp.TopstepX.Tests.Tools;

/// <summary>
/// The shipped bar-window cap, as an agent reads it and as a refusal explains it (gh#868).
/// </summary>
/// <remarks>
/// A client learns the cap from the tool schema or not at all. The refusal is the second copy, and it has
/// to name the bars the window would have needed, the cap, and a page the caller can actually send.
/// </remarks>
public sealed class GetBarsWindowLimitTests
{
    private static readonly DateTimeOffset _exampleStart = new(2026, 10, 27, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void GetBarsDescriptions_StateTheShippedBarLimit()
    {
        MarketDataOptions.DefaultMaxRowsText.Should().Be(
            MarketDataOptions.DefaultMaxRows.ToString(CultureInfo.InvariantCulture),
            "descriptions concatenate this spelling, so it has to be the constant");

        int limit = MarketDataOptions.DefaultMaxRows;
        string limitPattern = WholeNumber(limit);

        MethodInfo method = GetBarsMethod();
        string tool = DescriptionOf(method);
        tool.Should().MatchRegex(
            limitPattern,
            "the tool description is what an agent reads before it asks, and the cap has to be in it");

        foreach (string parameter in new[] { "resolutionMinutes", "fromUtc", "toUtc" })
        {
            DescriptionOf(method, parameter).Should().MatchRegex(
                limitPattern,
                "{0} is one of the three arguments that decide the bar count",
                parameter);
        }

        McpServerTool published = McpServerTool.Create(
            method,
            static _ => throw new InvalidOperationException(
                "The schema comes from the signature; this tool is never invoked here."),
            new McpServerToolCreateOptions());

        published.ProtocolTool.Description.Should().MatchRegex(
            limitPattern,
            "the description on the wire is the one a client receives");

        JsonElement properties = published.ProtocolTool.InputSchema.GetProperty("properties");
        foreach (string parameter in new[] { "resolutionMinutes", "fromUtc", "toUtc" })
        {
            properties.GetProperty(parameter).GetProperty("description").GetString().Should().MatchRegex(
                limitPattern,
                "{0}'s schema description is the copy a client reads",
                parameter);
        }
    }

    [Fact]
    public void AnOverCapOneMinuteWindow_NamesTheBarsNeededTheLimitAndHowToPage()
    {
        int limit = MarketDataOptions.DefaultMaxRows;
        long needed = limit + 1L;
        ToolGuards guards = Guards(limit);
        DateTimeOffset start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

        Action validate = () => guards.ValidateWindow(start, start.AddMinutes(needed), 1);

        string message = validate.Should().Throw<McpException>().Which.Message;
        message.Should().Contain(
            needed.ToString(CultureInfo.InvariantCulture),
            "the refusal names the bars the window would have needed");
        message.Should().Contain(
            "cap of " + limit.ToString(CultureInfo.InvariantCulture),
            "and the cap it is over");
        message.Should().Contain("narrower start and end");
        message.Should().Contain("coarser unit");
        message.Should().Contain(Stamp(_exampleStart));
        message.Should().Contain(Stamp(_exampleStart.AddMinutes(limit)));
        message.Should().Contain("resolutionMinutes 5");
        message.Should().NotContain(
            "gap-detection pass",
            "this refusal is the row cap's, not the fixed detection cap");
    }

    [Fact]
    public void TheExampleWindow_FitsAndTheNextMinuteDoesNot()
    {
        int limit = MarketDataOptions.DefaultMaxRows;
        ToolGuards guards = Guards(limit);
        DateTimeOffset exampleEnd = _exampleStart.AddMinutes(limit);

        BarRange window = guards.ValidateWindow(_exampleStart, exampleEnd, 1);
        window.Start.Should().Be(_exampleStart);
        window.End.Should().Be(exampleEnd);

        Action oneMore = () => guards.ValidateWindow(_exampleStart, exampleEnd.AddMinutes(1), 1);
        oneMore.Should().Throw<McpException>()
            .WithMessage("*" + (limit + 1).ToString(CultureInfo.InvariantCulture) + "*");
    }

    [Fact]
    public void TheSameClockSpanAtFiveMinutes_FitsInsideTheCap()
    {
        // The coarser-unit advice has to be true of the window the refusal just rejected.
        int limit = MarketDataOptions.DefaultMaxRows;
        long needed = limit + 1L;
        ToolGuards guards = Guards(limit);
        DateTimeOffset start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

        BarRange window = guards.ValidateWindow(start, start.AddMinutes(needed), 5);

        window.Duration.Should().Be(TimeSpan.FromMinutes(needed));
    }

    private static ToolGuards Guards(int maxRows) =>
        new(Options.Create(new MarketDataOptions
        {
            Instruments = "ES",
            MaxRows = maxRows,
            SessionCloseCentral = "16:00",
        }));

    private static MethodInfo GetBarsMethod() =>
        typeof(BarTools).GetMethod(nameof(BarTools.GetBars))
        ?? throw new InvalidOperationException("BarTools.GetBars has been renamed.");

    private static string DescriptionOf(MethodInfo method) =>
        method.GetCustomAttribute<DescriptionAttribute>()?.Description
        ?? throw new InvalidOperationException("get_bars has no description.");

    private static string DescriptionOf(MethodInfo method, string parameter) =>
        method.GetParameters().Single(p => p.Name == parameter)
            .GetCustomAttribute<DescriptionAttribute>()?.Description
        ?? throw new InvalidOperationException("get_bars." + parameter + " has no description.");

    /// <summary>Matches a number as a whole token, not as digits inside a longer one.</summary>
    private static string WholeNumber(int value) =>
        @"(?<![\d.])" + value.ToString(CultureInfo.InvariantCulture) + @"(?!\.?\d)";

    private static string Stamp(DateTimeOffset instant) =>
        instant.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
