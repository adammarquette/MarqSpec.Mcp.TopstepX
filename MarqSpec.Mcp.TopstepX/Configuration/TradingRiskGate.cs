using MarqSpec.Mcp.TopstepX.Telemetry;
using MarqSpec.Mcp.TopstepX.Venue;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// Central trading gate: master switch, practice-only stage, caps, dry-run preview, and audit (ADR-0024, gh#711).
/// </summary>
/// <remarks>
/// MCP tools call this type only — never the execution gateway directly. Every member that touches
/// stage resolution, allowlist, denylist, or the gateway re-checks
/// <see cref="TradingExecutionGate.RefuseIfDisabledOrHalted"/> first. Daily caps charge on
/// <see cref="TradingAuditOutcomes.Attempted"/> rows; outcome rows use zero contract count. A gateway
/// refusal after the write-ahead appends a second Attempted row with the negated count so the
/// session-day sum nets to zero.
/// </remarks>
public sealed class TradingRiskGate
{
    private readonly TradingExecutionGate _executionGate;
    private readonly IExecutionGateway _gateway;
    private readonly IMarketDataGateway _marketData;
    private readonly IAccountStageResolver _stageResolver;
    private readonly ITradingUnknownStageRefusalSignal _unknownStageSignal;
    private readonly ITradingDailyCapCoordinator _dailyCap;
    private readonly ITradingAuditLogWriter _auditLog;
    private readonly TradingContractDenylist _contractDenylist;
    private readonly TradingWriteVenueAccountCache _accountCache;
    private readonly HostTelemetry _telemetry;
    private readonly ITradingIdempotencyStore _idempotency;

    /// <summary>Creates the risk gate.</summary>
    public TradingRiskGate(
        TradingExecutionGate executionGate,
        IExecutionGateway gateway,
        IMarketDataGateway marketData,
        IAccountStageResolver stageResolver,
        ITradingUnknownStageRefusalSignal unknownStageSignal,
        ITradingDailyCapCoordinator dailyCap,
        ITradingAuditLogWriter auditLog,
        TradingContractDenylist contractDenylist,
        TradingWriteVenueAccountCache accountCache,
        HostTelemetry telemetry,
        ITradingIdempotencyStore idempotency)
    {
        _executionGate = executionGate;
        _gateway = gateway;
        _marketData = marketData;
        _stageResolver = stageResolver;
        _unknownStageSignal = unknownStageSignal;
        _dailyCap = dailyCap;
        _auditLog = auditLog;
        _contractDenylist = contractDenylist;
        _accountCache = accountCache;
        _telemetry = telemetry;
        _idempotency = idempotency;
    }

    /// <summary>Places an order when every gate layer permits.</summary>
    public Task<TradingRiskGateResult> SubmitPlace(
        ExecutionPlaceOrderCommand command,
        TradingRiskAttemptContext context,
        CancellationToken cancellationToken) =>
        RunWithTelemetry(
            ExecutionOperation.PlaceOrderOperation,
            command.AccountId,
            TradingMcpTools.PlaceOrderOperation,
            TradingRequestHasher.HashPlace(command),
            context,
            lease => RunPlaceAsync(command, context, lease, cancellationToken),
            cancellationToken);

    /// <summary>Modifies an order when every gate layer permits.</summary>
    public Task<TradingRiskGateResult> SubmitModify(
        ExecutionModifyOrderCommand command,
        TradingRiskAttemptContext context,
        CancellationToken cancellationToken) =>
        RunWithTelemetry(
            ExecutionOperation.ModifyOrderOperation,
            command.AccountId,
            TradingMcpTools.ModifyOrderOperation,
            TradingRequestHasher.HashModify(command),
            context,
            lease => RunModifyAsync(command, context, lease, cancellationToken),
            cancellationToken);

    /// <summary>Cancels an order when every gate layer permits.</summary>
    public Task<TradingRiskGateResult> SubmitCancel(
        ExecutionCancelOrderCommand command,
        TradingRiskAttemptContext context,
        CancellationToken cancellationToken) =>
        RunWithTelemetry(
            ExecutionOperation.CancelOrderOperation,
            command.AccountId,
            TradingMcpTools.CancelOrderOperation,
            TradingRequestHasher.HashCancel(command),
            context,
            lease => RunCancelAsync(command, context, lease, cancellationToken),
            cancellationToken);

