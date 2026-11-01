using System.Data.Common;
using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Data;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MarqSpec.Mcp.TopstepX.IntegrationTests;

/// <summary>
/// A real Postgres outage on the gate returns a refusal, does not throw, and does not call the venue
/// (gh#958, ADR-0024).
/// </summary>
[Collection(SeriesStoreCollection.Name)]
[Trait("Gate", "ADR-0024")]
public sealed class TradingRiskGateDbOutageTests(SeriesStoreFixture fixture) : IAsyncLifetime
{
    private const int AccountId = 100;
    private const string ContractId = "CON.F.US.MES.Z26";

    private readonly SeriesStoreFixture _fixture = fixture;

    /// <inheritdoc />
    public Task InitializeAsync() => _fixture.ResetAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task TradingRiskGate_ConnectionRefused_RefusesAndDoesNotSend()
    {
        await using NpgsqlConnection live = new(_fixture.ConnectionString);
        await live.OpenAsync();
        await using NpgsqlCommand ping = new("SELECT 1", live);
        (await ping.ExecuteScalarAsync()).Should().Be(1);

        NpgsqlConnectionStringBuilder refused = new(_fixture.ConnectionString)
        {
            Port = 1,
            Timeout = 2,
            CommandTimeout = 2,
        };
        await using TopstepXDbContext database = new(new DbContextOptionsBuilder<TopstepXDbContext>()
            .UseNpgsql(refused.ConnectionString, npgsql => npgsql.UseVector())
            .Options);
        CountingSends gateway = new();
        using HostTelemetry telemetry = new();
        TradingRiskGate gate = Gate(database, telemetry, gateway, writerLock: null);

        TradingRiskGateResult result = await gate.SubmitPlace(
            Place(),
            Live("connection-refused"),
            CancellationToken.None);

        result.Outcome.Should().Be(TradingAttemptOutcome.Refused);
        result.Refusal.Should().Be(TradingExecutionRefusal.BeforeSendFault);
        gateway.Sends.Should().Be(0);
    }

    [Fact]
    public async Task TradingRiskGate_LookupAfterBothSavesFail_RefusesAndDoesNotSend()
    {
        ArmedDown down = new();
        await using TopstepXDbContext database = _fixture.CreateContext(
            new SaveDown(down),
            new ReadDown(down));
        CountingSends gateway = new();
        using HostTelemetry telemetry = new();
        TradingRiskGate gate = Gate(database, telemetry, gateway, new ArmThenUnavailable(down));

        TradingRiskGateResult result = await gate.SubmitPlace(
            Place(),
            Live("lookup-outage"),
            CancellationToken.None);

        result.Outcome.Should().Be(TradingAttemptOutcome.Refused);
        result.Refusal!.Value.Code.Should().Be(ExecutionWriteRefusal.AccountWriterUnavailable);
        down.Tripped.Should().BeTrue();
        gateway.Sends.Should().Be(0);
    }

    private static ExecutionPlaceOrderCommand Place() =>
        new(
            AccountId,
            ContractId,
            ExecutionOrderType.Limit,
            VenueSide.Buy,
            Size: 1,
            LimitPrice: 6800m,
            StopPrice: null,
            TrailPrice: null);

    private static TradingRiskAttemptContext Live(string key) =>
        new(DryRun: false, key, RequireIdempotencyKey: true);

    private static TradingRiskGate Gate(
        TopstepXDbContext database,
        HostTelemetry telemetry,
        CountingSends gateway,
        ITradingAccountWriterLock? writerLock)
    {
        TradingExecutionOptions options = new()
        {
            Enabled = true,
            AllowedAccountIds = AccountId.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        return new TradingRiskGate(
            new TradingExecutionGate(
                Options.Create(options),
                new DisengagedKillSwitch(),
                new ProjectXAccountStageResolver()),
            gateway,
            new PracticeAccount(AccountId),
            new ProjectXAccountStageResolver(),
            new TradingDailyCapCoordinator(database, TimeProvider.System, telemetry),
            new TradingAuditLogWriter(database, TimeProvider.System),
            new TradingContractDenylist(
                Options.Create(options),
                new InstrumentRegistry(Options.Create(new MarketDataOptions { Instruments = "MES" }))),
            new TradingWriteVenueAccountCache(),
            telemetry,
            writerLock);
    }

    private sealed class ArmedDown
    {
        public bool Down { get; private set; }

        public bool Tripped { get; private set; }

        public void Arm() => Down = true;

        public void Trip()
        {
            Tripped = true;
            throw new NpgsqlException("connection is broken");
        }
    }

    private sealed class SaveDown(ArmedDown down) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (down.Down)
            {
                down.Trip();
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class ReadDown(ArmedDown down) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (down.Down)
            {
                down.Trip();
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class ArmThenUnavailable(ArmedDown down) : ITradingAccountWriterLock
    {
        private int _calls;

        public Task<TradingWriterLockTake> TryHoldAsync(int accountId, CancellationToken cancellationToken)
        {
            _calls++;
            if (_calls == 1)
            {
                return Task.FromResult(TradingWriterLockTake.Held);
            }

            down.Arm();
            return Task.FromResult(TradingWriterLockTake.Unavailable);
        }
    }

    private sealed class DisengagedKillSwitch : ITradingKillSwitch
    {
        public bool IsEngaged => false;
    }

    private sealed class CountingSends : IExecutionGateway
    {
        public int Sends { get; private set; }

        public Task<ExecutionWriteResult> PlaceOrder(
            ExecutionPlaceOrderCommand command,
            CancellationToken cancellationToken)
        {
            Sends++;
            throw new InvalidOperationException("an outage must not send");
        }

        public Task<ExecutionWriteResult> ModifyOrder(
            ExecutionModifyOrderCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExecutionWriteResult> CancelOrder(
            ExecutionCancelOrderCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExecutionWriteResult> ClosePosition(
            ExecutionClosePositionCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ExecutionWriteResult> PartialClosePosition(
            ExecutionPartialClosePositionCommand command,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class PracticeAccount(int accountId) : IMarketDataGateway
    {
        public string VenueId => "qa";

        public Task<IReadOnlyList<VenueAccount>> GetAccountsAsync(
            bool onlyActive,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VenueAccount>>(
            [
                new(accountId, AccountStage.Practice, CanTrade: true, IsVisible: true, Balance: 50_000m),
            ]);

        public Task<IReadOnlyList<VenueContract>> ResolveContractsAsync(
            InstrumentId instrument,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<VenueContract?> FindContractAsync(
            InstrumentId instrument,
            ContractExpiry expiry,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<Bar>> GetBarsAsync(
            string contractId,
            BarRange window,
            TimeSpan barSize,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<VenuePosition>> GetOpenPositionsAsync(
            int accountId,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<VenueOrder>> GetOrdersAsync(
            int accountId,
            BarRange? window,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<VenueTrade>> GetTradesAsync(
            int accountId,
            BarRange window,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
