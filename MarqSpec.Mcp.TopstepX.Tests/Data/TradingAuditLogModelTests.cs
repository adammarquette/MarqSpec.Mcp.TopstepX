using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace MarqSpec.Mcp.TopstepX.Tests.Data;

/// <summary>
/// Model-shape pins for the trading audit log (gh#709).
/// </summary>
public sealed class TradingAuditLogModelTests : IDisposable
{
    private readonly TopstepXDbContext _database = new(
        new DbContextOptionsBuilder<TopstepXDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    public void Dispose() => _database.Dispose();

    [Fact]
    public void TradingAuditLogKey_IsSurrogateIdentity()
    {
        KeyNames<TradingAuditLogRecord>().Should().Equal(nameof(TradingAuditLogRecord.Id));
    }

    private IReadOnlyList<string> KeyNames<TEntity>() =>
        _database.Model.FindEntityType(typeof(TEntity))!
            .FindPrimaryKey()!
            .Properties
            .Select(property => property.Name)
            .ToArray();
}
