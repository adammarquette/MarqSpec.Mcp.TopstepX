using System.Reflection;
using Microsoft.Extensions.Logging;

namespace MarqSpec.Mcp.TopstepX.Venue;

/// <summary>Maps venue transmit failures to ADR-0024 outcomes (gh#710).</summary>
internal static class ExecutionTransmitFault
{
    /// <summary>Maps an exception after an execution transmit attempt.</summary>
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
                return OutcomeUnknown();
            }

            throw exception;
        }

        if (exception is VenueException venue)
        {
            return MapVenueException(venue, transmitted, logger);
        }

        if (!transmitted)
        {
            LogDefiniteFailure(logger, exception.GetType().Name, httpStatus: null);
            return NotTransmittedVenueFailure();
        }

        LogUncertainFailure(logger, exception.GetType().Name, httpStatus: null);
        return OutcomeUnknown();
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
            LogDefiniteFailure(logger, venue.GetType().Name, httpStatus: 401);
            return NotTransmittedVenueFailure();
        }

        if (venue.ErrorCode == 408)
        {
            if (!transmitted)
            {
                LogDefiniteFailure(logger, venue.GetType().Name, httpStatus: 408);
                return NotTransmittedVenueFailure();
            }

            LogUncertainFailure(logger, venue.GetType().Name, httpStatus: 408);
            return OutcomeUnknown();
        }

        if (venue.ErrorCode is int code && code is >= 400 and < 500)
        {
            LogDefiniteFailure(logger, venue.GetType().Name, httpStatus: code);
            return new ExecutionWriteResult(
                ExecutionWriteOutcome.VenueRejected,
                GateRefusal: null,
                HttpStatusCode: code,
                VendorErrorCode: null,
                OrderId: null);
        }

        if (!transmitted)
        {
            LogDefiniteFailure(logger, venue.GetType().Name, httpStatus: venue.ErrorCode);
            return NotTransmittedVenueFailure();
        }

        LogUncertainFailure(logger, venue.GetType().Name, httpStatus: venue.ErrorCode);
        return OutcomeUnknown();
    }

    private static ExecutionWriteResult NotTransmittedVenueFailure() =>
        new(
            ExecutionWriteOutcome.NotTransmitted,
            GateRefusal: null,
            HttpStatusCode: null,
            VendorErrorCode: null,
            OrderId: null);

    private static ExecutionWriteResult OutcomeUnknown() =>
        new(
            ExecutionWriteOutcome.TransmittedOutcomeUnknown,
            GateRefusal: null,
            HttpStatusCode: null,
            VendorErrorCode: null,
            OrderId: null);

    private static void LogDefiniteFailure(ILogger logger, string exceptionType, int? httpStatus) =>
        logger.LogWarning(
            "Execution transmit failed before a definitive venue response (retry-safe). ExceptionType={ExceptionType} HttpStatus={HttpStatus}",
            exceptionType,
            httpStatus);

    private static void LogUncertainFailure(ILogger logger, string exceptionType, int? httpStatus) =>
        logger.LogWarning(
            "Execution transmit outcome unknown (do not auto-retry). ExceptionType={ExceptionType} HttpStatus={HttpStatus}",
            exceptionType,
            httpStatus);
}
