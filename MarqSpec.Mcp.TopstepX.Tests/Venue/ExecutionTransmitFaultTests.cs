using FluentAssertions;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.Extensions.Logging.Abstractions;

namespace MarqSpec.Mcp.TopstepX.Tests.Venue;

public sealed class ExecutionTransmitFaultTests
{
    [Fact]
    public void MapOrRethrow_Venue408AfterTransmit_ReturnsOutcomeUnknown()
    {
        VenueException timeout = new("request timed out", errorCode: 408);

        ExecutionWriteResult result = ExecutionTransmitFault.MapOrRethrow(
            timeout,
            transmitted: true,
            CancellationToken.None,
            NullLogger.Instance);

        result.Outcome.Should().Be(ExecutionWriteOutcome.TransmittedOutcomeUnknown);
    }
}