    /// <summary>Flattens a position when every gate layer permits.</summary>
    public Task<TradingRiskGateResult> SubmitClose(
        ExecutionClosePositionCommand command,
        TradingRiskAttemptContext context,
        CancellationToken cancellationToken) =>
        RunWithTelemetry(
            ExecutionOperation.ClosePositionOperation,
            command.AccountId,
            TradingMcpTools.ClosePositionOperation,
            TradingRequestHasher.HashClose(command),
            context,
            lease => RunCloseAsync(command, context, lease, cancellationToken),
            cancellationToken);

    /// <summary>Partially closes a position when every gate layer permits.</summary>
    public Task<TradingRiskGateResult> SubmitPartialClose(
        ExecutionPartialClosePositionCommand command,
        TradingRiskAttemptContext context,
        CancellationToken cancellationToken) =>
        RunWithTelemetry(
            ExecutionOperation.PartialClosePositionOperation,
            command.AccountId,
            TradingMcpTools.PartialClosePositionOperation,
            TradingRequestHasher.HashPartialClose(command),
            context,
            lease => RunPartialCloseAsync(command, context, lease, cancellationToken),
            cancellationToken);

    private async Task<TradingRiskGateResult> RunWithTelemetry(
        string operation,
        int accountId,
        string tool,
        string requestHash,
        TradingRiskAttemptContext context,
        Func<TradingIdempotencyLease, Task<TradingRiskGateResult>> body,
        CancellationToken cancellationToken)
    {
        _accountCache.Clear();
        using HostTelemetry.ExecutionWriteScope write = _telemetry.ExecutionWrite(operation);
        TradingIdempotencyLease? lease = null;
        try
        {
            lease = await _idempotency.LeaseAsync(
                context.IdempotencyKey,
                accountId,
                tool,
                requestHash,
                cancellationToken).ConfigureAwait(false);

            if (lease.Prior is { } prior)
            {
                write.Outcome(ExecutionWriteRefusalForResult.For(prior));
                return prior;
            }

            TradingRiskGateResult result = await body(lease).ConfigureAwait(false);
            lease.Remember(result);
            write.Outcome(ExecutionWriteRefusalForResult.For(result));
            return result;
        }
        catch (Exception)
        {
            write.Outcome(ExecutionWriteRefusal.Error);
            throw;
        }
        finally
        {
            if (lease is not null)
            {
                await lease.DisposeAsync().ConfigureAwait(false);
            }

            _accountCache.Clear();
        }
    }

