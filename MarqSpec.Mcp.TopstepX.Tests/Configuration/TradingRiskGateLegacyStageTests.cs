using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using FluentAssertions;
using MarqSpec.Client.ProjectX;
using MarqSpec.Client.ProjectX.Api.Models;
using MarqSpec.Mcp.TopstepX.Configuration;
using MarqSpec.Mcp.TopstepX.Domain;
using MarqSpec.Mcp.TopstepX.Domain.MarketData;
using MarqSpec.Mcp.TopstepX.MarketData;
using MarqSpec.Mcp.TopstepX.Telemetry;
using MarqSpec.Mcp.TopstepX.Tests.Venue;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace MarqSpec.Mcp.TopstepX.Tests.Configuration;

/// <summary>
/// Stage cases from the closed gh#736 seam tests (gh#720), rewritten onto
/// <see cref="TradingRiskGate"/>, <see cref="IAccountStageResolver"/>, <see cref="HostTelemetry"/>,
/// and the current <see cref="ExecutionWriteResult"/>.
/// </summary>
[Trait("Gate", "ADR-0024")]
[Collection(HostTelemetryCollection.Name)]
public sealed class TradingRiskGateLegacyStageTests
{
    private const int LegacyPracticeAccountId = 900002;
    private const int EvaluationAccountId = 900003;
    private const int FundedAccountId = 900004;
    private const int UnknownAccountId = 900005;

    private const string LegacyPracticeName = "PRACTICEJUL3";
    private const string EvaluationName = "50KTC-V2-00000-00000003";
    private const string FundedName = "EXPRESS-V2-00000-00000004";
    private const string RenamedFundedName = "EXPRESS-V2-00000-00000001";
    private const string NearMissName = "PRACX-V2-00000-00000005";
    private const string MesContract = "CON.F.US.MES.Z26";
    private const long AcknowledgedOrderId = 7_000_001;

    private readonly ProjectXAccountStageResolver _stageResolver = new();

    [Fact]
    public async Task TradingRiskGate_AcknowledgesPlace_WhenAccountNameIsPracticeJul3()
    {
        _stageResolver.ResolveFromAccountName(LegacyPracticeName).Should().Be(AccountStage.Practice);

        using HostTelemetry telemetry = new();
        using MetricCollector<long> writes = Writes(telemetry);
        StageRig rig = OpenRig(
            telemetry,
            new TradingExecutionOptions { Enabled = true },
            Account(LegacyPracticeAccountId, LegacyPracticeName));

        TradingRiskGateResult result = await rig.Gate.SubmitPlace(
            Place(LegacyPracticeAccountId),
            Live(),
            CancellationToken.None);

        result.Outcome.Should().Be(TradingAttemptOutcome.Live);
        result.ParsedStage.Should().Be(AccountStage.Practice);
        result.Refusal.Should().BeNull();
        result.WriteResult.Should().Be(AcknowledgedWrite());
        AssertVendorCalls(rig.Vendor, place: 1);
        writes.GetMeasurementSnapshot().Should().NotContain(measurement =>
            Equals(measurement.Tags[HostTelemetry.RefusalTag], ExecutionWriteRefusal.UnknownAccountStage));
    }

    [Fact]
    public async Task TradingRiskGate_RefusesPlace_WhenAccountNameIsPracxNearMiss()
    {
        _stageResolver.ResolveFromAccountName(NearMissName).Should().Be(AccountStage.Unknown);

        using HostTelemetry telemetry = new();
        using MetricCollector<long> writes = Writes(telemetry);
        StageRig rig = OpenRig(
            telemetry,
            new TradingExecutionOptions { Enabled = true },
            Account(UnknownAccountId, NearMissName));

        TradingRiskGateResult result = await AssertRefusalAsync(
            telemetry,
            () => rig.Gate.SubmitPlace(Place(UnknownAccountId), Live(), CancellationToken.None),
            ExecutionOperation.PlaceOrderOperation,
            ExecutionWriteRefusal.UnknownAccountStage);

        result.ParsedStage.Should().Be(AccountStage.Unknown);
        result.Refusal.Should().Be(TradingExecutionRefusal.UnknownAccountStage(UnknownAccountId));
        result.WriteResult.Should().BeNull();
        AssertVendorCalls(rig.Vendor, place: 0);
        IReadOnlyList<CollectedMeasurement<long>> refused = writes.GetMeasurementSnapshot();
        refused.Should().ContainSingle();
        refused[0].Value.Should().Be(1);
        refused[0].Tags[HostTelemetry.RefusalTag].Should().Be(ExecutionWriteRefusal.UnknownAccountStage);
        refused[0].Tags[HostTelemetry.OperationTag].Should().Be(ExecutionOperation.PlaceOrderOperation);
        refused[0].Tags[HostTelemetry.ReplayTag].Should().Be(ExecutionWriteReplay.False);
    }

