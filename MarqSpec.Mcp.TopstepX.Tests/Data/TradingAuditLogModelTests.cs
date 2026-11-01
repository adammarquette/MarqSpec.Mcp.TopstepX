using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
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

    [Fact]
    public async Task FindStoredWriterRefusal_IgnoresARowWhoseFinaliseIdIsNull()
    {
        Guid thisAttempt = Guid.NewGuid();
        _database.TradingAuditLog.Add(Row(null, TradingAuditOutcomes.Refused, "account_writer_held"));
        _database.TradingAuditLog.Add(Row(
            Guid.NewGuid(),
            TradingAuditOutcomes.Refused,
            "account_writer_held"));
        _database.TradingAuditLog.Add(Row(
            Guid.NewGuid(),
            TradingAuditOutcomes.Attempted,
            "account_writer_held",
            contractCount: -1));
        await _database.SaveChangesAsync();

        TradingWriterRefusalStored stored = await new TradingAuditLogWriter(_database, TimeProvider.System)
            .FindStoredWriterRefusalAsync(
                thisAttempt,
                "account_writer_held",
                reversalCount: 1,
                CancellationToken.None);

        stored.Should().Be(TradingWriterRefusalStored.None);
    }

    private static TradingAuditLogRecord Row(
        Guid? finaliseId,
        string outcome,
        string code,
        int contractCount = 0) =>
        new()
        {
            AttemptedAt = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero),
            AccountId = 100,
            ParsedStage = "Practice",
            Tool = "place_order",
            IdempotencyKey = "old-row",
            RequestHash = new string('a', 64),
            DryRun = false,
            ContractCount = contractCount,
            Outcome = outcome,
            VenueErrorCode = code,
            FinaliseId = finaliseId,
        };

    private IReadOnlyList<string> KeyNames<TEntity>() =>
        _database.Model.FindEntityType(typeof(TEntity))!
            .FindPrimaryKey()!
            .Properties
            .Select(property => property.Name)
            .ToArray();
}
