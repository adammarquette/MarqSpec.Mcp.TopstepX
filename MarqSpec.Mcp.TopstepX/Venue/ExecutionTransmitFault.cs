using System.Reflection;
using MarqSpec.Client.ProjectX.Exceptions;
using Microsoft.Extensions.Logging;

namespace MarqSpec.Mcp.TopstepX.Venue;

/// <summary>Maps venue transmit failures to ADR-0024 outcomes (gh#710).</summary>
internal static class ExecutionTransmitFault
{
    /// <summary>Maps an exception after an execution transmit attempt.</summary>
    /// <param name="exception">What escaped the guarded call.</param>
    /// <param name="transmitted">True once the guarded delegate started (the request may have left).</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <param name="logger">Where definite failures are logged without vendor text.</param>
    /// <returns>A terminal write result, or rethrows caller cancellation before transmit.</returns>
    public static ExecutionWriteResult MapOrRethrow(
        Exception exception,
        bool transmitted,
        CancellationToken cancellationToken,
        ILogger logger)
    {
        exception = Unwrap(exception);

        if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            if (transmitted)
            {
                return OutcomeUnknown(
                    "The venue did not return a response; the write may have been placed.");
            }

            throw exception;
        }

        if (exception is VenueException venue)
        {
            return MapVenueException(venue, transmitted, logger);
        }

        if (exception is AuthenticationException auth)
        {
            LogDefiniteFailure(logger, auth, httpStatus: null);
            return NotTransmittedVenueFailure();
        }

        if (!transmitted)
        {
            LogDefiniteFailure(logger, exception, httpStatus: null);
            return NotTransmittedVenueFailure();
        }

        LogUncertainFailure(logger, exception, httpStatus: null);
        return OutcomeUnknown(
            "The venue did not return a response; the write may have been placed.");
    }

    private static Exception Unwrap(Exception exception) =>
        exception switch
        {
            TargetInvocationException { InnerException: { } inner } => Unwrap(inner),
            AggregateException { InnerExceptions: [var only] } => Unwrap(only),
            _ => exception,
        };

    private static ExecutionWriteResult MapVenueException(
        VenueException venue,
        bool transmitted,
        ILogger logger)
    {
        if (venue.ErrorCode == 401)
        {
            LogDefiniteFailure(logger, venue, httpStatus: 401);
            return NotTransmittedVenueFailure();
        }

        if (venue.ErrorCode is int code && code is >= 400 and < 500)
        {
            LogDefiniteFailure(logger, venue, httpStatus: code);
            return new ExecutionWriteResult(
                ExecutionWriteOutcome.VenueRejected,
                GateRefusal: null,
                VenueErrorCode: code,
                VenueErrorMessage: null,
                OrderId: null);
        }

        if (!transmitted && IsCredentialRefusal(venue))
        {
            LogDefiniteFailure(logger, venue, httpStatus: venue.ErrorCode);
            return NotTransmittedVenueFailure();
        }

        if (!transmitted)
        {
            LogDefiniteFailure(logger, venue, httpStatus: venue.ErrorCode);
            return NotTransmittedVenueFailure();
        }

        LogUncertainFailure(logger, venue, httpStatus: venue.ErrorCode);
        return OutcomeUnknown(
            "The venue did not return a response; the write may have been placed.");
    }

    private static bool IsCredentialRefusal(VenueException venue) =>
        venue.Message.Contains("credentials", StringComparison.OrdinalIgnoreCase);

    private static ExecutionWriteResult NotTransmittedVenueFailure() =>
        new(
            ExecutionWriteOutcome.NotTransmitted,
            GateRefusal: null,
            VenueErrorCode: null,
            VenueErrorMessage: null,
            OrderId: null);

    private static ExecutionWriteResult OutcomeUnknown(string message) =>
        new(
            ExecutionWriteOutcome.TransmittedOutcomeUnknown,
            GateRefusal: null,
            VenueErrorCode: null,
            VenueErrorMessage: message,
            OrderId: null);

    private static void LogDefiniteFailure(ILogger logger, Exception exception, int? httpStatus) =>
        logger.LogWarning(
            exception,
            "Execution transmit failed before a definitive venue response (retry-safe). ExceptionType={ExceptionType} HttpStatus={HttpStatus}",
            exception.GetType().Name,
            httpStatus);

    private static void LogUncertainFailure(ILogger logger, Exception exception, int? httpStatus) =>
        logger.LogWarning(
            exception,
            "Execution transmit outcome unknown (do not auto-retry). ExceptionType={ExceptionType} HttpStatus={HttpStatus}",
            exception.GetType().Name,
            httpStatus);
}
