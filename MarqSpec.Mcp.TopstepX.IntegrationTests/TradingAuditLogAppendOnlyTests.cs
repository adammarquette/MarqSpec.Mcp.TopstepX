using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// The append-only guarantee on <see cref="TradingAuditLogRecord"/> — enforced in Postgres, not in application code.
/// </summary>
[Collection(SeriesStoreCollection.Name)]
public sealed class TradingAuditLogAppendOnlyTests(SeriesStoreFixture fixture) : IAsyncLifetime
{
    private readonly SeriesStoreFixture _fixture = fixture;

    private long _rowId;

    /// <inheritdoc />
    public Task InitializeAsync() => _fixture.ResetAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Append_PersistsEveryColumn()
    {
        await using TopstepXDbContext database = _fixture.CreateContext();

        DateTimeOffset attemptedAt = new(2026, 10, 18, 6, 0, 0, TimeSpan.Zero);
        TradingAuditLogRecord row = NewRow(attemptedAt, dryRun: true, venueErrorCode: null);
        database.TradingAuditLog.Add(row);
        await database.SaveChangesAsync();
        _rowId = row.Id;

        TradingAuditLogRecord stored = await database.TradingAuditLog.SingleAsync(a => a.Id == _rowId);
        stored.AttemptedAt.Should().Be(attemptedAt);
        stored.AccountId.Should().Be(12345);
        stored.ParsedStage.Should().Be("Practice");
        stored.Tool.Should().Be("place_order");
        stored.IdempotencyKey.Should().Be("key-append-test");
        stored.RequestHash.Should().Be(new string('a', 64));
        stored.DryRun.Should().BeTrue();
        stored.Outcome.Should().Be("Preview");
        stored.VenueErrorCode.Should().BeNull();
    }

    [Fact]
    public async Task Update_ThroughEf_IsRefused()
    {
        await SeedOneRowAsync();

        await using TopstepXDbContext database = _fixture.CreateContext();
        TradingAuditLogRecord row = await database.TradingAuditLog.SingleAsync(a => a.Id == _rowId);
        row.Outcome = "Tampered";

        Func<Task> save = () => database.SaveChangesAsync();
        await save.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Delete_ThroughEf_IsRefused()
    {
        await SeedOneRowAsync();

        await using TopstepXDbContext database = _fixture.CreateContext();
        TradingAuditLogRecord row = await database.TradingAuditLog.SingleAsync(a => a.Id == _rowId);
        database.TradingAuditLog.Remove(row);

        Func<Task> save = () => database.SaveChangesAsync();
        await save.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task Update_ThroughSql_IsRefused()
    {
        await SeedOneRowAsync();

        await using TopstepXDbContext database = _fixture.CreateContext();
        Func<Task> update = () => database.Database.ExecuteSqlRawAsync(
            """UPDATE "TradingAuditLog" SET "Outcome" = 'Tampered' WHERE "Id" = {0};""",
            _rowId);

        PostgresException ex = (await update.Should().ThrowAsync<PostgresException>()).Which;
        ex.SqlState.Should().Be(PostgresErrorCodes.RaiseException);
        ex.MessageText.Should().Contain("append-only");
    }

    [Fact]
    public async Task Delete_ThroughSql_IsRefused()
    {
        await SeedOneRowAsync();

        await using TopstepXDbContext database = _fixture.CreateContext();
        Func<Task> delete = () => database.Database.ExecuteSqlRawAsync(
            """DELETE FROM "TradingAuditLog" WHERE "Id" = {0};""",
            _rowId);

        PostgresException ex = (await delete.Should().ThrowAsync<PostgresException>()).Which;
        ex.SqlState.Should().Be(PostgresErrorCodes.RaiseException);
        ex.MessageText.Should().Contain("append-only");
    }

    private async Task SeedOneRowAsync()
    {
        if (_rowId != 0)
        {
            return;
        }

        await using TopstepXDbContext database = _fixture.CreateContext();
        TradingAuditLogRecord row = NewRow(
            new DateTimeOffset(2026, 10, 18, 7, 0, 0, TimeSpan.Zero),
            dryRun: false,
            venueErrorCode: "OrderRejected");
        database.TradingAuditLog.Add(row);
        await database.SaveChangesAsync();
        _rowId = row.Id;
    }

    private static TradingAuditLogRecord NewRow(
        DateTimeOffset attemptedAt,
        bool dryRun,
        string? venueErrorCode) =>
        new()
        {
            AttemptedAt = attemptedAt,
            AccountId = 12345,
            ParsedStage = "Practice",
            Tool = "place_order",
            IdempotencyKey = "key-append-test",
            RequestHash = new string('a', 64),
            DryRun = dryRun,
            ContractCount = 0,
            Outcome = dryRun ? "Preview" : "Failed",
            VenueErrorCode = venueErrorCode,
        };
}
