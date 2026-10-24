using MarqSpec.Mcp.TopstepX.Telemetry;
using MarqSpec.Mcp.TopstepX.Venue;

namespace MarqSpec.Mcp.TopstepX.Configuration;

/// <summary>
/// Central trading gate: master switch, practice-only stage, caps, dry-run preview, and audit (ADR-0024, gh#711).
/// </summary>
/// <remarks>
/// MCP tools call this type only — never the execution gateway directly. Every member that touches
/// stage resolution, allowlist, denylist, or the gateway re-checks
/// <see cref="TradingExecutionGate.RefuseIfDisabledOrHalted"/> first, and that check re-reads the bound
/// options so a master switch flipped during the call is visible before the claim and again before the
/// send. A disable that lands before the claim writes no audit row. Daily caps charge on
/// <see cref="TradingAuditOutcomes.Attempted"/> rows; outcome rows use zero contract count. Modify charges
/// the full new size, not a delta from the resting order. A gateway refusal after the write-ahead appends
/// a second Attempted row with the negated count so the session-day sum nets to zero. A pre-send
/// <see cref="TradingAuditOutcomes.Unknown"/> row, unique per account, tool and key, is written before the
/// send so a throw or a lost acknowledgment cannot be retried into a second send. A refusal recorded after
/// that claim is what a replay returns. <see cref="TradingAttemptOutcome.Unknown"/> is returned only when
/// the send may already have happened. Once the send has started, a later throw — including a daily-cap
/// unlock raised from cleanup, or a telemetry listener — is <see cref="TradingAttemptOutcome.Unknown"/>,
/// never a retry. Only
/// <see cref="PreSendFaultException"/> leaves this type for a fault from before the send.
/// One instance serves one order operation. The type is registered scoped so each request gets a
/// fresh gate. The send-started and lease-release flags live on a per-call <see cref="SendAttempt"/>
/// passed into <see cref="SendAndFinalizeAsync"/>, not on the instance. A second operation throws
/// <see cref="InvalidOperationException"/> before any vendor call, so a shared instance cannot clear
/// those flags and report a live send as safe to retry.
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

    /// <summary>
    /// One order operation per instance. Set at entry, before the per-call <see cref="SendAttempt"/>
    /// exists. The gate is registered scoped and each request builds a new instance. A second operation
    /// on this instance throws rather than reuse the send-started state.
    /// </summary>
    private bool _operationStarted;

    /// <summary>
    /// Cleanup fault planted by a test and copied onto the per-call attempt. Production passes null through
    /// the public constructor. Cancel and close take no daily-cap lease, so a test passes the fault to the
    /// internal constructor and the gate copies it onto the per-call attempt. The hook is not a settable
    /// property on this type.
    /// </summary>
    private readonly Exception? _cleanupFault;

    /// <summary>
    /// Send-started and lease-release state for one order operation. Passed through
    /// <see cref="SendAndFinalizeAsync"/> so it cannot survive onto the next call.
    /// </summary>
    private sealed class SendAttempt
    {
        public bool SendStarted { get; set; }

        public bool LeaseReleaseFailed { get; set; }

        /// <summary>
        /// Raised after the send, inside the telemetry try, when cleanup failed. The daily-cap release
        /// sets it from the dispose exception. A null value with <see cref="ITradingDailyCapCoordinator.LastReleaseFailed"/>
        /// becomes an <see cref="InvalidOperationException"/> at that same site.
        /// </summary>
        public Exception? CleanupFault { get; set; }
    }

    /// <summary>Creates the risk gate. Production passes no cleanup fault.</summary>
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
        HostTelemetry telemetry)
        : this(
            executionGate,
            gateway,
            marketData,
            stageResolver,
            unknownStageSignal,
            dailyCap,
            auditLog,
            contractDenylist,
            accountCache,
            telemetry,
            cleanupFault: null)
    {
    }

    /// <summary>
    /// Creates the risk gate with a cleanup fault visible only to assemblies named by
    /// <c>InternalsVisibleTo</c>. Production uses the public constructor. The fault is copied onto the
    /// per-call attempt so cancel and close, which take no daily-cap lease, can still raise a post-send
    /// cleanup fault into the handler that must not report a retry.
    /// </summary>
    /// <param name="executionGate">The disabled, halted, stage and cap checks.</param>
    /// <param name="gateway">The venue write seam.</param>
    /// <param name="marketData">Account, order and position reads.</param>
    /// <param name="stageResolver">Practice-stage parsing.</param>
    /// <param name="unknownStageSignal">Signal raised when a write is refused because the stage cannot be read.</param>
    /// <param name="dailyCap">The session-day lock and contract rollup.</param>
    /// <param name="auditLog">The append-only audit log.</param>
    /// <param name="contractDenylist">The contract denylist.</param>
    /// <param name="accountCache">The per-attempt account cache.</param>
    /// <param name="telemetry">Execution-write spans and counters.</param>
    /// <param name="cleanupFault">
    /// A fault planted by a test, or null. Copied onto the per-call attempt before the operation runs.
    /// </param>
    internal TradingRiskGate(
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
        Exception? cleanupFault)
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
        _cleanupFault = cleanupFault;
    }

    /// <summary>Places an order when every gate layer permits.</summary>
    public Task<TradingRiskGateResult> SubmitPlace(
        ExecutionPlaceOrderCommand command,
        TradingRiskAttemptContext context,
        CancellationToken cancellationToken) =>
        RunWithTelemetry(
            ExecutionOperation.PlaceOrderOperation,
            attempt => RunPlaceAsync(command, context, attempt, cancellationToken));

    /// <summary>Modifies an order when every gate layer permits.</summary>
    public Task<TradingRiskGateResult> SubmitModify(
        ExecutionModifyOrderCommand command,
        TradingRiskAttemptContext context,
        CancellationToken cancellationToken) =>
        RunWithTelemetry(
            ExecutionOperation.ModifyOrderOperation,
            attempt => RunModifyAsync(command, context, attempt, cancellationToken));

    /// <summary>Cancels an order when every gate layer permits.</summary>
    public Task<TradingRiskGateResult> SubmitCancel(
        ExecutionCancelOrderCommand command,
        TradingRiskAttemptContext context,
        CancellationToken cancellationToken) =>
        RunWithTelemetry(
            ExecutionOperation.CancelOrderOperation,
            attempt => RunCancelAsync(command, context, attempt, cancellationToken));

    /// <summary>Flattens a position when every gate layer permits.</summary>
    public Task<TradingRiskGateResult> SubmitClose(
        ExecutionClosePositionCommand command,
        TradingRiskAttemptContext context,
        CancellationToken cancellationToken) =>
        RunWithTelemetry(
            ExecutionOperation.ClosePositionOperation,
            attempt => RunCloseAsync(command, context, attempt, cancellationToken));

    /// <summary>Partially closes a position when every gate layer permits.</summary>
    public Task<TradingRiskGateResult> SubmitPartialClose(
        ExecutionPartialClosePositionCommand command,
        TradingRiskAttemptContext context,
        CancellationToken cancellationToken) =>
        RunWithTelemetry(
            ExecutionOperation.PartialClosePositionOperation,
            attempt => RunPartialCloseAsync(command, context, attempt, cancellationToken));

    private async Task<TradingRiskGateResult> RunWithTelemetry(
        string operation,
        Func<SendAttempt, Task<TradingRiskGateResult>> body)
    {
        if (_operationStarted)
        {
            throw new InvalidOperationException(
                "TradingRiskGate serves one order operation. A second call on the same instance is refused.");
        }

        _operationStarted = true;
        SendAttempt attempt = new() { CleanupFault = _cleanupFault };
        _accountCache.Clear();
        HostTelemetry.ExecutionWriteScope write = _telemetry.ExecutionWrite(operation);
        TradingRiskGateResult result;
        try
        {
            try
            {
                result = await body(attempt).ConfigureAwait(false);
                if (attempt.SendStarted && _dailyCap.LastReleaseFailed && attempt.CleanupFault is null)
                {
                    attempt.CleanupFault = new InvalidOperationException("Trading bookkeeping failed.");
                }

                if (attempt.SendStarted && attempt.CleanupFault is not null)
                {
                    // Cleanup runs after the send. Raising it here is what the post-send handler classifies.
                    // Returning a result instead would never enter that handler.
                    throw attempt.CleanupFault;
                }

                if (result.IsReplay)
                {
                    write.MarkReplay();
                }

                write.Outcome(ExecutionWriteRefusalForResult.For(result));
            }
            catch (VenueException exception) when (!attempt.SendStarted)
            {
                // Named so the venue-failure walk sees a translation on this route. Before the send it is
                // the same retryable fault as any other pre-send exception.
                write.Outcome(ExecutionWriteRefusal.Error);
                throw new PreSendFaultException(exception);
            }
            catch (VenueException)
            {
                // The send has started. Do not wrap this as PreSendFaultException: that type is SafeToRetry.
                write.Outcome(ExecutionWriteRefusal.OutcomeUnknown);
                result = UnknownTransmit(AccountStage.Unknown);
            }
            catch (Exception exception) when (!attempt.SendStarted)
            {
                write.Outcome(ExecutionWriteRefusal.Error);
                throw new PreSendFaultException(exception);
            }
            catch (Exception)
            {
                // The send has started. Do not rethrow, and do not wrap as PreSendFaultException.
                // SafeToRetry would place a second live order.
                write.Outcome(ExecutionWriteRefusal.OutcomeUnknown);
                result = UnknownTransmit(AccountStage.Unknown);
            }
        }
        finally
        {
            write.Dispose();
            _accountCache.Clear();
        }

        if (attempt.SendStarted && write.BookkeepingFailed)
        {
            return UnknownTransmit(result.ParsedStage);
        }

        return result;
    }

    private async Task<TradingRiskGateResult> RunPlaceAsync(
        ExecutionPlaceOrderCommand command,
        TradingRiskAttemptContext context,
        SendAttempt attempt,
        CancellationToken cancellationToken)
    {
        AuditEnvelope audit = Envelope(
            TradingMcpTools.PlaceOrderOperation,
            TradingRequestHasher.HashPlace(command, context.DryRun),
            context);

        if (_executionGate.RefuseIfDisabledOrHalted() is TradingExecutionRefusal disabled
            && disabled.Code == TradingExecutionRefusal.TradingDisabled.Code)
        {
            return RefuseWithoutSideEffects(disabled);
        }

        if (RefuseInvalidPlace(command) is { } invalid)
        {
            return RefuseWithoutSideEffects(invalid);
        }

        if (await RefuseWhileHaltedAsync(command.AccountId, audit, command.Size, cancellationToken)
                .ConfigureAwait(false) is { } halted)
        {
            return halted;
        }

        if (await BlockedByIdempotencyAsync(command.AccountId, audit, context, cancellationToken)
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

        return await WithDailyCapAsync(
            command.AccountId,
            attempt,
            async () =>
            {
                if (await RefuseDailyCapAsync(command.AccountId, command.Size, cancellationToken)
                        .ConfigureAwait(false) is TradingExecutionRefusal daily)
                {
                    return await AuditedUnlessTradingDisabledAsync(
                        command.AccountId,
                        stage,
                        audit,
                        command.Size,
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
                    attempt,
                    ct => _gateway.PlaceOrder(command, ct),
                    cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<TradingRiskGateResult> RunModifyAsync(
        ExecutionModifyOrderCommand command,
        TradingRiskAttemptContext context,
        SendAttempt attempt,
        CancellationToken cancellationToken)
    {
        AuditEnvelope audit = Envelope(
            TradingMcpTools.ModifyOrderOperation,
            TradingRequestHasher.HashModify(command, context.DryRun),
            context);

        if (_executionGate.RefuseIfDisabledOrHalted() is TradingExecutionRefusal disabled
            && disabled.Code == TradingExecutionRefusal.TradingDisabled.Code)
        {
            return RefuseWithoutSideEffects(disabled);
        }

        if (RefuseInvalidModify(command) is { } invalid)
        {
            return RefuseWithoutSideEffects(invalid);
        }

        int? modifySize = command.Size;

        int haltCount = 0;
        if (modifySize is int haltedSize)
        {
            haltCount = haltedSize;
        }

        if (await RefuseWhileHaltedAsync(command.AccountId, audit, haltCount, cancellationToken)
                .ConfigureAwait(false) is { } halted)
        {
            return halted;
        }

        if (await BlockedByIdempotencyAsync(command.AccountId, audit, context, cancellationToken)
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
                contractCount: haltCount,
                stageBlock,
                cancellationToken).ConfigureAwait(false);
        }

        if (RefuseAllowlist(command.AccountId) is TradingExecutionRefusal allowlist)
        {
            return await RefuseAsync(
                command.AccountId,
                FormatStage(stage),
                audit,
                contractCount: haltCount,
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
                contractCount: haltCount,
                contractRefusal,
                cancellationToken).ConfigureAwait(false);
        }

        if (modifySize is int perOrderSize
            && _executionGate.RefuseIfOrderSizeExceedsLimit(perOrderSize) is TradingExecutionRefusal size)
        {
            return await RefuseAsync(
                command.AccountId,
                FormatStage(stage),
                audit,
                contractCount: perOrderSize,
                size,
                cancellationToken).ConfigureAwait(false);
        }

        return await WithDailyCapAsync(
            command.AccountId,
            attempt,
            async () =>
            {
                if (modifySize is int dailySize
                    && await RefuseDailyCapAsync(command.AccountId, dailySize, cancellationToken)
                        .ConfigureAwait(false) is TradingExecutionRefusal daily)
                {
                    return await AuditedUnlessTradingDisabledAsync(
                        command.AccountId,
                        stage,
                        audit,
                        dailySize,
                        daily,
                        cancellationToken).ConfigureAwait(false);
                }

                int capCharge = 0;
                if (modifySize is int chargedSize)
                {
                    capCharge = chargedSize;
                }

                if (context.DryRun)
                {
                    return await PreviewAsync(
                        command.AccountId,
                        stage,
                        audit,
                        contractCount: capCharge,
                        cancellationToken).ConfigureAwait(false);
                }

                return await TransmitWithCapChargeAsync(
                    command.AccountId,
                    stage,
                    audit,
                    capChargeSize: capCharge,
                    attempt,
                    ct => _gateway.ModifyOrder(command, ct),
                    cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<TradingRiskGateResult> RunCancelAsync(
        ExecutionCancelOrderCommand command,
        TradingRiskAttemptContext context,
        SendAttempt attempt,
        CancellationToken cancellationToken)
    {
        AuditEnvelope audit = Envelope(
            TradingMcpTools.CancelOrderOperation,
            TradingRequestHasher.HashCancel(command, context.DryRun),
            context);

        if (_executionGate.RefuseIfDisabledOrHalted() is TradingExecutionRefusal disabled
            && disabled.Code == TradingExecutionRefusal.TradingDisabled.Code)
        {
            return RefuseWithoutSideEffects(disabled);
        }

        if (await RefuseWhileHaltedAsync(command.AccountId, audit, contractCount: 0, cancellationToken)
                .ConfigureAwait(false) is { } halted)
        {
            return halted;
        }

        if (await BlockedByIdempotencyAsync(command.AccountId, audit, context, cancellationToken)
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
            attempt,
            ct => _gateway.CancelOrder(command, ct),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<TradingRiskGateResult> RunCloseAsync(
        ExecutionClosePositionCommand command,
        TradingRiskAttemptContext context,
        SendAttempt attempt,
        CancellationToken cancellationToken)
    {
        AuditEnvelope audit = Envelope(
            TradingMcpTools.ClosePositionOperation,
            TradingRequestHasher.HashClose(command, context.DryRun),
            context);

        if (_executionGate.RefuseIfDisabledOrHalted() is TradingExecutionRefusal disabled
            && disabled.Code == TradingExecutionRefusal.TradingDisabled.Code)
        {
            return RefuseWithoutSideEffects(disabled);
        }

        if (await RefuseWhileHaltedAsync(command.AccountId, audit, contractCount: 0, cancellationToken)
                .ConfigureAwait(false) is { } halted)
        {
            return halted;
        }

        if (await BlockedByIdempotencyAsync(command.AccountId, audit, context, cancellationToken)
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
            attempt,
            ct => _gateway.ClosePosition(command, ct),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<TradingRiskGateResult> RunPartialCloseAsync(
        ExecutionPartialClosePositionCommand command,
        TradingRiskAttemptContext context,
        SendAttempt attempt,
        CancellationToken cancellationToken)
    {
        AuditEnvelope audit = Envelope(
            TradingMcpTools.PartialClosePositionOperation,
            TradingRequestHasher.HashPartialClose(command, context.DryRun),
            context);

        if (_executionGate.RefuseIfDisabledOrHalted() is TradingExecutionRefusal disabled
            && disabled.Code == TradingExecutionRefusal.TradingDisabled.Code)
        {
            return RefuseWithoutSideEffects(disabled);
        }

        if (command.Size <= 0)
        {
            return RefuseWithoutSideEffects(TradingExecutionRefusal.SizeNotPositive(command.Size));
        }

        if (await RefuseWhileHaltedAsync(command.AccountId, audit, command.Size, cancellationToken)
                .ConfigureAwait(false) is { } halted)
        {
            return halted;
        }

        if (await BlockedByIdempotencyAsync(command.AccountId, audit, context, cancellationToken)
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

        if (await RefusePartialCloseSizeAsync(command, cancellationToken).ConfigureAwait(false)
            is TradingExecutionRefusal position)
        {
            if (position.Code == TradingExecutionRefusal.TradingDisabled.Code)
            {
                return RefuseWithoutSideEffects(position);
            }

            if (position.Code == TradingExecutionRefusal.KillSwitchEngaged.Code)
            {
                return await RefuseAsync(
                    command.AccountId,
                    FormatStage(stage),
                    audit,
                    command.Size,
                    position,
                    cancellationToken).ConfigureAwait(false);
            }

            return RefuseWithoutSideEffects(position);
        }

        return await WithDailyCapAsync(
            command.AccountId,
            attempt,
            async () =>
            {
                if (await RefuseDailyCapAsync(command.AccountId, command.Size, cancellationToken)
                        .ConfigureAwait(false) is TradingExecutionRefusal daily)
                {
                    return await AuditedUnlessTradingDisabledAsync(
                        command.AccountId,
                        stage,
                        audit,
                        command.Size,
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
                    attempt,
                    ct => _gateway.PartialClosePosition(command, ct),
                    cancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
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
        SendAttempt attempt,
        Func<CancellationToken, Task<ExecutionWriteResult>> send,
        CancellationToken cancellationToken)
    {
        if (await RefuseBeforeClaimAsync(accountId, stage, audit, capChargeSize, cancellationToken)
                .ConfigureAwait(false) is { } switchedOff)
        {
            return switchedOff;
        }

        if (await RefuseGatewayWriteUnlessPermittedAsync(accountId, cancellationToken).ConfigureAwait(false)
            is TradingExecutionRefusal gatewayRefusal)
        {
            if (gatewayRefusal.Code == TradingExecutionRefusal.TradingDisabled.Code)
            {
                return RefuseWithoutSideEffects(gatewayRefusal);
            }

            return await RefuseAsync(
                accountId,
                FormatStage(stage),
                audit,
                capChargeSize,
                gatewayRefusal,
                cancellationToken).ConfigureAwait(false);
        }

        if (await ClaimOrReplayAsync(accountId, stage, audit, cancellationToken).ConfigureAwait(false)
            is { } claimed)
        {
            return claimed;
        }

        if (await RefuseAfterClaimAsync(accountId, stage, audit, cancellationToken).ConfigureAwait(false)
            is { } afterClaim)
        {
            return afterClaim;
        }

        try
        {
            await WriteAuditAsync(
                accountId,
                FormatStage(stage),
                audit,
                TradingAuditOutcomes.Attempted,
                capChargeSize,
                venueErrorCode: null,
                orderId: null,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The send has not started. Record the refusal so the claim is not replayed as OutcomeUnknown.
            return await RefuseUnsentClaimAsync(accountId, stage, audit, cancellationToken)
                .ConfigureAwait(false);
        }

        return await SendAndFinalizeAsync(
            accountId,
            stage,
            audit,
            capChargeSize,
            attempt,
            send,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<TradingRiskGateResult> TransmitWithoutCapChargeAsync(
        int accountId,
        AccountStage stage,
        AuditEnvelope audit,
        SendAttempt attempt,
        Func<CancellationToken, Task<ExecutionWriteResult>> send,
        CancellationToken cancellationToken)
    {
        if (await RefuseBeforeClaimAsync(accountId, stage, audit, contractCount: 0, cancellationToken)
                .ConfigureAwait(false) is { } switchedOff)
        {
            return switchedOff;
        }

        if (await RefuseGatewayWriteUnlessPermittedAsync(accountId, cancellationToken).ConfigureAwait(false)
            is TradingExecutionRefusal gatewayRefusal)
        {
            if (gatewayRefusal.Code == TradingExecutionRefusal.TradingDisabled.Code)
            {
                return RefuseWithoutSideEffects(gatewayRefusal);
            }

            return await RefuseAsync(
                accountId,
                FormatStage(stage),
                audit,
                contractCount: 0,
                gatewayRefusal,
                cancellationToken).ConfigureAwait(false);
        }

        if (await ClaimOrReplayAsync(accountId, stage, audit, cancellationToken).ConfigureAwait(false)
            is { } claimed)
        {
            return claimed;
        }

        if (await RefuseAfterClaimAsync(accountId, stage, audit, cancellationToken).ConfigureAwait(false)
            is { } afterClaim)
        {
            return afterClaim;
        }

        try
        {
            await WriteAuditAsync(
                accountId,
                FormatStage(stage),
                audit,
                TradingAuditOutcomes.Attempted,
                contractCount: 0,
                venueErrorCode: null,
                orderId: null,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return await RefuseUnsentClaimAsync(accountId, stage, audit, cancellationToken)
                .ConfigureAwait(false);
        }

        return await SendAndFinalizeAsync(
            accountId,
            stage,
            audit,
            capChargeApplied: 0,
            attempt,
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
        SendAttempt attempt,
        Func<CancellationToken, Task<ExecutionWriteResult>> send,
        CancellationToken cancellationToken)
    {
        if (_executionGate.RefuseIfDisabledOrHalted() is TradingExecutionRefusal blocked)
        {
            try
            {
                if (capChargeApplied != 0)
                {
                    await WriteAuditAsync(
                        accountId,
                        FormatStage(stage),
                        audit,
                        TradingAuditOutcomes.Attempted,
                        contractCount: -capChargeApplied,
                        venueErrorCode: blocked.Code,
                        orderId: null,
                        CancellationToken.None).ConfigureAwait(false);
                }

                return await RecordRefusalAsync(
                    accountId,
                    FormatStage(stage),
                    audit,
                    contractCount: 0,
                    blocked,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return RefuseWithoutSideEffects(blocked);
            }
        }

        ExecutionWriteResult write;
        try
        {
            attempt.SendStarted = true;
            write = await send(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The send may have reached the venue. The pre-send Unknown claim already blocks a retry.
            // A second Unknown row would violate the unique claim, and rethrowing would skip the
            // structured OutcomeUnknown the caller must see.
            return UnknownTransmit(stage);
        }

        try
        {
            return await MapWriteAsync(accountId, stage, audit, capChargeApplied, write).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The broker may already have acknowledged. The claim stays Unknown until a Live row exists,
            // which is what a replay returns, and it does not send again.
            return UnknownTransmit(stage);
        }
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
            return await AuditedUnlessTradingDisabledAsync(
                accountId,
                stage,
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
            orderId: null,
            cancellationToken).ConfigureAwait(false);

        return new TradingRiskGateResult(
            TradingAttemptOutcome.Preview,
            stage,
            Refusal: null,
            WriteResult: null,
            Preview: new TradingExecutionPreview(accountId, stage, audit.Tool, WouldTransmit: true));
    }

    private Task<TradingRiskGateResult> AuditedUnlessTradingDisabledAsync(
        int accountId,
        AccountStage stage,
        AuditEnvelope audit,
        int contractCount,
        TradingExecutionRefusal refusal,
        CancellationToken cancellationToken)
    {
        if (refusal.Code == TradingExecutionRefusal.TradingDisabled.Code)
        {
            return Task.FromResult(RefuseWithoutSideEffects(refusal));
        }

        return RefuseAsync(
            accountId,
            FormatStage(stage),
            audit,
            contractCount,
            refusal,
            cancellationToken);
    }

    /// <summary>
    /// A master-switch or kill-switch refusal before the claim. Trading disabled writes nothing. The kill
    /// switch still writes a refusal row.
    /// </summary>
    private async Task<TradingRiskGateResult?> RefuseBeforeClaimAsync(
        int accountId,
        AccountStage stage,
        AuditEnvelope audit,
        int contractCount,
        CancellationToken cancellationToken)
    {
        if (_executionGate.RefuseIfDisabledOrHalted() is not TradingExecutionRefusal blocked)
        {
            return null;
        }

        if (blocked.Code == TradingExecutionRefusal.TradingDisabled.Code)
        {
            return RefuseWithoutSideEffects(blocked);
        }

        return await RefuseAsync(
            accountId,
            FormatStage(stage),
            audit,
            contractCount,
            blocked,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A switch flipped after the claim and before the send. The claim already exists, so the refusal is
    /// recorded and a replay returns it instead of OutcomeUnknown. Nothing is sent.
    /// </summary>
    private async Task<TradingRiskGateResult?> RefuseAfterClaimAsync(
        int accountId,
        AccountStage stage,
        AuditEnvelope audit,
        CancellationToken cancellationToken)
    {
        if (_executionGate.RefuseIfDisabledOrHalted() is not TradingExecutionRefusal blocked)
        {
            return null;
        }

        _ = cancellationToken;
        return await RecordRefusalAsync(
            accountId,
            FormatStage(stage),
            audit,
            contractCount: 0,
            blocked,
            CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// The Attempted row could not be stored, and the send has not started. Record a refusal when the store
    /// can still take one. Either way the caller gets a retryable result and no exception.
    /// </summary>
    private async Task<TradingRiskGateResult> RefuseUnsentClaimAsync(
        int accountId,
        AccountStage stage,
        AuditEnvelope audit,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        try
        {
            return await RecordRefusalAsync(
                accountId,
                FormatStage(stage),
                audit,
                contractCount: 0,
                TradingExecutionRefusal.BeforeSendFault,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return RefuseWithoutSideEffects(TradingExecutionRefusal.BeforeSendFault);
        }
    }

    private static TradingRiskGateResult RefuseWithoutSideEffects(TradingExecutionRefusal refusal) =>
        new(
            TradingAttemptOutcome.Refused,
            AccountStage.Unknown,
            refusal,
            WriteResult: null,
            Preview: null);

    private Task<TradingRiskGateResult> RefuseAsync(
        int accountId,
        string parsedStage,
        AuditEnvelope audit,
        int contractCount,
        TradingExecutionRefusal refusal,
        CancellationToken cancellationToken)
    {
        if (refusal.Code == TradingExecutionRefusal.TradingDisabled.Code)
        {
            return Task.FromResult(RefuseWithoutSideEffects(refusal));
        }

        return RecordRefusalAsync(
            accountId,
            parsedStage,
            audit,
            contractCount,
            refusal,
            cancellationToken);
    }

    private async Task<TradingRiskGateResult> RecordRefusalAsync(
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
            orderId: null,
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
                    orderId: null,
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

        // A keyed pre-send row is already Unknown. A second one would violate the unique claim. An unkeyed
        // attempt has no claim, so the outcome row is still written. Live and Failed are what a replay prefers.
        if (outcome != TradingAuditOutcomes.Unknown || string.IsNullOrEmpty(audit.IdempotencyKey))
        {
            await WriteAuditAsync(
                accountId,
                FormatStage(stage),
                audit,
                outcome,
                contractCount: 0,
                venueCode,
                write.OrderId,
                CancellationToken.None).ConfigureAwait(false);
        }

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
        long? orderId,
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
                venueErrorCode,
                orderId),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Refuses a missing or over-long key after the master switch and before any stage fetch.
    /// A stored transmit is replayed from the audit claim inside the transmit path, not from process memory.
    /// </summary>
    private async Task<TradingRiskGateResult?> BlockedByIdempotencyAsync(
        int accountId,
        AuditEnvelope audit,
        TradingRiskAttemptContext context,
        CancellationToken cancellationToken)
    {
        if (audit.IdempotencyKey.Length > TradingAuditClaims.MaxKeyLength)
        {
            return RefuseWithoutSideEffects(TradingExecutionRefusal.IdempotencyKeyTooLong);
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

        return null;
    }

    /// <summary>
    /// Inserts the pre-send Unknown claim, or returns the replay or conflict that claim already records.
    /// A blank key is not claimed: direct gate callers that omit one still transmit, and the unique index
    /// excludes empty keys.
    /// </summary>
    private async Task<TradingRiskGateResult?> ClaimOrReplayAsync(
        int accountId,
        AccountStage stage,
        AuditEnvelope audit,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(audit.IdempotencyKey))
        {
            return null;
        }

        TradingAuditClaimResult claim = await _auditLog.TryClaimUnknownAsync(
            new TradingAuditWriteRequest(
                accountId,
                FormatStage(stage),
                audit.Tool,
                audit.IdempotencyKey,
                audit.RequestHash,
                audit.DryRun,
                TradingAuditOutcomes.Unknown,
                ContractCount: 0,
                VenueErrorCode: null),
            cancellationToken).ConfigureAwait(false);

        if (claim.Status == TradingAuditClaimStatus.Claimed)
        {
            return null;
        }

        if (claim.Status == TradingAuditClaimStatus.Conflict)
        {
            return await RefuseAsync(
                accountId,
                FormatStage(stage),
                audit,
                contractCount: 0,
                TradingExecutionRefusal.IdempotencyConflict,
                cancellationToken).ConfigureAwait(false);
        }

        return ReplayFrom(stage, claim);
    }

    private static TradingRiskGateResult ReplayFrom(AccountStage stage, TradingAuditClaimResult claim)
    {
        AccountStage parsed = Enum.TryParse(claim.ParsedStage, out AccountStage stored)
            ? stored
            : stage;

        if (claim.BestOutcome == TradingAuditOutcomes.Refused)
        {
            string code = string.IsNullOrEmpty(claim.VenueErrorCode)
                ? ExecutionWriteRefusal.Error
                : claim.VenueErrorCode;
            return new TradingRiskGateResult(
                TradingAttemptOutcome.Refused,
                parsed,
                new TradingExecutionRefusal(
                    code,
                    "Stored refusal for this account, tool and idempotency key. The venue was not left with a new order."),
                WriteResult: null,
                Preview: null,
                IsReplay: true);
        }

        (ExecutionWriteOutcome venueOutcome, TradingAttemptOutcome attempt) = claim.BestOutcome switch
        {
            TradingAuditOutcomes.Live => (ExecutionWriteOutcome.BrokerAcknowledged, TradingAttemptOutcome.Live),
            TradingAuditOutcomes.Failed => (ExecutionWriteOutcome.VenueRejected, TradingAttemptOutcome.Failed),
            _ => (ExecutionWriteOutcome.TransmittedOutcomeUnknown, TradingAttemptOutcome.Unknown),
        };

        int? vendorCode = int.TryParse(
            claim.VenueErrorCode,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out int parsedCode)
            ? parsedCode
            : null;

        long? orderId = attempt == TradingAttemptOutcome.Live ? claim.OrderId : null;

        return new TradingRiskGateResult(
            attempt,
            parsed,
            Refusal: null,
            new ExecutionWriteResult(venueOutcome, GateRefusal: null, HttpStatusCode: null, vendorCode, orderId),
            Preview: null,
            IsReplay: true);
    }

    /// <summary>
    /// Holds the daily-cap lock around <paramref name="body"/>. A release failure is counted here and stored
    /// on the attempt. After the send, <see cref="RunWithTelemetry"/> raises that fault into the post-send
    /// handler. A release failure before the send leaves the body's result alone. The release itself does
    /// not throw.
    /// </summary>
    private async Task<TradingRiskGateResult> WithDailyCapAsync(
        int accountId,
        SendAttempt attempt,
        Func<Task<TradingRiskGateResult>> body,
        CancellationToken cancellationToken)
    {
        IAsyncDisposable lease = await _dailyCap
            .AcquireAccountDayLockAsync(accountId, cancellationToken)
            .ConfigureAwait(false);
        TradingRiskGateResult result;
        try
        {
            result = await body().ConfigureAwait(false);
        }
        finally
        {
            await ReleaseDailyCapLeaseAsync(attempt, lease).ConfigureAwait(false);
        }

        return result;
    }

    private async ValueTask ReleaseDailyCapLeaseAsync(SendAttempt attempt, IAsyncDisposable lease)
    {
        try
        {
            await lease.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Counted here. RunWithTelemetry raises CleanupFault after the send so the post-send handler
            // returns OutcomeUnknown. Returning a result from this catch would never enter that handler.
            attempt.LeaseReleaseFailed = true;
            attempt.CleanupFault = exception;
            _telemetry.RecordBookkeepingFault();
        }
    }

    private static TradingRiskGateResult UnknownTransmit(AccountStage stage) =>
        new(
            TradingAttemptOutcome.Unknown,
            stage,
            Refusal: null,
            new ExecutionWriteResult(
                ExecutionWriteOutcome.TransmittedOutcomeUnknown,
                GateRefusal: null,
                HttpStatusCode: null,
                VendorErrorCode: null,
                OrderId: null),
            Preview: null);

    private async Task<TradingRiskGateResult?> RefuseWhileHaltedAsync(
        int accountId,
        AuditEnvelope audit,
        int contractCount,
        CancellationToken cancellationToken)
    {
        if (_executionGate.RefuseIfDisabledOrHalted() is TradingExecutionRefusal halted
            && halted.Code != TradingExecutionRefusal.TradingDisabled.Code)
        {
            return await RefuseAsync(
                accountId,
                ParsedStage.Unresolved,
                audit,
                contractCount,
                halted,
                cancellationToken).ConfigureAwait(false);
        }

        return null;
    }

    private static TradingExecutionRefusal? RefuseInvalidPlace(ExecutionPlaceOrderCommand command)
    {
        if (command.Size <= 0)
        {
            return TradingExecutionRefusal.SizeNotPositive(command.Size);
        }

        return RefuseInvalidPrices(command.OrderType, command.LimitPrice, command.StopPrice, command.TrailPrice);
    }

    private static TradingExecutionRefusal? RefuseInvalidModify(ExecutionModifyOrderCommand command)
    {
        if (command.Size is int size && size <= 0)
        {
            return TradingExecutionRefusal.SizeNotPositive(size);
        }

        if (command.LimitPrice is <= 0 || command.StopPrice is <= 0)
        {
            return TradingExecutionRefusal.PriceNotPositive;
        }

        return null;
    }

    private static TradingExecutionRefusal? RefuseInvalidPrices(
        ExecutionOrderType orderType,
        decimal? limitPrice,
        decimal? stopPrice,
        decimal? trailPrice)
    {
        if (limitPrice is <= 0 || stopPrice is <= 0 || trailPrice is <= 0)
        {
            return TradingExecutionRefusal.PriceNotPositive;
        }

        bool limitRequired = orderType is ExecutionOrderType.Limit or ExecutionOrderType.StopLimit;
        bool stopRequired = orderType is ExecutionOrderType.Stop or ExecutionOrderType.StopLimit;
        bool trailRequired = orderType is ExecutionOrderType.TrailingStop;
        if ((limitRequired && limitPrice is null)
            || (stopRequired && stopPrice is null)
            || (trailRequired && trailPrice is null))
        {
            return TradingExecutionRefusal.PriceRequired;
        }

        return null;
    }

    private async Task<TradingExecutionRefusal?> RefusePartialCloseSizeAsync(
        ExecutionPartialClosePositionCommand command,
        CancellationToken cancellationToken)
    {
        if (_executionGate.RefuseIfDisabledOrHalted() is TradingExecutionRefusal disabled)
        {
            return disabled;
        }

        IReadOnlyList<VenuePosition> positions = await _marketData
            .GetOpenPositionsAsync(command.AccountId, cancellationToken)
            .ConfigureAwait(false);

        int open = 0;
        foreach (VenuePosition position in positions)
        {
            if (string.Equals(position.ContractId, command.ContractId, StringComparison.Ordinal)
                && position.SignedSize != 0)
            {
                open = Math.Abs(position.SignedSize);
                break;
            }
        }

        if (command.Size > open)
        {
            return TradingExecutionRefusal.PartialCloseExceedsPosition(command.Size, open);
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