    [Fact]
    public async Task TradingRiskGate_RefusesDryRun_WhenAccountNameIsPracxNearMiss_AndCountsOnce()
    {
        using HostTelemetry telemetry = new();
        using MetricCollector<long> writes = Writes(telemetry);
        StageRig rig = OpenRig(
            telemetry,
            new TradingExecutionOptions { Enabled = true },
            Account(UnknownAccountId, NearMissName));

        TradingRiskGateResult result = await rig.Gate.SubmitPlace(
            Place(UnknownAccountId),
            new TradingRiskAttemptContext(DryRun: true),
            CancellationToken.None);

        result.Outcome.Should().Be(TradingAttemptOutcome.Refused);
        result.ParsedStage.Should().Be(AccountStage.Unknown);
        result.Refusal.Should().Be(TradingExecutionRefusal.UnknownAccountStage(UnknownAccountId));
        result.WriteResult.Should().BeNull();
        AssertVendorCalls(rig.Vendor, place: 0);
        IReadOnlyList<CollectedMeasurement<long>> refused = writes.GetMeasurementSnapshot();
        refused.Should().ContainSingle();
        refused[0].Value.Should().Be(1);
        refused[0].Tags[HostTelemetry.RefusalTag].Should().Be(ExecutionWriteRefusal.UnknownAccountStage);
        refused[0].Tags[HostTelemetry.OperationTag].Should().Be(ExecutionOperation.PlaceOrderOperation);
        refused[0].Tags[HostTelemetry.ReplayTag].Should().Be(ExecutionWriteReplay.False);
    }

    [Fact]
    public async Task TradingRiskGate_CountsUnknownStageOnce_ForEveryWriteTool()
    {
        using HostTelemetry telemetry = new();
        using MetricCollector<long> writes = Writes(telemetry);
        StageRig rig = OpenRig(
            telemetry,
            new TradingExecutionOptions { Enabled = true },
            Account(UnknownAccountId, NearMissName));

        await rig.Gate.SubmitPlace(Place(UnknownAccountId), Live(), CancellationToken.None);
        await rig.NextGate().SubmitModify(
            new ExecutionModifyOrderCommand(UnknownAccountId, OrderId: 1, Size: 1, null, null),
            Live(),
            CancellationToken.None);
        await rig.NextGate().SubmitCancel(
            new ExecutionCancelOrderCommand(UnknownAccountId, OrderId: 1),
            Live(),
            CancellationToken.None);
        await rig.NextGate().SubmitClose(
            new ExecutionClosePositionCommand(UnknownAccountId, MesContract),
            Live(),
            CancellationToken.None);
        await rig.NextGate().SubmitPartialClose(
            new ExecutionPartialClosePositionCommand(UnknownAccountId, MesContract, Size: 1),
            new TradingRiskAttemptContext(DryRun: true),
            CancellationToken.None);

        IReadOnlyList<CollectedMeasurement<long>> refused = writes.GetMeasurementSnapshot();
        refused.Should().HaveCount(5);
        foreach (CollectedMeasurement<long> measurement in refused)
        {
            measurement.Value.Should().Be(1);
            measurement.Tags[HostTelemetry.RefusalTag].Should().Be(ExecutionWriteRefusal.UnknownAccountStage);
            measurement.Tags[HostTelemetry.ReplayTag].Should().Be(ExecutionWriteReplay.False);
        }

        refused.Select(measurement => measurement.Tags[HostTelemetry.OperationTag]).Should().BeEquivalentTo(
            new object?[]
            {
                ExecutionOperation.PlaceOrderOperation,
                ExecutionOperation.ModifyOrderOperation,
                ExecutionOperation.CancelOrderOperation,
                ExecutionOperation.ClosePositionOperation,
                ExecutionOperation.PartialClosePositionOperation,
            });
    }

