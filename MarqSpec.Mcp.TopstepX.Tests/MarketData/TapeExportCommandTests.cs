using FluentAssertions;
using MarqSpec.Mcp.TopstepX.MarketData;

namespace MarqSpec.Mcp.TopstepX.Tests.MarketData;

/// <summary>The export and import verbs take an optional exact <c>yyyy-MM-dd</c> day.</summary>
public sealed class TapeExportCommandTests
{
    [Fact]
    public void Parse_WhenDayIsMissing_AppliesToEveryDay()
    {
        (bool isExport, DateOnly? day) = TapeExportCommands.Parse(["export-tape"]);

        isExport.Should().BeTrue();
        day.Should().BeNull();

        (bool import, DateOnly? importDay) = TapeExportCommands.Parse(["import-tape"]);
        import.Should().BeFalse();
        importDay.Should().BeNull();
    }

    [Fact]
    public void Parse_WhenDayIsIso_ReturnsThatDay()
    {
        (bool isExport, DateOnly? day) = TapeExportCommands.Parse(["import-tape", "2026-10-17"]);

        isExport.Should().BeFalse();
        day.Should().Be(new DateOnly(2026, 10, 17));
    }

    [Fact]
    public void Parse_WhenDayIsNotIso_Refuses()
    {
        Action parse = () => TapeExportCommands.Parse(["export-tape", "17/10/2026"]);

        parse.Should().Throw<ArgumentException>().Which.Message.Should().Contain("yyyy-MM-dd");
    }
}
