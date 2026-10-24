using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// The pre-send Unknown claim is unique per account, tool and key, and a new context still sees it.
/// </summary>
[Collection(SeriesStoreCollection.Name)]
[Trait("Gate", "ADR-0024")]
public sealed class TradingAuditIdempotencyClaimTests(SeriesStoreFixture fixture) : IAsyncLifetime
{
    private readonly SeriesStoreFixture _fixture = fixture;

    /// <inheritdoc />
    public Task InitializeAsync() => _fixture.ResetAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task TradingRiskGate_UnknownClaim_IsVisibleToANewContext_AndASecondClaimIsRejected()
    {
        TradingAuditWriteRequest claim = Claim(accountId: 42, tool: "place_order", key: "durable", hash: new string('a', 64));

        await using TopstepXDbContext first = _fixture.CreateContext();
        TradingAuditLogWriter writer = new(first, TimeProvider.System);
        TradingAuditClaimResult inserted = await writer.TryClaimUnknownAsync(claim, CancellationToken.None);
        inserted.Status.Should().Be(TradingAuditClaimStatus.Claimed);

        await using TopstepXDbContext restarted = _fixture.CreateContext();
        TradingAuditLogWriter again = new(restarted, TimeProvider.System);
        TradingAuditClaimResult replay = await again.TryClaimUnknownAsync(claim, CancellationToken.None);
        replay.Status.Should().Be(TradingAuditClaimStatus.Replay);
        replay.BestOutcome.Should().Be(TradingAuditOutcomes.Unknown);

        TradingAuditClaimResult conflict = await again.TryClaimUnknownAsync(
            claim with { RequestHash = new string('b', 64) },
            CancellationToken.None);
        conflict.Status.Should().Be(TradingAuditClaimStatus.Conflict);

        TradingAuditClaimResult otherTool = await again.TryClaimUnknownAsync(
            claim with { Tool = "cancel_order" },
            CancellationToken.None);
        otherTool.Status.Should().Be(TradingAuditClaimStatus.Claimed);

        TradingAuditClaimResult otherAccount = await again.TryClaimUnknownAsync(
            claim with { AccountId = 43 },
            CancellationToken.None);
        otherAccount.Status.Should().Be(TradingAuditClaimStatus.Claimed);
    }

    [Fact]
    public async Task TradingRiskGate_DuplicateUnknownClaim_IsRejectedByTheUniqueIndex()
    {
        await using TopstepXDbContext database = _fixture.CreateContext();
        database.TradingAuditLog.Add(Row());
        await database.SaveChangesAsync();

        database.ChangeTracker.Clear();
        database.TradingAuditLog.Add(Row());
        Func<Task> save = () => database.SaveChangesAsync();

        (await save.Should().ThrowAsync<DbUpdateException>())
            .Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task TradingRiskGate_LiveRow_IsWhatARestartedClaimReplays()
    {
        TradingAuditWriteRequest claim = Claim(7, "place_order", "acked", new string('c', 64));
        await using (TopstepXDbContext database = _fixture.CreateContext())
        {
            TradingAuditLogWriter writer = new(database, TimeProvider.System);
            (await writer.TryClaimUnknownAsync(claim, CancellationToken.None)).Status
                .Should().Be(TradingAuditClaimStatus.Claimed);
            await writer.WriteAsync(
                claim with { Outcome = TradingAuditOutcomes.Live, OrderId = 99 },
                CancellationToken.None);
        }

        await using TopstepXDbContext restarted = _fixture.CreateContext();
        TradingAuditClaimResult replay = await new TradingAuditLogWriter(restarted, TimeProvider.System)
            .TryClaimUnknownAsync(claim, CancellationToken.None);

        replay.Status.Should().Be(TradingAuditClaimStatus.Replay);
        replay.BestOutcome.Should().Be(TradingAuditOutcomes.Live);
        replay.OrderId.Should().Be(99);
    }

    private static TradingAuditWriteRequest Claim(int accountId, string tool, string key, string hash) =>
        new(
            accountId,
            "Practice",
            tool,
            key,
            hash,
            DryRun: false,
            TradingAuditOutcomes.Unknown,
            ContractCount: 0,
            VenueErrorCode: null);

    private static TradingAuditLogRecord Row() =>
        new()
        {
            AttemptedAt = DateTimeOffset.UnixEpoch,
            AccountId = 1,
            ParsedStage = "Practice",
            Tool = "place_order",
            IdempotencyKey = "index-key",
            RequestHash = new string('d', 64),
            DryRun = false,
            ContractCount = 0,
            Outcome = TradingAuditOutcomes.Unknown,
        };
}