    [Fact]
    public async Task TradingRiskGate_WhenTheWritesListenerThrows_StillRefusesUnknownStage()
    {
        using HostTelemetry telemetry = new();
        using MetricCollector<long> faults = new(
            telemetry, HostTelemetry.Name, HostTelemetry.BookkeepingFaultsInstrument);
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, subscriber) =>
        {
            if (instrument.Name == HostTelemetry.ExecutionWritesInstrument
                && ReferenceEquals(instrument.Meter.Scope, telemetry))
            {
                subscriber.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) =>
            throw new InvalidOperationException("listener failed"));
        listener.Start();
        StageRig rig = OpenRig(
            telemetry,
            new TradingExecutionOptions { Enabled = true },
            Account(UnknownAccountId, NearMissName));

        TradingRiskGateResult result = await rig.Gate.SubmitPlace(
            Place(UnknownAccountId),
            Live(),
            CancellationToken.None);

        result.Outcome.Should().Be(TradingAttemptOutcome.Refused);
        result.Refusal.Should().Be(TradingExecutionRefusal.UnknownAccountStage(UnknownAccountId));
        result.WriteResult.Should().BeNull();
        AssertVendorCalls(rig.Vendor, place: 0);
        faults.GetMeasurementSnapshot().Should().ContainSingle().Which.Value.Should().Be(1);
    }

    [Theory]
    [InlineData(EvaluationAccountId, EvaluationName, AccountStage.Evaluation)]
    [InlineData(FundedAccountId, FundedName, AccountStage.Funded)]
    [InlineData(UnknownAccountId, NearMissName, AccountStage.Unknown)]
    public async Task TradingRiskGate_RefusesPlace_WhenAllowlistedAccountIsNotPractice(
        int accountId,
        string accountName,
        AccountStage stage)
    {
        _stageResolver.ResolveFromAccountName(accountName).Should().Be(stage);

        using HostTelemetry telemetry = new();
        using MetricCollector<long> writes = Writes(telemetry);
        StageRig rig = OpenRig(
            telemetry,
            new TradingExecutionOptions
            {
                Enabled = true,
                AllowedAccountIds = accountId.ToString(CultureInfo.InvariantCulture),
            },
            Account(accountId, accountName));

        string refusal = stage == AccountStage.Unknown
            ? ExecutionWriteRefusal.UnknownAccountStage
            : ExecutionWriteRefusal.NonPracticeAccount;

        TradingRiskGateResult result = await AssertRefusalAsync(
            telemetry,
            () => rig.Gate.SubmitPlace(Place(accountId), Live(), CancellationToken.None),
            ExecutionOperation.PlaceOrderOperation,
            refusal);

        TradingExecutionRefusal expected = stage == AccountStage.Unknown
            ? TradingExecutionRefusal.UnknownAccountStage(accountId)
            : TradingExecutionRefusal.NonPracticeAccountStage(accountId, stage);
        result.ParsedStage.Should().Be(stage);
        result.Refusal.Should().Be(expected);
        result.WriteResult.Should().BeNull();
        AssertVendorCalls(rig.Vendor, place: 0);
        IReadOnlyList<CollectedMeasurement<long>> refused = writes.GetMeasurementSnapshot()
            .Where(measurement =>
                Equals(measurement.Tags[HostTelemetry.RefusalTag], ExecutionWriteRefusal.UnknownAccountStage))
            .ToList();
        if (stage == AccountStage.Unknown)
        {
            refused.Should().ContainSingle();
            refused[0].Value.Should().Be(1);
            refused[0].Tags[HostTelemetry.OperationTag].Should().Be(ExecutionOperation.PlaceOrderOperation);
            refused[0].Tags[HostTelemetry.ReplayTag].Should().Be(ExecutionWriteReplay.False);
        }
        else
        {
            refused.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task TradingRiskGate_RefusesPlace_WhenAccountIsRenamedBetweenCalls()
    {
        _stageResolver.ResolveFromAccountName(LegacyPracticeName).Should().Be(AccountStage.Practice);
        _stageResolver.ResolveFromAccountName(RenamedFundedName).Should().Be(AccountStage.Funded);

        using HostTelemetry telemetry = new();
        using MetricCollector<long> writes = Writes(telemetry);
        StageRig rig = OpenRig(
            telemetry,
            new TradingExecutionOptions { Enabled = true },
            Account(LegacyPracticeAccountId, LegacyPracticeName));

        TradingRiskGateResult first = await rig.Gate.SubmitPlace(
            Place(LegacyPracticeAccountId),
            Live(),
            CancellationToken.None);

        first.Outcome.Should().Be(TradingAttemptOutcome.Live);
        first.ParsedStage.Should().Be(AccountStage.Practice);
        first.WriteResult.Should().Be(AcknowledgedWrite());
        AssertVendorCalls(rig.Vendor, place: 1);
        int readsBeforeRename = rig.Accounts.Fetches;
        readsBeforeRename.Should().BeGreaterThan(0);

        rig.Accounts.Accounts = [Account(LegacyPracticeAccountId, RenamedFundedName)];

        TradingRiskGateResult second = await AssertRefusalAsync(
            telemetry,
            () => rig.NextGate().SubmitPlace(Place(LegacyPracticeAccountId), Live(), CancellationToken.None),
            ExecutionOperation.PlaceOrderOperation,
            ExecutionWriteRefusal.NonPracticeAccount);

        second.ParsedStage.Should().Be(AccountStage.Funded);
        second.Refusal.Should().Be(
            TradingExecutionRefusal.NonPracticeAccountStage(LegacyPracticeAccountId, AccountStage.Funded));
        second.WriteResult.Should().BeNull();
        rig.Accounts.Fetches.Should().BeGreaterThan(readsBeforeRename);
        AssertVendorCalls(rig.Vendor, place: 1);
        writes.GetMeasurementSnapshot().Should().NotContain(measurement =>
            Equals(measurement.Tags[HostTelemetry.RefusalTag], ExecutionWriteRefusal.UnknownAccountStage));
    }

    private VenueAccount Account(int accountId, string accountName) =>
        new(
            accountId,
            _stageResolver.ResolveFromAccountName(accountName),
            CanTrade: true,
            IsVisible: true,
            Balance: 50_000m);

    private static ExecutionPlaceOrderCommand Place(int accountId) =>
        new(
            accountId,
            MesContract,
            ExecutionOrderType.Limit,
            VenueSide.Buy,
            Size: 1,
            LimitPrice: 6800.25m,
            StopPrice: null,
            TrailPrice: null);

    private static TradingRiskAttemptContext Live() => new(DryRun: false);

    private static MetricCollector<long> Writes(HostTelemetry telemetry) =>
        new(telemetry, HostTelemetry.Name, HostTelemetry.ExecutionWritesInstrument);

    private static ExecutionWriteResult AcknowledgedWrite() =>
        new(
            ExecutionWriteOutcome.BrokerAcknowledged,
            GateRefusal: null,
            HttpStatusCode: null,
            VendorErrorCode: null,
            OrderId: AcknowledgedOrderId);

    private static async Task<TradingRiskGateResult> AssertRefusalAsync(
        HostTelemetry telemetry,
        Func<Task<TradingRiskGateResult>> act,
        string operation,
        string refusal)
    {
        using MetricCollector<long> writes = new(
            telemetry,
            HostTelemetry.Name,
            HostTelemetry.ExecutionWritesInstrument);
        List<Activity> spans = [];
        using ActivityListener listener = Listen(spans, telemetry.Activities);

        TradingRiskGateResult result = await act();

        result.Outcome.Should().Be(TradingAttemptOutcome.Refused);

        CollectedMeasurement<long> measurement = writes.GetMeasurementSnapshot().Should().ContainSingle().Subject;
        measurement.Value.Should().Be(1);
        measurement.Tags[HostTelemetry.OperationTag].Should().Be(operation);
        measurement.Tags[HostTelemetry.RefusalTag].Should().Be(refusal);

        Activity span = spans
            .Where(candidate => candidate.OperationName.StartsWith("execution.", StringComparison.Ordinal))
            .Should()
            .ContainSingle()
            .Subject;
        span.OperationName.Should().Be("execution." + operation);
        span.GetTagItem(HostTelemetry.OperationTag).Should().Be(operation);
        span.GetTagItem(HostTelemetry.RefusalTag).Should().Be(refusal);

        return result;
    }

    private static void AssertVendorCalls(ExecutionOrderClient vendor, int place)
    {
        vendor.PlaceOrderCount.Should().Be(place);
        vendor.ModifyOrderCount.Should().Be(0);
        vendor.CancelOrderCount.Should().Be(0);
        vendor.ClosePositionCount.Should().Be(0);
        vendor.PartialClosePositionCount.Should().Be(0);
    }

    private StageRig OpenRig(
        HostTelemetry telemetry,
        TradingExecutionOptions options,
        VenueAccount account)
    {
        (ExecutionOrderClient vendor, IProjectXApiClient client) = ExecutionOrderClient.Create();
        vendor.PlaceOrderAnswer = static (_, _) => Task.FromResult(new PlaceOrderResponse
        {
            Success = true,
            OrderId = AcknowledgedOrderId,
            ErrorCode = 0,
        });

        MutableAccountsGateway accounts = new(account);
        TradingExecutionGate executionGate = new(Options.Create(options), new FakeKillSwitch(), _stageResolver);
        InstrumentRegistry registry = new(Options.Create(new MarketDataOptions { Instruments = "MES" }));
        TradingContractDenylist denylist = new(Options.Create(options), registry);
        TradingRiskGate NewGate()
        {
            TradingWriteVenueAccountCache accountCache = new();
            NullTradingUnknownStageRefusalSignal signal = new();
            IExecutionGateway gateway = new ProjectXExecutionGateway(
                executionGate,
                accounts,
                signal,
                client,
                accountCache,
                telemetry,
                NullLogger<ProjectXExecutionGateway>.Instance);

            return new(
                executionGate,
                gateway,
                accounts,
                _stageResolver,
                signal,
                new FakeDailyCapCoordinator(),
                new RecordingAuditLog(),
                denylist,
                accountCache,
                telemetry);
        }

        return new StageRig(vendor, NewGate(), accounts, NewGate);
    }

    private static ActivityListener Listen(List<Activity> into, ActivitySource source)
    {
        ActivityListener listener = new()
        {
            ShouldListenTo = candidate => ReferenceEquals(candidate, source),
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = into.Add,
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private sealed record StageRig(
        ExecutionOrderClient Vendor,
        TradingRiskGate Gate,
        MutableAccountsGateway Accounts,
        Func<TradingRiskGate> NextGate);

    private sealed class MutableAccountsGateway(VenueAccount account) : IMarketDataGateway
    {
        public IReadOnlyList<VenueAccount> Accounts { get; set; } = [account];

        public int Fetches { get; private set; }

        public string VenueId => "stub";

        public Task<IReadOnlyList<VenueAccount>> GetAccountsAsync(
            bool onlyActive,
            CancellationToken cancellationToken)
        {
            _ = onlyActive;
            Fetches++;
            return Task.FromResult(Accounts);
        }

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

    private sealed class FakeKillSwitch : ITradingKillSwitch
    {
        public bool IsEngaged { get; set; }
    }

    private sealed class RecordingAuditLog : ITradingAuditLogWriter
    {
        public Task WriteAsync(TradingAuditWriteRequest request, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class FakeDailyCapCoordinator : ITradingDailyCapCoordinator
    {
        public bool LastReleaseFailed => false;

        public Task<IAsyncDisposable> AcquireAccountDayLockAsync(
            int accountId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IAsyncDisposable>(new NoopLock());

        public Task<int> GetConservativeContractTotalTodayUtcAsync(
            int accountId,
            CancellationToken cancellationToken) =>
            Task.FromResult(0);

        private sealed class NoopLock : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
