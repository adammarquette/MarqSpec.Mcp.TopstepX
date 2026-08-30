using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.Mcp.TopstepX.Tests.Data;

/// <summary>
/// The key-shape decisions gh#215 made, pinned on the model so a later "cleanup" cannot silently
/// move <c>ContractId</c> out of the tape key or into the footprint key.
/// </summary>
public sealed class TradeTapeKeyTests : IDisposable
{
    private readonly TopstepXDbContext _database = new(
        new DbContextOptionsBuilder<TopstepXDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    public void Dispose() => _database.Dispose();

    [Fact]
    public void TradesKey_IncludesContractId_UnlikeBars()
    {
        KeyNames<TradeRecord>().Should().Equal(
            nameof(TradeRecord.Venue),
            nameof(TradeRecord.Instrument),
            nameof(TradeRecord.ContractId),
            nameof(TradeRecord.TradeTimeUtc),
            nameof(TradeRecord.Sequence));

        KeyNames<BarRecord>().Should().NotContain(nameof(BarRecord.ContractId));
    }

    [Fact]
    public void TapeCoverageKey_IncludesContractId_BecauseListeningIsPerContract()
    {
        KeyNames<TapeCoverageRecord>().Should().Equal(
            nameof(TapeCoverageRecord.Venue),
            nameof(TapeCoverageRecord.Instrument),
            nameof(TapeCoverageRecord.ContractId),
            nameof(TapeCoverageRecord.RangeStart),
            nameof(TapeCoverageRecord.RangeEnd));
    }

    [Fact]
    public void FootprintCellsKey_OmitsContractId_TheAsymmetryTheDictionaryHasToState()
    {
        KeyNames<FootprintCellRecord>().Should().Equal(
            nameof(FootprintCellRecord.Venue),
            nameof(FootprintCellRecord.Instrument),
            nameof(FootprintCellRecord.ResolutionMinutes),
            nameof(FootprintCellRecord.BucketStart),
            nameof(FootprintCellRecord.Price));

        _database.Model.FindEntityType(typeof(FootprintCellRecord))!
            .FindProperty("ContractId")
            .Should().BeNull();
    }

    private IReadOnlyList<string> KeyNames<TEntity>() =>
        _database.Model.FindEntityType(typeof(TEntity))!
            .FindPrimaryKey()!
            .Properties
            .Select(property => property.Name)
            .ToArray();
}