    private async Task<TradingRiskGateResult> RunPlaceAsync(
        ExecutionPlaceOrderCommand command,
        TradingRiskAttemptContext context,
        TradingIdempotencyLease lease,
        CancellationToken cancellationToken)
    {
        AuditEnvelope audit = Envelope(
            TradingMcpTools.PlaceOrderOperation,
            TradingRequestHasher.HashPlace(command),
            context);

        if (_executionGate.RefuseIfDisabledOrHalted() is TradingExecutionRefusal masterOrHalt)
        {
            if (masterOrHalt.Code == TradingExecutionRefusal.TradingDisabled.Code)
            {
                return RefuseWithoutSideEffects(masterOrHalt);
            }

            return await RefuseAsync(
                command.AccountId,
                ParsedStage.Unresolved,
                audit,
                contractCount: command.Size,
                masterOrHalt,
                cancellationToken).ConfigureAwait(false);
        }

        if (await BlockedByIdempotencyAsync(command.AccountId, audit, context, lease, cancellationToken)
                .ConfigureAwait(false) is { } idempotencyBlock)
        {
            return idempotencyBlock;
        }

        (AccountStage stage, TradingExecutionRefusal? stageRefusal) = await ResolvePracticeStageAsync(
            command.AccountId,
            cancellationToken).ConfigureAwait(false);
        if (stageRefusal is TradingExecutionRefusal stageBlock)
        {
            return await RefuseAsync(
                command.AccountId,
                FormatStage(stage),
                audit,
                contractCount: command.Size,
                stageBlock,
                cancellationToken).ConfigureAwait(false);
        }

        if (RefuseAllowlist(command.AccountId) is TradingExecutionRefusal allowlist)
        {
            return await RefuseAsync(
                command.AccountId,
                FormatStage(stage),
                audit,
                contractCount: command.Size,
                allowlist,
                cancellationToken).ConfigureAwait(false);
        }

        if (RefuseContract(command.ContractId) is TradingExecutionRefusal symbol)
        {
            return await RefuseAsync(
                command.AccountId,
                FormatStage(stage),
                audit,
                contractCount: command.Size,
                symbol,
                cancellationToken).ConfigureAwait(false);
        }

        if (_executionGate.RefuseIfOrderSizeExceedsLimit(command.Size) is TradingExecutionRefusal size)
        {
            return await RefuseAsync(
                command.AccountId,
                FormatStage(stage),
                audit,
                contractCount: command.Size,
                size,
                cancellationToken).ConfigureAwait(false);
        }

        await using (await _dailyCap.AcquireAccountDayLockAsync(command.AccountId, cancellationToken)
                         .ConfigureAwait(false))
        {
            if (await RefuseDailyCapAsync(command.AccountId, command.Size, cancellationToken).ConfigureAwait(false)
                is TradingExecutionRefusal daily)
            {
                return await RefuseAsync(
                    command.AccountId,
                    FormatStage(stage),
                    audit,
                    contractCount: command.Size,
                    daily,
                    cancellationToken).ConfigureAwait(false);
            }

            if (context.DryRun)
            {
                return await PreviewAsync(
                    command.AccountId,
                    stage,
                    audit,
                    contractCount: command.Size,
                    cancellationToken).ConfigureAwait(false);
            }

            return await TransmitWithCapChargeAsync(
                command.AccountId,
                stage,
                audit,
                capChargeSize: command.Size,
                ct => _gateway.PlaceOrder(command, ct),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<TradingRiskGateResult> RunModifyAsync(
        ExecutionModifyOrderCommand command,
        TradingRiskAttemptContext context,
        TradingIdempotencyLease lease,
        CancellationToken cancellationToken)
    {
        AuditEnvelope audit = Envelope(
            TradingMcpTools.ModifyOrderOperation,
            TradingRequestHasher.HashModify(command),
            context);
        int capSize = command.Size ?? 0;

        if (_executionGate.RefuseIfDisabledOrHalted() is TradingExecutionRefusal masterOrHalt)
        {
            if (masterOrHalt.Code == TradingExecutionRefusal.TradingDisabled.Code)
            {
                return RefuseWithoutSideEffects(masterOrHalt);
            }

            return await RefuseAsync(
                command.AccountId,
                ParsedStage.Unresolved,
                audit,
                contractCount: capSize,
                masterOrHalt,
                cancellationToken).ConfigureAwait(false);
        }

        if (await BlockedByIdempotencyAsync(command.AccountId, audit, context, lease, cancellationToken)
                .ConfigureAwait(false) is { } idempotencyBlock)
        {
            return idempotencyBlock;
        }

        (AccountStage stage, TradingExecutionRefusal? stageRefusal) = await ResolvePracticeStageAsync(
            command.AccountId,
            cancellationToken).ConfigureAwait(false);
        if (stageRefusal is TradingExecutionRefusal stageBlock)
        {
            return await RefuseAsync(
                command.AccountId,
                FormatStage(stage),
                audit,
                contractCount: capSize,
                stageBlock,
                cancellationToken).ConfigureAwait(false);
        }

        if (RefuseAllowlist(command.AccountId) is TradingExecutionRefusal allowlist)
        {
            return await RefuseAsync(
                command.AccountId,
                FormatStage(stage),
                audit,
                contractCount: capSize,
                allowlist,
                cancellationToken).ConfigureAwait(false);
        }

        if (await RefuseModifyContractAsync(command, cancellationToken).ConfigureAwait(false)
            is TradingExecutionRefusal contractRefusal)
        {
            return await RefuseAsync(
                command.AccountId,
                FormatStage(stage),
                audit,
                contractCount: capSize,
                contractRefusal,
                cancellationToken).ConfigureAwait(false);
        }

        if (command.Size is int modifySize
            && _executionGate.RefuseIfOrderSizeExceedsLimit(modifySize) is TradingExecutionRefusal size)
        {
            return await RefuseAsync(
                command.AccountId,
                FormatStage(stage),
                audit,
                contractCount: modifySize,
                size,
                cancellationToken).ConfigureAwait(false);
        }

        await using (await _dailyCap.AcquireAccountDayLockAsync(command.AccountId, cancellationToken)
                         .ConfigureAwait(false))
        {
            if (capSize > 0
                && await RefuseDailyCapAsync(command.AccountId, capSize, cancellationToken).ConfigureAwait(false)
                    is TradingExecutionRefusal daily)
            {
                return await RefuseAsync(
                    command.AccountId,
                    FormatStage(stage),
                    audit,
                    contractCount: capSize,
                    daily,
                    cancellationToken).ConfigureAwait(false);
            }

            if (context.DryRun)
            {
                return await PreviewAsync(
                    command.AccountId,
                    stage,
                    audit,
                    contractCount: capSize,
                    cancellationToken).ConfigureAwait(false);
            }

            return await TransmitWithCapChargeAsync(
                command.AccountId,
                stage,
                audit,
                capChargeSize: capSize,
                ct => _gateway.ModifyOrder(command, ct),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<TradingRiskGateResult> RunCancelAsync(
        ExecutionCancelOrderCommand command,
        TradingRiskAttemptContext context,
        TradingIdempotencyLease lease,
        CancellationToken cancellationToken)
    {
        AuditEnvelope audit = Envelope(
            TradingMcpTools.CancelOrderOperation,
            TradingRequestHasher.HashCancel(command),
            context);

        if (_executionGate.RefuseIfDisabledOrHalted() is TradingExecutionRefusal masterOrHalt)
        {
            if (masterOrHalt.Code == TradingExecutionRefusal.TradingDisabled.Code)
            {
                return RefuseWithoutSideEffects(masterOrHalt);
            }

            return await RefuseAsync(
                command.AccountId,
                ParsedStage.Unresolved,
                audit,
                contractCount: 0,
                masterOrHalt,
                cancellationToken).ConfigureAwait(false);
        }

        if (await BlockedByIdempotencyAsync(command.AccountId, audit, context, lease, cancellationToken)
                .ConfigureAwait(false) is { } idempotencyBlock)
        {
            return idempotencyBlock;
        }

        (AccountStage stage, TradingExecutionRefusal? stageRefusal) = await ResolvePracticeStageAsync(
            command.AccountId,
            cancellationToken).ConfigureAwait(false);
        if (stageRefusal is TradingExecutionRefusal stageBlock)
        {
            return await RefuseAsync(
                command.AccountId,
                FormatStage(stage),
                audit,
                contractCount: 0,
                stageBlock,
                cancellationToken).ConfigureAwait(false);
        }

        if (RefuseAllowlist(command.AccountId) is TradingExecutionRefusal allowlist)
        {
            return await RefuseAsync(
                command.AccountId,
                FormatStage(stage),
                audit,
                contractCount: 0,
                allowlist,
                cancellationToken).ConfigureAwait(false);
        }

        if (context.DryRun)
        {
            return await PreviewAsync(
                command.AccountId,
                stage,
                audit,
                contractCount: 0,
                cancellationToken).ConfigureAwait(false);
        }

        return await TransmitWithoutCapChargeAsync(
            command.AccountId,
            stage,
            audit,
            ct => _gateway.CancelOrder(command, ct),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<TradingRiskGateResult> RunCloseAsync(
        ExecutionClosePositionCommand command,
        TradingRiskAttemptContext context,
        TradingIdempotencyLease lease,
        CancellationToken cancellationToken)
    {
        AuditEnvelope audit = Envelope(
            TradingMcpTools.ClosePositionOperation,
            TradingRequestHasher.HashClose(command),
            context);

        if (_executionGate.RefuseIfDisabledOrHalted() is TradingExecutionRefusal masterOrHalt)
        {
            if (masterOrHalt.Code == TradingExecutionRefusal.TradingDisabled.Code)
            {
                return RefuseWithoutSideEffects(masterOrHalt);
            }

            return await RefuseAsync(
                command.AccountId,
                ParsedStage.Unresolved,
                audit,
                contractCount: 0,
                masterOrHalt,
                cancellationToken).ConfigureAwait(false);
        }

        if (await BlockedByIdempotencyAsync(command.AccountId, audit, context, lease, cancellationToken)
                .ConfigureAwait(false) is { } idempotencyBlock)
        {
            return idempotencyBlock;
        }

        (AccountStage stage, TradingExecutionRefusal? stageRefusal) = await ResolvePracticeStageAsync(
            command.AccountId,
            cancellationToken).ConfigureAwait(false);
        if (stageRefusal is TradingExecutionRefusal stageBlock)
        {
            return await RefuseAsync(
                command.AccountId,
                FormatStage(stage),
                audit,
                contractCount: 0,
                stageBlock,
                cancellationToken).ConfigureAwait(false);
        }

        if (RefuseAllowlist(command.AccountId) is TradingExecutionRefusal allowlist)
        {
            return await RefuseAsync(
                command.AccountId,
                FormatStage(stage),
                audit,
                contractCount: 0,
                allowlist,
                cancellationToken).ConfigureAwait(false);
        }

        if (RefuseContract(command.ContractId) is TradingExecutionRefusal symbol)
        {
            return await RefuseAsync(
                command.AccountId,
                FormatStage(stage),
                audit,
                contractCount: 0,
                symbol,
                cancellationToken).ConfigureAwait(false);
        }

        if (context.DryRun)
        {
            return await PreviewAsync(
                command.AccountId,
                stage,
                audit,
                contractCount: 0,
                cancellationToken).ConfigureAwait(false);
        }

        return await TransmitWithoutCapChargeAsync(
            command.AccountId,
            stage,
            audit,
            ct => _gateway.ClosePosition(command, ct),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<TradingRiskGateResult> RunPartialCloseAsync(
        ExecutionPartialClosePositionCommand command,
        TradingRiskAttemptContext context,
        TradingIdempotencyLease lease,
        CancellationToken cancellationToken)
    {
        AuditEnvelope audit = Envelope(
            TradingMcpTools.PartialClosePositionOperation,
            TradingRequestHasher.HashPartialClose(command),
            context);

        if (_executionGate.RefuseIfDisabledOrHalted() is TradingExecutionRefusal masterOrHalt)
        {
            if (masterOrHalt.Code == TradingExecutionRefusal.TradingDisabled.Code)
            {
                return RefuseWithoutSideEffects(masterOrHalt);
            }

            return await RefuseAsync(
                command.AccountId,
                ParsedStage.Unresolved,
                audit,
                contractCount: command.Size,
                masterOrHalt,
                cancellationToken).ConfigureAwait(false);
        }

        if (await BlockedByIdempotencyAsync(command.AccountId, audit, context, lease, cancellationToken)
                .ConfigureAwait(false) is { } idempotencyBlock)
        {
            return idempotencyBlock;
        }

        (AccountStage stage, TradingExecutionRefusal? stageRefusal) = await ResolvePracticeStageAsync(
            command.AccountId,
            cancellationToken).ConfigureAwait(false);
        if (stageRefusal is TradingExecutionRefusal stageBlock)
        {
            return await RefuseAsync(
                command.AccountId,
                FormatStage(stage),
                audit,
                contractCount: command.Size,
                stageBlock,
                cancellationToken).ConfigureAwait(false);
        }

        if (RefuseAllowlist(command.AccountId) is TradingExecutionRefusal allowlist)
        {
            return await RefuseAsync(
                command.AccountId,
                FormatStage(stage),
                audit,
                contractCount: command.Size,
                allowlist,
                cancellationToken).ConfigureAwait(false);
        }

        if (RefuseContract(command.ContractId) is TradingExecutionRefusal symbol)
        {
            return await RefuseAsync(
                command.AccountId,
                FormatStage(stage),
                audit,
                contractCount: command.Size,
                symbol,
                cancellationToken).ConfigureAwait(false);
        }

        if (_executionGate.RefuseIfOrderSizeExceedsLimit(command.Size) is TradingExecutionRefusal size)
        {
            return await RefuseAsync(
                command.AccountId,
                FormatStage(stage),
                audit,
                contractCount: command.Size,
                size,
                cancellationToken).ConfigureAwait(false);
        }

        await using (await _dailyCap.AcquireAccountDayLockAsync(command.AccountId, cancellationToken)
                         .ConfigureAwait(false))
        {
            if (await RefuseDailyCapAsync(command.AccountId, command.Size, cancellationToken).ConfigureAwait(false)
                is TradingExecutionRefusal daily)
            {
                return await RefuseAsync(
                    command.AccountId,
                    FormatStage(stage),
                    audit,
                    contractCount: command.Size,
                    daily,
                    cancellationToken).ConfigureAwait(false);
            }

            if (context.DryRun)
            {
                return await PreviewAsync(
                    command.AccountId,
                    stage,
                    audit,
                    contractCount: command.Size,
                    cancellationToken).ConfigureAwait(false);
            }

            return await TransmitWithCapChargeAsync(
                command.AccountId,
                stage,
                audit,
                capChargeSize: command.Size,
                ct => _gateway.PartialClosePosition(command, ct),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<TradingExecutionRefusal?> RefuseModifyContractAsync(
        ExecutionModifyOrderCommand command,
        CancellationToken cancellationToken)
    {
        if (_executionGate.RefuseIfDisabledOrHalted() is TradingExecutionRefusal disabled)
        {
            return disabled;
        }

        IReadOnlyList<VenueOrder> orders = await _marketData
            .GetOrdersAsync(command.AccountId, window: null, cancellationToken)
            .ConfigureAwait(false);

        VenueOrder? order = orders.FirstOrDefault(candidate => candidate.OrderId == command.OrderId);
        if (order is null)
        {
            return TradingExecutionRefusal.OrderNotFound(command.OrderId);
        }

        return RefuseContract(order.ContractId);
    }

    private async Task<(AccountStage Stage, TradingExecutionRefusal? Refusal)> ResolvePracticeStageAsync(
        int accountId,
        CancellationToken cancellationToken)
    {
        if (_executionGate.RefuseIfDisabledOrHalted() is TradingExecutionRefusal disabled)
        {
            return (AccountStage.Unknown, disabled);
        }

        IReadOnlyList<VenueAccount> accounts = await _marketData
            .GetAccountsAsync(onlyActive: true, cancellationToken)
            .ConfigureAwait(false);

        VenueAccount? account = accounts.FirstOrDefault(candidate => candidate.AccountId == accountId);
        if (account is null)
        {
            return (AccountStage.Unknown, TradingExecutionRefusal.AccountNotFound(accountId));
        }

        if (!account.CanTrade)
        {
            return (account.Stage, TradingExecutionRefusal.AccountNotActive(accountId));
        }

        _accountCache.Remember(account);

        AccountStage stage = _stageResolver.ResolveFromFreshVenueAccount(account);
        if (stage != AccountStage.Practice)
        {
            TradingExecutionRefusal? stageRefusal = _executionGate.RefuseIfStageBlocksWrite(
                stage,
                accountId,
                _unknownStageSignal);
            return (stage, stageRefusal);
        }

        return (stage, null);
    }

    private TradingExecutionRefusal? RefuseAllowlist(int accountId)
    {
        if (_executionGate.RefuseIfDisabledOrHalted() is TradingExecutionRefusal disabled)
        {
            return disabled;
        }

        return _executionGate.RefuseIfNotAllowlisted(accountId);
    }

    private TradingExecutionRefusal? RefuseContract(string contractId)
    {
        if (_executionGate.RefuseIfDisabledOrHalted() is TradingExecutionRefusal disabled)
        {
            return disabled;
        }

        return _contractDenylist.RefuseIfContractDenied(contractId);
    }

    private async Task<TradingExecutionRefusal?> RefuseDailyCapAsync(
        int accountId,
        int requestedSize,
        CancellationToken cancellationToken)
    {
        if (_executionGate.RefuseIfDisabledOrHalted() is TradingExecutionRefusal disabled)
        {
            return disabled;
        }

        int already = await _dailyCap
            .GetConservativeContractTotalTodayUtcAsync(accountId, cancellationToken)
            .ConfigureAwait(false);
        return _executionGate.RefuseIfDailyLimitExceeded(accountId, already, requestedSize);
    }

    private async Task<TradingRiskGateResult> TransmitWithCapChargeAsync(
        int accountId,
        AccountStage stage,
        AuditEnvelope audit,
        int capChargeSize,
        Func<CancellationToken, Task<ExecutionWriteResult>> send,
        CancellationToken cancellationToken)
    {
        if (_executionGate.RefuseIfDisabledOrHalted() is TradingExecutionRefusal disabled)
        {
            return await RefuseAsync(
                accountId,
                FormatStage(stage),
                audit,
                capChargeSize,
                disabled,
                cancellationToken).ConfigureAwait(false);
        }

        if (await RefuseGatewayWriteUnlessPermittedAsync(accountId, cancellationToken).ConfigureAwait(false)
            is TradingExecutionRefusal gatewayRefusal)
        {
            return await RefuseAsync(
                accountId,
                FormatStage(stage),
                audit,
                capChargeSize,
                gatewayRefusal,
                cancellationToken).ConfigureAwait(false);
        }

        await WriteAuditAsync(
            accountId,
            FormatStage(stage),
            audit,
            TradingAuditOutcomes.Attempted,
            capChargeSize,
            venueErrorCode: null,
            cancellationToken).ConfigureAwait(false);

        return await SendAndFinalizeAsync(
            accountId,
            stage,
            audit,
            capChargeSize,
            send,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<TradingRiskGateResult> TransmitWithoutCapChargeAsync(
        int accountId,
        AccountStage stage,
        AuditEnvelope audit,
        Func<CancellationToken, Task<ExecutionWriteResult>> send,
        CancellationToken cancellationToken)
    {
        if (_executionGate.RefuseIfDisabledOrHalted() is TradingExecutionRefusal disabled)
        {
            return await RefuseAsync(
                accountId,
                FormatStage(stage),
                audit,
                contractCount: 0,
                disabled,
                cancellationToken).ConfigureAwait(false);
        }

        if (await RefuseGatewayWriteUnlessPermittedAsync(accountId, cancellationToken).ConfigureAwait(false)
            is TradingExecutionRefusal gatewayRefusal)
        {
            return await RefuseAsync(
                accountId,
                FormatStage(stage),
                audit,
                contractCount: 0,
                gatewayRefusal,
                cancellationToken).ConfigureAwait(false);
        }

        await WriteAuditAsync(
            accountId,
            FormatStage(stage),
            audit,
            TradingAuditOutcomes.Attempted,
            contractCount: 0,
            venueErrorCode: null,
            cancellationToken).ConfigureAwait(false);

        return await SendAndFinalizeAsync(
            accountId,
            stage,
            audit,
            capChargeApplied: 0,
            send,
            cancellationToken).ConfigureAwait(false);
    }

    private Task<TradingExecutionRefusal?> RefuseGatewayWriteUnlessPermittedAsync(
        int accountId,
        CancellationToken cancellationToken) =>
        _executionGate.RefuseWriteUnlessPermittedAsync(
            accountId,
            _marketData,
            _unknownStageSignal,
            cancellationToken,
            _accountCache.TryGet(accountId));

    private async Task<TradingRiskGateResult> SendAndFinalizeAsync(
        int accountId,
        AccountStage stage,
        AuditEnvelope audit,
        int capChargeApplied,
        Func<CancellationToken, Task<ExecutionWriteResult>> send,
        CancellationToken cancellationToken)
    {
        ExecutionWriteResult write;
        try
        {
            write = await send(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await WriteAuditAsync(
                accountId,
                FormatStage(stage),
                audit,
                TradingAuditOutcomes.Unknown,
                contractCount: 0,
                venueErrorCode: ex.GetType().Name,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return await MapWriteAsync(accountId, stage, audit, capChargeApplied, write).ConfigureAwait(false);
    }

    private async Task<TradingRiskGateResult> PreviewAsync(
        int accountId,
        AccountStage stage,
        AuditEnvelope audit,
        int contractCount,
        CancellationToken cancellationToken)
    {
        if (_executionGate.RefuseIfDisabledOrHalted() is TradingExecutionRefusal disabled)
        {
            return await RefuseAsync(
                accountId,
                FormatStage(stage),
                audit,
                contractCount,
                disabled,
                cancellationToken).ConfigureAwait(false);
        }

        await WriteAuditAsync(
            accountId,
            FormatStage(stage),
            audit,
            TradingAuditOutcomes.Preview,
            contractCount,
            venueErrorCode: null,
            cancellationToken).ConfigureAwait(false);

        return new TradingRiskGateResult(
            TradingAttemptOutcome.Preview,
            stage,
            Refusal: null,
            WriteResult: null,
            Preview: new TradingExecutionPreview(accountId, stage, audit.Tool, WouldTransmit: true));
    }

    private static TradingRiskGateResult RefuseWithoutSideEffects(TradingExecutionRefusal refusal) =>
        new(
            TradingAttemptOutcome.Refused,
            AccountStage.Unknown,
            refusal,
            WriteResult: null,
            Preview: null);

    private async Task<TradingRiskGateResult> RefuseAsync(
        int accountId,
        string parsedStage,
        AuditEnvelope audit,
        int contractCount,
        TradingExecutionRefusal refusal,
        CancellationToken cancellationToken)
    {
        await WriteAuditAsync(
            accountId,
            parsedStage,
            audit,
            TradingAuditOutcomes.Refused,
            contractCount,
            venueErrorCode: refusal.Code,
            cancellationToken).ConfigureAwait(false);

        AccountStage stageEnum = Enum.TryParse(parsedStage, out AccountStage parsed)
            ? parsed
            : AccountStage.Unknown;

        return new TradingRiskGateResult(
            TradingAttemptOutcome.Refused,
            stageEnum,
            refusal,
            WriteResult: null,
            Preview: null);
    }

    private async Task<TradingRiskGateResult> MapWriteAsync(
        int accountId,
        AccountStage stage,
        AuditEnvelope audit,
        int capChargeApplied,
        ExecutionWriteResult write)
    {
        if (write.GateRefusal is TradingExecutionRefusal gateRefusal)
        {
            if (capChargeApplied != 0)
            {
                await WriteAuditAsync(
                    accountId,
                    FormatStage(stage),
                    audit,
                    TradingAuditOutcomes.Attempted,
                    contractCount: -capChargeApplied,
                    venueErrorCode: gateRefusal.Code,
                    CancellationToken.None).ConfigureAwait(false);
            }

            return await RefuseAsync(
                accountId,
                FormatStage(stage),
                audit,
                contractCount: 0,
                gateRefusal,
                CancellationToken.None).ConfigureAwait(false);
        }

        (string outcome, TradingAttemptOutcome attemptOutcome) = MapExecutionOutcome(write.Outcome);

        string? venueCode = write.VendorErrorCode?.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await WriteAuditAsync(
            accountId,
            FormatStage(stage),
            audit,
            outcome,
            contractCount: 0,
            venueCode,
            CancellationToken.None).ConfigureAwait(false);

        return new TradingRiskGateResult(
            attemptOutcome,
            stage,
            write.GateRefusal,
            write,
            Preview: null);
    }

    private async Task WriteAuditAsync(
        int accountId,
        string parsedStage,
        AuditEnvelope audit,
        string outcome,
        int contractCount,
        string? venueErrorCode,
        CancellationToken cancellationToken)
    {
        await _auditLog.WriteAsync(
            new TradingAuditWriteRequest(
                accountId,
                parsedStage,
                audit.Tool,
                audit.IdempotencyKey,
                audit.RequestHash,
                audit.DryRun,
                outcome,
                contractCount,
                venueErrorCode),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Refuses a missing key or a conflicting replay after the master switch and before any stage fetch.
    /// A stored hit is returned by <see cref="RunWithTelemetry"/> and never reaches here.
    /// </summary>
    private async Task<TradingRiskGateResult?> BlockedByIdempotencyAsync(
        int accountId,
        AuditEnvelope audit,
        TradingRiskAttemptContext context,
        TradingIdempotencyLease lease,
        CancellationToken cancellationToken)
    {
        if (audit.IdempotencyKey.Length > InMemoryTradingIdempotencyStore.MaxKeyLength)
        {
            return RefuseWithoutSideEffects(TradingExecutionRefusal.IdempotencyKeyRequired);
        }

        if (context.RequireIdempotencyKey && string.IsNullOrWhiteSpace(audit.IdempotencyKey))
        {
            return await RefuseAsync(
                accountId,
                ParsedStage.Unresolved,
                audit,
                contractCount: 0,
                TradingExecutionRefusal.IdempotencyKeyRequired,
                cancellationToken).ConfigureAwait(false);
        }

        if (lease.Conflicts)
        {
            return await RefuseAsync(
                accountId,
                ParsedStage.Unresolved,
                audit,
                contractCount: 0,
                TradingExecutionRefusal.IdempotencyConflict,
                cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private static AuditEnvelope Envelope(string tool, string requestHash, TradingRiskAttemptContext context) =>
        new(tool, requestHash, context.DryRun, context.IdempotencyKey);

    private static string FormatStage(AccountStage stage) => stage.ToString();

    /// <summary>
    /// Maps venue write outcomes to audit and attempt results. Unrecognized values fail closed as
    /// <see cref="TradingAuditOutcomes.Unknown"/> — never live (gh#711, gh#734).
    /// </summary>
    private static (string AuditOutcome, TradingAttemptOutcome AttemptOutcome) MapExecutionOutcome(
        ExecutionWriteOutcome venueOutcome) =>
        venueOutcome switch
        {
            ExecutionWriteOutcome.BrokerAcknowledged => (TradingAuditOutcomes.Live, TradingAttemptOutcome.Live),
            ExecutionWriteOutcome.TransmittedOutcomeUnknown => (
                TradingAuditOutcomes.Unknown,
                TradingAttemptOutcome.Unknown),
            ExecutionWriteOutcome.VenueRejected => (TradingAuditOutcomes.Failed, TradingAttemptOutcome.Failed),
            ExecutionWriteOutcome.BrokerSuccessWithoutOrderId => (
                TradingAuditOutcomes.Unknown,
                TradingAttemptOutcome.Unknown),
            ExecutionWriteOutcome.NotTransmitted => (
                TradingAuditOutcomes.Refused,
                TradingAttemptOutcome.Refused),
            _ => (TradingAuditOutcomes.Unknown, TradingAttemptOutcome.Unknown),
        };

    private readonly record struct AuditEnvelope(
        string Tool,
        string RequestHash,
        bool DryRun,
        string IdempotencyKey);

    private static class ParsedStage
    {
        public const string Unresolved = "Unresolved";
    }
}
