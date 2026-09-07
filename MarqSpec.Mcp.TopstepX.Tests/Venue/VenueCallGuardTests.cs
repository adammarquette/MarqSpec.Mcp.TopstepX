using System.Diagnostics;
using FluentAssertions;
using MarqSpec.Client.ProjectX.Exceptions;
using MarqSpec.Mcp.TopstepX.Telemetry;
using MarqSpec.Mcp.TopstepX.Venue;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;

namespace MarqSpec.Mcp.TopstepX.Tests.Venue;

/// <summary>
/// The single funnel every vendor call in <see cref="ProjectXMarketDataGateway"/> goes through: it translates
/// the vendor's failures into one exception type, and it is where a venue call becomes a number and a span
/// (gh#536).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the code path, driven directly.</b> The gateway itself cannot be built in the unit tier without
/// a fake <c>IProjectXApiClient</c>, and that interface carries the venue's whole order surface —
/// writing one would put those method names in this repository to test a counter, which is the opposite of
/// what ADR-0002 asks for. Extracting the funnel gives the same coverage over the same object the gateway
/// actually calls, with a plain delegate in place of the vendor.
/// </para>
/// <para>
/// <b>The translation behaviour is unchanged and is pinned here for the first time.</b> It used to be a
/// private <c>Guarded</c> local to the gateway, reachable from no test at all.
/// </para>
/// </remarks>
[Collection(HostTelemetryCollection.Name)]
public sealed class VenueCallGuardTests
{
    [Fact]
    public async Task ASuccessfulCallIsCountedAndTimedUnderItsOperation()
    {
        using HostTelemetry telemetry = new();
        VenueCallGuard guard = new(telemetry);

        using MetricCollector<long> calls = new(
            telemetry, HostTelemetry.Name, HostTelemetry.VenueCallsInstrument);
        using MetricCollector<double> duration = new(
            telemetry, HostTelemetry.Name, HostTelemetry.VenueCallDurationInstrument);

        int answer = await guard.RunAsync(
            VenueOperation.GetBars, () => Task.FromResult(7), "retrieving bars for a contract");

        answer.Should().Be(7);

        calls.LastMeasurement!.Value.Should().Be(1);
        calls.LastMeasurement.Tags[HostTelemetry.OperationTag].Should().Be(VenueOperation.GetBars);
        duration.LastMeasurement!.Tags[HostTelemetry.OperationTag].Should().Be(VenueOperation.GetBars);
    }

    [Fact]
    public async Task AVendorRefusalIsStillCounted_AndCarriesTheStatusCodeButNotTheVendorsMessage()
    {
        // A vendor allowance is spent by the REQUEST, so a refused call is a call that was issued. And the
        // vendor's own message string never travels: it is free text on a channel a language model reads
        // (ADR-0008), and the numeric code carries the diagnostic value without the surface.
        const string VendorText = "account 12345 is not entitled to CON.F.US.EP.Z26";

        using HostTelemetry telemetry = new();
        VenueCallGuard guard = new(telemetry);

        using MetricCollector<long> calls = new(
            telemetry, HostTelemetry.Name, HostTelemetry.VenueCallsInstrument);

        List<Activity> spans = [];
        using ActivityListener listener = Listen(spans);

        Func<Task> refused = () => guard.RunAsync<int>(
            VenueOperation.ResolveContracts,
            () => throw new ProjectXApiException(VendorText, 403),
            "searching contracts for ES");

        VenueException thrown = (await refused.Should().ThrowAsync<VenueException>()).Which;
        thrown.Message.Should().NotContain(VendorText);
        thrown.Message.Should().Contain("searching contracts for ES");

        calls.LastMeasurement!.Value.Should().Be(1);

        Activity span = spans.Should().ContainSingle().Subject;
        span.Status.Should().Be(ActivityStatusCode.Error);
        span.OperationName.Should().Be("venue." + VenueOperation.ResolveContracts);

        foreach (KeyValuePair<string, object?> tag in span.TagObjects)
        {
            tag.Value?.ToString().Should().NotContain(
                VendorText, "span attribute '{0}' carried vendor free text", tag.Key);
            tag.Value?.ToString().Should().NotContain(
                "CON.F.US.", "span attribute '{0}' carried a contract id", tag.Key);
        }
    }

    [Fact]
    public async Task AnUnreachableGatewayIsCounted_AndReportedAsUnreachableRatherThanRefused()
    {
        using HostTelemetry telemetry = new();
        VenueCallGuard guard = new(telemetry);

        using MetricCollector<long> calls = new(
            telemetry, HostTelemetry.Name, HostTelemetry.VenueCallsInstrument);

        Func<Task> unreachable = () => guard.RunAsync<int>(
            VenueOperation.GetAccounts,
            () => throw new HttpRequestException("no route to host"),
            "listing accounts");

        VenueException thrown = (await unreachable.Should().ThrowAsync<VenueException>()).Which;
        thrown.Message.Should().Contain("could not be reached");

        calls.LastMeasurement!.Value.Should().Be(1);
    }

    [Fact]
    public async Task ARejectedCredentialStillNamesTheUsernameAndKeyMixUp()
    {
        // The one message worth keeping verbatim, because the gateway reports the real cause as a bare
        // unknown error and an operator cannot get there from the wire.
        using HostTelemetry telemetry = new();
        VenueCallGuard guard = new(telemetry);

        Func<Task> rejected = () => guard.RunAsync<int>(
            VenueOperation.GetAccounts,
            () => throw new AuthenticationException("nope"),
            "listing accounts");

        VenueException thrown = (await rejected.Should().ThrowAsync<VenueException>()).Which;
        thrown.Message.Should().Contain("ProjectX__ApiKey is the USERNAME");
    }

    [Fact]
    public void EveryOperationTheGatewayNamesIsOneTheVocabularyKnows()
    {
        // The `operation` tag is a storage key in every backend already scraping it. A gateway method that
        // invented its own string would start a series no dashboard names and no reviewer priced, so the
        // vocabulary is the closed list and this is what keeps it closed.
        VenueOperation.All.Should().OnlyHaveUniqueItems();
        VenueOperation.All.Should().HaveCount(7,
            "one per read on IMarketDataGateway — a new read adds a value here in the same change");
    }

    private static ActivityListener Listen(List<Activity> into)
    {
        ActivityListener listener = new()
        {
            ShouldListenTo = candidate => candidate.Name == HostTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> options) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = into.Add,
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
